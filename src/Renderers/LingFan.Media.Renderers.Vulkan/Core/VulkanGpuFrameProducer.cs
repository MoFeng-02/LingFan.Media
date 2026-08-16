using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan 零拷贝帧生产者：把解码后端原生输出（Windows D3D11 共享句柄 / Linux VAAPI dma_buf）
/// 经 Vulkan 外部内存导入为 VkImage（<see cref="VulkanImageResource"/>），实现零拷贝上屏。
/// </summary>
/// <remarks>
/// <para>注册为 <see cref="IGpuFrameProducer"/>，供解码器经中立桥调用，严守依赖倒置（后端不感知 Vulkan 绑定细节）。</para>
/// <para><b>导入机制</b>：创建带 VK_KHR_external_memory 的 VkImage，经 vkAllocateMemory +
/// <c>ImportMemoryWin32HandleInfoKHR</c>（Windows）/ <c>ImportMemoryFdInfoKHR</c>（Linux）绑定原生共享句柄，
/// vkBindImageMemory 后即为零拷贝 Vulkan 纹理，由 <see cref="VulkanRenderer.BlitVulkanImageResource"/> 直接 blit 到 SwapChain。</para>
/// <para><b>能力自报 + 行为副作用双判据（S_OK≠被接受）</b>：扩展不可用 / 句柄无效 / 导入失败 →
/// <see cref="TryImport"/> 返回 <see langword="false"/>，调用方回落软解并计 [FRAMEPATH] 统计，绝不报"已就绪"假绿。</para>
/// <para><b>AOT</b>：VulkanNative 为零反射绑定（vkGetDeviceProcAddr 解析外部内存函数指针）；
/// 无 [DllImport]/[ComImport]/反射；跨平台经 OperatingSystem.IsXxx() 运行时分发，无 #if。</para>
/// <para><b>v1 范围</b>：Windows(D3D11→VK) + Linux(VAAPI→VK)。Android(AHardwareBuffer)/Apple(IOSurface) 为后续端点，
/// 当前返回 false（调用方回落软解），不阻断播放。</para>
    /// <para><b>多切片</b>：按 <see cref="GpuFrameImportSource.ArrayLayers"/> 创建整数组 VkImage，并据
    /// <see cref="GpuFrameImportSource.SubresourceIndex"/> 在 <see cref="VulkanRenderer.BlitVulkanImageResource"/>
    /// 选 <c>baseArrayLayer</c>，正确处理 D3D11VA 纹理数组（切片索引=avFrame-&gt;data[1]）。</para>
/// <para><b>句柄所有权契约（单一责任人）</b>：原生共享句柄（NT HANDLE / dma_buf fd）的所有权自
/// <see cref="TryImport"/> 调用起转移至本生产者；无论导入成功或失败，生产者均在返回前
/// 经 <c>CloseHandle</c>（NT HANDLE）/ close（fd）关闭句柄（导入成功后资源引用已由 VkImage/VkDeviceMemory 持有，
/// 关闭句柄不销毁资源）。调用方（解码器）导出句柄后<b>不得</b>再关闭，避免双关。</para>
/// </remarks>
public sealed partial class VulkanGpuFrameProducer : IGpuFrameProducer, IDisposable
{
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly ILogger? _logger;

    // NV12 导入转码资源（惰性创建，随生产者生命周期在 Dispose 释放；device/physicalDevice 为共享、不拥有）
    private VulkanNv12ToRgbaConverter? _converter;
    private CommandPool _commandPool;          // 默认 Handle==0：未创建
    private Queue _graphicsQueue;             // 默认 Handle==0：未创建
    private CommandBuffer _cmdBuffer;          // 默认 Handle==0：未创建
    private uint _graphicsQueueFamily = uint.MaxValue;

    // 零拷贝导入诊断：仅首帧打印具体失败步骤 + Vulkan Result 码，避免逐帧刷屏（详见 FFmpeg 侧 warn 同步定位）。
    private int _diagRemain = 3;

    /// <inheritdoc/>
    public GPUApiType ApiType => GPUApiType.Vulkan;

    public VulkanGpuFrameProducer(Device device, PhysicalDevice physicalDevice, ILogger? logger = null)
    {
        _device = device;
        _physicalDevice = physicalDevice;
        _logger = logger;
    }

    /// <inheritdoc/>
    public unsafe bool TryImport(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;
        try
        {
            if (source.Handle == IntPtr.Zero || source.Width <= 0 || source.Height <= 0)
                return false;

            // NV12 外部纹理（VAAPI·Android NV12 / D3D11VA NV12 共享句柄）：导入为 NV12 VkImage
            // → GPU 内转 RGBA（零 CPU 拷贝）→ 交付 RGBA VulkanImageResource。须先于 RGBA 分支。
            if (source.Format == PixelFormat.NV12)
                return TryImportNv12(source, out texture);

            if (OperatingSystem.IsWindows() && source.Kind == GpuFrameImportKind.D3D11SharedHandle)
                return TryImportWin32(source, out texture);
            if (OperatingSystem.IsLinux() && source.Kind == GpuFrameImportKind.LinuxDmaBufFd)
                return TryImportLinux(source, out texture);

            // Android / iOS：后续端点（AHardwareBuffer / IOSurface），当前回落软解。
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Vulkan 零拷贝导入失败，回落软件解码（S_OK≠被接受：导入行为副作用未成立）。");
            texture?.Dispose();
            texture = null;
            return false;
        }
    }

    /// <summary>关闭 DXGI 共享 NT 句柄（导入完成/失败后由生产者负责关闭，防内核句柄泄漏）。</summary>
    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    private unsafe bool TryImportWin32(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;
        if (!TryMapFormat(source.Format, out Format vkFormat))
        {
            if (_diagRemain > 0) { _diagRemain--; _logger?.LogWarning($"[VKFDIAG] TryMapFormat 失败: fmt={source.Format}"); }
            return false;
        }

        var extMem = new ExternalMemoryImageCreateInfo
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
        };
        var ci = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = vkFormat,
            Extent = new Extent3D((uint)source.Width, (uint)source.Height, 1),
            MipLevels = 1,
            ArrayLayers = (uint)Math.Max(1, source.ArrayLayers),
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        ExternalMemoryImageCreateInfo* pExt = &extMem;
        ci.PNext = pExt;

        Image image;
        Result createResult = VulkanNative.CreateImage(_device, &ci, null, out image);
        if (createResult != Result.Success)
        {
            if (_diagRemain > 0) { _diagRemain--; _logger?.LogWarning($"[VKFDIAG] vkCreateImage 失败: {createResult} (fmt={vkFormat}, w={source.Width} h={source.Height} handle={(long)source.Handle:X})"); }
            CloseHandle(source.Handle);
            return false;
        }

        try
        {
            var req = QueryImageMemoryRequirements(image);

            var imp = new ImportMemoryWin32HandleInfoKHR
            {
                SType = StructureType.ImportMemoryWin32HandleInfoKhr,
                HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
                Handle = source.Handle,
            };
            // D3D11 共享纹理为 dedicated 分配；导入 Vulkan 必须显式声明 VkMemoryDedicatedAllocateInfo，
            // 否则 vkBindImageMemory 因内存非 dedicated 而失败（零拷贝未接受、回落软解 OOM）。
            // NVIDIA 即使 requirements 未标 requires 也强制 dedicated，故 Win32 恒声明。
            var dedicated = new MemoryDedicatedAllocateInfo
            {
                SType = StructureType.MemoryDedicatedAllocateInfo,
                Image = image,
            };
            imp.PNext = &dedicated;
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = &imp,
                AllocationSize = req.Size,
                MemoryTypeIndex = ExternalCompatibleMemoryType(req.MemoryTypeBits),
            };

            DeviceMemory memory;
            Result allocResult = VulkanNative.AllocateMemory(_device, &ai, null, out memory);
            if (allocResult != Result.Success)
            {
                if (_diagRemain > 0)
                {
                    _diagRemain--;
                    _logger?.LogWarning($"[VKFDIAG] vkAllocateMemory 失败: {allocResult} | v2={VulkanNative.HasImageMemoryRequirements2} size={req.Size} memTypeBits=0x{req.MemoryTypeBits:X} reqDed={req.RequiresDedicated} 选用memType={ai.MemoryTypeIndex} flags=0x{GetMemoryTypeFlags(req.MemoryTypeBits, ai.MemoryTypeIndex):X} vkGpu={GetDeviceName()} handle={(long)source.Handle:X}");
                }
                VulkanNative.DestroyImage(_device, image, null);
                CloseHandle(source.Handle);
                return false;
            }

            Result bindResult = VulkanNative.BindImageMemory(_device, image, memory, 0);
            if (bindResult != Result.Success)
            {
                if (_diagRemain > 0) { _diagRemain--; _logger?.LogWarning($"[VKFDIAG] vkBindImageMemory 失败: {bindResult} (handle={(long)source.Handle:X})"); }
                VulkanNative.FreeMemory(_device, memory, null);
                VulkanNative.DestroyImage(_device, image, null);
                CloseHandle(source.Handle);
                return false;
            }

            // NT 共享句柄：vkAllocateMemory 已把句柄导入为 VkDeviceMemory（独立引用），此处关闭句柄不销毁资源，防内核句柄泄漏。
            // D3D11 共享纹理真实格式为 B8G8R8A8_UNORM（BGRA 字节序），交付 BGRA32 使 Blit 路径
            // srcVkFormat=B8G8R8A8Unorm 与 VkImage/swapchain 一致，避免 R/B 通道偏蓝。
            texture = new VulkanImageResource(_device, image, memory,
                source.Width, source.Height, PixelFormat.BGRA32, source.SubresourceIndex, ImageLayout.Undefined);
            CloseHandle(source.Handle);
            return true;
        }
        catch
        {
            VulkanNative.DestroyImage(_device, image, null);
            CloseHandle(source.Handle);
            throw;
        }
    }

    private unsafe bool TryImportLinux(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;
        if (!TryMapFormat(source.Format, out Format vkFormat))
            return false;

        var extMem = new ExternalMemoryImageCreateInfo
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.DmaBufBitExt,
        };
        var ci = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = vkFormat,
            Extent = new Extent3D((uint)source.Width, (uint)source.Height, 1),
            MipLevels = 1,
            ArrayLayers = (uint)Math.Max(1, source.ArrayLayers),
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        ExternalMemoryImageCreateInfo* pExt = &extMem;
        ci.PNext = pExt;

        Image image;
        if (VulkanNative.CreateImage(_device, &ci, null, out image) != Result.Success)
            return false;

        try
        {
            var req = QueryImageMemoryRequirements(image);

            var imp = new ImportMemoryFdInfoKHR
            {
                SType = StructureType.ImportMemoryFDInfoKhr,
                HandleType = ExternalMemoryHandleTypeFlags.DmaBufBitExt,
                Fd = (int)source.Handle,
            };
            MemoryDedicatedAllocateInfo dedicated = new()
            {
                SType = StructureType.MemoryDedicatedAllocateInfo,
                Image = image,
            };
            if (req.RequiresDedicated)
                imp.PNext = &dedicated;
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = &imp,
                AllocationSize = req.Size,
                MemoryTypeIndex = ExternalCompatibleMemoryType(req.MemoryTypeBits),
            };

            DeviceMemory memory;
            if (VulkanNative.AllocateMemory(_device, &ai, null, out memory) != Result.Success)
            {
                VulkanNative.DestroyImage(_device, image, null);
                CloseFd((int)source.Handle); // 失败出口：fd 未导入，须关闭防泄漏（单一责任人）
                return false;
            }

            if (VulkanNative.BindImageMemory(_device, image, memory, 0) != Result.Success)
            {
                VulkanNative.FreeMemory(_device, memory, null);
                VulkanNative.DestroyImage(_device, image, null);
                CloseFd((int)source.Handle); // 失败出口：fd 未导入，须关闭防泄漏（单一责任人）
                return false;
            }

            texture = new VulkanImageResource(_device, image, memory,
                source.Width, source.Height, source.Format, source.SubresourceIndex, ImageLayout.Undefined);
            CloseFd((int)source.Handle); // 成功出口：导入消费 fd，关闭不销毁 dma_buf（VkDeviceMemory 持引用），防 fd 泄漏
            return true;
        }
        catch
        {
            VulkanNative.DestroyImage(_device, image, null);
            throw;
        }
    }

    /// <summary>
    /// NV12 外部纹理零拷贝导入：把解码后端输出的 NV12 原生句柄（Windows D3D11 共享 / Linux VAAPI dma_buf）
    /// 经 Vulkan 外部内存导入为 NV12 VkImage，再用中性 <see cref="VulkanNv12ToRgbaConverter"/> 在 GPU 内转 RGBA，
    /// 交付 RGBA <see cref="VulkanImageResource"/> 供渲染器 blit 上屏（零 CPU 回读）。
    /// </summary>
    /// <remarks>
    /// <para>RGBA 目标由本方法创建并所有权转移给调用方（经 <see cref="VulkanImageResource"/> 释放）；
    /// NV12 外部图像转码后即销毁并关闭原生句柄（单一责任人）。转换器/命令池/图形队列惰性创建，随 <see cref="Dispose"/> 释放。</para>
    /// <para>句柄所有权契约（单一责任人）：无论成功/失败，本方法在返回前关闭原生句柄（NT HANDLE / fd），防内核泄漏。</para>
    /// </remarks>
    private unsafe bool TryImportNv12(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;

        bool isWindows = OperatingSystem.IsWindows();
        ExternalMemoryHandleTypeFlags handleType = isWindows
            ? ExternalMemoryHandleTypeFlags.D3D11TextureBit
            : ExternalMemoryHandleTypeFlags.DmaBufBitExt;

        // 1) 建 NV12 外部 VkImage（多平面 G8B8R82Plane420Unorm，仅采样用法）
        var extMem = new ExternalMemoryImageCreateInfo
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = handleType,
        };
        var ci = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.G8B8R82Plane420Unorm,
            Extent = new Extent3D((uint)source.Width, (uint)source.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        ExternalMemoryImageCreateInfo* pExt = &extMem;
        ci.PNext = pExt;

        Image nv12Image;
        if (VulkanNative.CreateImage(_device, &ci, null, out nv12Image) != Result.Success)
        {
            CloseHandleIfNeeded(source, isWindows);
            return false;
        }

        DeviceMemory nv12Memory = default;
        (ImageView Y, ImageView UV) planeViews = default;
        Image rgbaImage = default;
        DeviceMemory rgbaMemory = default;
        ImageView rgbaView = default;
        bool imported = false, rgbaBuilt = false;

        try
        {
            var req = QueryImageMemoryRequirements(nv12Image);

            if (isWindows)
            {
                var imp = new ImportMemoryWin32HandleInfoKHR
                {
                    SType = StructureType.ImportMemoryWin32HandleInfoKhr,
                    HandleType = handleType,
                    Handle = source.Handle,
                };
                var ai = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    PNext = &imp,
                    AllocationSize = req.Size,
                    MemoryTypeIndex = ExternalCompatibleMemoryType(req.MemoryTypeBits),
                };
                if (VulkanNative.AllocateMemory(_device, &ai, null, out nv12Memory) != Result.Success)
                {
                    CloseHandle(source.Handle);
                    return false;
                }
            }
            else
            {
                var imp = new ImportMemoryFdInfoKHR
                {
                    SType = StructureType.ImportMemoryFDInfoKhr,
                    HandleType = handleType,
                    Fd = (int)source.Handle,
                };
                var ai = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    PNext = (void*)&imp,
                    AllocationSize = req.Size,
                    MemoryTypeIndex = ExternalCompatibleMemoryType(req.MemoryTypeBits),
                };
                if (VulkanNative.AllocateMemory(_device, &ai, null, out nv12Memory) != Result.Success)
                {
                    VulkanNative.DestroyImage(_device, nv12Image, null);
                    CloseHandleIfNeeded(source, isWindows); // 失败出口：fd/句柄未导入，须关闭防泄漏
                    return false;
                }
            }
            imported = true;

            if (VulkanNative.BindImageMemory(_device, nv12Image, nv12Memory, 0) != Result.Success)
            {
                VulkanNative.FreeMemory(_device, nv12Memory, null);
                CloseHandleIfNeeded(source, isWindows);
                return false;
            }

            // 2) 建 RGBA 目标（内部 Vulkan 图像，交付给调用方拥有）
            Format rgbaFormat = Format.B8G8R8A8Unorm;
            if (!TryCreateRgbaTarget((uint)source.Width, (uint)source.Height, rgbaFormat, out rgbaImage, out rgbaMemory, out rgbaView))
            {
                VulkanNative.DestroyImage(_device, nv12Image, null);
                VulkanNative.FreeMemory(_device, nv12Memory, null);
                CloseHandleIfNeeded(source, isWindows);
                return false;
            }
            rgbaBuilt = true;

            // 3) 记录转换命令 + 提交 + 等待（NV12 平面视图须存活至提交完成）
            if (!EnsureConvertResources() ||
                !TryConvertNv12(nv12Image, ref planeViews, rgbaImage, rgbaView, (uint)source.Width, (uint)source.Height, rgbaFormat))
            {
                if (planeViews.Y.Handle != 0 || planeViews.UV.Handle != 0) _converter?.DestroyPlaneViews(planeViews);
                ReleaseRgbaTarget(rgbaImage, rgbaMemory, rgbaView);
                rgbaBuilt = false;
                VulkanNative.DestroyImage(_device, nv12Image, null);
                VulkanNative.FreeMemory(_device, nv12Memory, null);
                CloseHandleIfNeeded(source, isWindows);
                return false;
            }

            // 4) 清理 NV12 外部图像（转码已完成，RGBA 已独立）并关闭原生句柄（单一责任人）
            _converter!.DestroyPlaneViews(planeViews);
            VulkanNative.DestroyImage(_device, nv12Image, null);
            VulkanNative.FreeMemory(_device, nv12Memory, null);
            CloseHandleIfNeeded(source, isWindows);

            // 5) 交付 RGBA 资源（所有权转移给调用方；当前处 TransferSrcOptimal，供渲染器 blit）
            texture = new VulkanImageResource(_device, rgbaImage, rgbaMemory,
                source.Width, source.Height, PixelFormat.BGRA32, 0, ImageLayout.TransferSrcOptimal);
            return true;
        }
        catch
        {
            if (planeViews.Y.Handle != 0 || planeViews.UV.Handle != 0) _converter?.DestroyPlaneViews(planeViews);
            if (rgbaBuilt) ReleaseRgbaTarget(rgbaImage, rgbaMemory, rgbaView);
            if (imported) VulkanNative.FreeMemory(_device, nv12Memory, null);
            VulkanNative.DestroyImage(_device, nv12Image, null);
            CloseHandleIfNeeded(source, isWindows);
            throw;
        }
    }

    /// <summary>建 RGBA 目标 VkImage（ColorAttachmentBit | TransferSrcBit）+ 设备内存 + 视图；失败清理并返回 false。</summary>
    private unsafe bool TryCreateRgbaTarget(uint width, uint height, Format format, out Image image, out DeviceMemory memory, out ImageView view)
    {
        image = default; memory = default; view = default;

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        if (VulkanNative.CreateImage(_device, &imageInfo, null, out image) != Result.Success)
            return false;

        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, image, &memReq);
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits),
        };
        if (VulkanNative.AllocateMemory(_device, &allocInfo, null, out memory) != Result.Success)
        {
            VulkanNative.DestroyImage(_device, image, null);
            image = default;
            return false;
        }
        if (VulkanNative.BindImageMemory(_device, image, memory, 0) != Result.Success)
        {
            VulkanNative.FreeMemory(_device, memory, null);
            VulkanNative.DestroyImage(_device, image, null);
            image = default; memory = default;
            return false;
        }

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        if (VulkanNative.CreateImageView(_device, &viewInfo, null, out view) != Result.Success)
        {
            VulkanNative.DestroyImage(_device, image, null);
            VulkanNative.FreeMemory(_device, memory, null);
            image = default; memory = default; view = default;
            return false;
        }
        return true;
    }

    /// <summary>销毁 RGBA 目标（视图 + 图像 + 内存）。</summary>
    private unsafe void ReleaseRgbaTarget(Image image, DeviceMemory memory, ImageView view)
    {
        if (view.Handle != 0) VulkanNative.DestroyImageView(_device, view, null);
        if (image.Handle != 0) VulkanNative.DestroyImage(_device, image, null);
        if (memory.Handle != 0) VulkanNative.FreeMemory(_device, memory, null);
    }

    /// <summary>惰性创建 NV12 转码资源：中性转换器 + 图形队列命令池/命令缓冲（单次）。失败返回 false。</summary>
    private unsafe bool EnsureConvertResources()
    {
        if (_converter is null)
            _converter = new VulkanNv12ToRgbaConverter(_device, _physicalDevice, _logger);

        if (_commandPool.Handle != 0)
            return true;

        // 查找图形队列族
        uint count = 0;
        VulkanNative.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref count, null);
        if (count == 0) return false;
        var props = stackalloc QueueFamilyProperties[(int)count];
        VulkanNative.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref count, props);
        uint gq = uint.MaxValue;
        for (uint i = 0; i < count; i++)
            if ((props[(int)i].QueueFlags & QueueFlags.GraphicsBit) != 0) { gq = i; break; }
        if (gq == uint.MaxValue) return false;
        _graphicsQueueFamily = gq;
        VulkanNative.GetDeviceQueue(_device, gq, 0, out _graphicsQueue);

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = gq,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        if (VulkanNative.CreateCommandPool(_device, ref poolInfo, null, out _commandPool) != Result.Success)
            return false;

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer cb;
        if (VulkanNative.AllocateCommandBuffers(_device, ref allocInfo, &cb) != Result.Success)
        {
            VulkanNative.DestroyCommandPool(_device, _commandPool, null);
            _commandPool = default;
            return false;
        }
        _cmdBuffer = cb;
        return true;
    }

    /// <summary>
    /// 在一次性命令缓冲内记录 NV12→RGBA 转换，提交并等待 GPU 完成。NV12 平面视图须于提交完成后经
    /// <see cref="VulkanNv12ToRgbaConverter.DestroyPlaneViews"/> 销毁。
    /// </summary>
    private unsafe bool TryConvertNv12(Image nv12Image, ref (ImageView Y, ImageView UV) planeViews, Image rgbaImage, ImageView rgbaView, uint width, uint height, Format targetFormat)
    {
        planeViews = _converter!.CreatePlaneViews(nv12Image);

        VulkanNative.ResetCommandBuffer(_cmdBuffer, CommandBufferResetFlags.None);
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        if (VulkanNative.BeginCommandBuffer(_cmdBuffer, ref beginInfo) != Result.Success)
            return false;

        try
        {
            _converter.Convert(_cmdBuffer, nv12Image, planeViews, ImageLayout.Undefined, width, height, targetFormat, rgbaImage, rgbaView);
        }
        catch
        {
            // 把命令缓冲移出 recording 状态（参考渲染器异常恢复手法），避免 ResetCommandBuffer 返回 VK_NOT_READY
            VulkanNative.EndCommandBuffer(_cmdBuffer);
            return false;
        }

        if (VulkanNative.EndCommandBuffer(_cmdBuffer) != Result.Success)
            return false;

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
        };
        CommandBuffer cb = _cmdBuffer;
        submitInfo.PCommandBuffers = &cb;
        if (VulkanNative.QueueSubmit(_graphicsQueue, 1, &submitInfo, IntPtr.Zero) != Result.Success)
            return false;
        if (VulkanNative.QueueWaitIdle(_graphicsQueue) != Result.Success)
            return false;
        return true;
    }

    /// <summary>关闭原生共享句柄（单一责任人）。Windows 关 NT HANDLE；Linux 关 dma_buf fd。</summary>
    private static void CloseHandleIfNeeded(GpuFrameImportSource source, bool isWindows)
    {
        if (isWindows)
            CloseHandle(source.Handle);
        else
            CloseFd((int)source.Handle);
    }

    /// <summary>关闭 Linux dma_buf 文件描述符（导入完成后由导入方负责关闭，防 fd 泄漏）。</summary>
    [LibraryImport("libc")]
    private static partial int close(int fd);

    private static void CloseFd(int fd)
    {
        if (fd >= 0) _ = close(fd);
    }

    private static bool TryMapFormat(PixelFormat format, out Format vkFormat)
    {
        switch (format)
        {
            // D3D11 共享纹理恒为 B8G8R8A8_UNORM（见 D3D11Nv12ToRgbaConverter 输出格式）。ffmpeg 导出端标记
            // RGBA32，但真实 DXGI 格式为 B8G8R8A8_UNORM（BGRA 字节序）；故 RGBA32 与 BGRA32 均须映射为
            // B8G8R8A8Unorm 以匹配共享纹理，否则 vkBindImageMemory 因格式不匹配而失败（零拷贝未接受）。
            case PixelFormat.BGRA32:
            case PixelFormat.RGBA32: vkFormat = Format.B8G8R8A8Unorm; return true;
            default: vkFormat = default; return false;
        }
    }

    private unsafe uint FindMemoryType(uint memoryTypeBits)
    {
        PhysicalDeviceMemoryProperties props;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &props);
        for (uint i = 0; i < props.MemoryTypeCount; i++)
        {
            if ((memoryTypeBits & (1u << (int)i)) != 0 &&
                (props.MemoryTypes[(int)i].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
            {
                return i;
            }
        }
        // 兜底：任意满足 memoryTypeBits 的类型
        for (uint i = 0; i < props.MemoryTypeCount; i++)
            if ((memoryTypeBits & (1u << (int)i)) != 0)
                return i;
        return 0;
    }

    /// <summary>
    /// 外部内存导入专用内存类型选择（D3D11/dma_buf 共享纹理 → Vulkan VkImage）。
    /// <para><b>为何不能复用 <see cref="FindMemoryType"/> 的 DeviceLocalBit 过滤</b>：外部导入纹理的
    /// <c>memoryTypeBits</c> 已由驱动编码「与导入句柄真正兼容」的类型集合；NVIDIA 上该集合内的类型未必带
    /// <c>VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT</c> 标志位（其 heapIndex 虽指向设备本地堆）。若再按
    /// DeviceLocalBit 过滤会漏掉唯一兼容类型、兜底选到不兼容类型，导致 vkAllocateMemory 把导入误判为真实分配
    /// → <c>VK_ERROR_OUT_OF_DEVICE_MEMORY</c>（Khronos 论坛同名案例）。</para>
    /// <para>策略：优先 memoryTypeBits 中带 DeviceLocalBit 的置位（若设备本地堆确实带标志位），否则直接取
    /// 最低置位（驱动已保证兼容）。绝不再附加 DeviceLocalBit 硬过滤。</para>
    /// </summary>
    private unsafe uint ExternalCompatibleMemoryType(uint memoryTypeBits)
    {
        PhysicalDeviceMemoryProperties props;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &props);
        uint fallback = uint.MaxValue;
        for (uint i = 0; i < props.MemoryTypeCount; i++)
        {
            if ((memoryTypeBits & (1u << (int)i)) == 0) continue;
            if (fallback == uint.MaxValue) fallback = i;
            if ((props.MemoryTypes[(int)i].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
                return i;
        }
        return fallback == uint.MaxValue ? 0u : fallback;
    }

    /// <summary>取 memoryTypeBits 中第 <paramref name="index"/> 个类型的属性标志（诊断用）。</summary>
    private unsafe uint GetMemoryTypeFlags(uint memoryTypeBits, uint index)
    {
        if (index >= 32) return 0;
        PhysicalDeviceMemoryProperties props;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &props);
        if (index >= props.MemoryTypeCount) return 0;
        return (uint)props.MemoryTypes[(int)index].PropertyFlags;
    }

    /// <summary>取 Vulkan 物理设备名（诊断多 GPU 选卡：须与 D3D11VA 解码设备同 GPU 才能导入 D3D11 共享纹理）。</summary>
    private unsafe string GetDeviceName()
    {
        PhysicalDeviceProperties props;
        VulkanNative.GetPhysicalDeviceProperties(_physicalDevice, &props);
        return Marshal.PtrToStringAnsi((nint)props.DeviceName) ?? "(unknown)";
    }

    /// <summary>
    /// 取 image 的内存需求。外部内存导入纹理优先用 vkGetImageMemoryRequirements2 + VkMemoryDedicatedRequirements
    /// 取权威 size / memoryTypeBits / dedicated 标志；Vulkan 1.0 未提供 v2 时回退 v1。
    /// <para><b>为何必须 v2</b>：带 VK_EXTERNAL_MEMORY 的 image，v1 vkGetImageMemoryRequirements 返回的
    /// memoryTypeBits / size 在导入场景下不可靠（NVIDIA 上尤甚）——v1 的 memoryTypeBits 常不含与导入句柄
    /// 真正兼容的设备本地内存类型，导致 vkAllocateMemory 误判为真实分配而返回 ErrorOutOfDeviceMemory。</para>
    /// </summary>
    private unsafe (ulong Size, uint MemoryTypeBits, bool RequiresDedicated) QueryImageMemoryRequirements(Image image)
    {
        if (VulkanNative.HasImageMemoryRequirements2)
        {
            ImageMemoryRequirementsInfo2 reqInfo2 = new()
            {
                SType = StructureType.ImageMemoryRequirementsInfo2,
                Image = image,
            };
            MemoryDedicatedRequirements dedicatedReq = new()
            {
                SType = StructureType.MemoryDedicatedRequirements,
            };
            reqInfo2.PNext = &dedicatedReq;
            MemoryRequirements2 memReq2 = new()
            {
                SType = StructureType.MemoryRequirements2,
            };
            VulkanNative.GetImageMemoryRequirements2(_device, &reqInfo2, &memReq2);
            return (memReq2.MemoryRequirements.Size, memReq2.MemoryRequirements.MemoryTypeBits,
                dedicatedReq.RequiresDedicatedAllocation || dedicatedReq.PrefersDedicatedAllocation);
        }
        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, image, &memReq);
        return (memReq.Size, memReq.MemoryTypeBits, false);
    }

    /// <summary>
    /// 释放 NV12 转码资源（命令池 + 中性转换器）。device/physicalDevice 为共享引用，不在此销毁。
    /// 由 DI 容器在 Singleton 生命周期结束时调用（接口 <see cref="IGpuFrameProducer"/> 不继承 <see cref="IDisposable"/>，
    /// 但具体类型实现后容器仍会释放）。
    /// </summary>
    public unsafe void Dispose()
    {
        if (_commandPool.Handle != 0)
        {
            VulkanNative.DestroyCommandPool(_device, _commandPool, null);
            _commandPool = default;
        }
        _converter?.Dispose();
        _converter = null;
    }
}

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
/// <see cref="TryImport"/> 返回 <see langword="false"/>，调用方回落软解并计入 CPU 拷贝统计，绝不报"已就绪"假绿。</para>
/// <para><b>AOT</b>：VulkanNative 为零反射绑定（vkGetDeviceProcAddr 解析外部内存函数指针）；
/// 无 [DllImport]/[ComImport]/反射；跨平台经 OperatingSystem.IsXxx() 运行时分发，无 #if。</para>
/// <para><b>v1 范围</b>：Windows(D3D11→VK) + Linux(VAAPI→VK) + Android(AHB→VK，外部格式 YCbCr 采样转换)。
/// Apple(IOSurface) 为后续端点，当前返回 false（调用方回落软解），不阻断播放。</para>
    /// <para><b>多切片</b>：按 <see cref="GpuFrameImportSource.ArrayLayers"/> 创建整数组 VkImage，并据
    /// <see cref="GpuFrameImportSource.SubresourceIndex"/> 在 <see cref="VulkanRenderer.BlitVulkanImageResource"/>
    /// 选 <c>baseArrayLayer</c>，正确处理 D3D11VA 纹理数组（切片索引=avFrame-&gt;data[1]）。</para>
/// <para><b>句柄所有权契约（单一责任人）</b>：原生共享句柄（NT HANDLE / dma_buf fd）的所有权自
/// <see cref="TryImport"/> 调用起转移至本生产者；无论导入成功或失败，生产者均在返回前
/// 经 <c>CloseHandle</c>（NT HANDLE）/ close（fd）关闭句柄（导入成功后资源引用已由 VkImage/VkDeviceMemory 持有，
/// 关闭句柄不销毁资源）。调用方（解码器）导出句柄后<b>不得</b>再关闭，避免双关。
/// Android AHardwareBuffer 为<b>例外</b>：AHB 指针由调用方借用（不转移所有权），生产者不 acquire/release/关闭，
/// 调用方在 TryImport 返回后自行释放 AImage/AHB 引用（详见 <c>TryImportAndroidAHardwareBuffer</c>）。</para>
/// </remarks>
public sealed unsafe partial class VulkanGpuFrameProducer : IGpuFrameProducer, IDisposable
{
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly ILogger? _logger;

    // NV12 导入转码资源（惰性创建，随生产者生命周期在 Dispose 释放；device/physicalDevice 为共享、不拥有）
    private VulkanNv12ToRgbaConverter? _converter;
    // Android AHB（外部格式 YCbCr）导入转码资源（惰性创建，随生产者生命周期在 Dispose 释放）
    private VulkanYcbcrToRgbaConverter? _ycbcrConverter;
    private CommandPool _commandPool;          // 默认 Handle==0：未创建
    private Queue _graphicsQueue;             // 默认 Handle==0：未创建
    private CommandBuffer _cmdBuffer;         // 默认 Handle==0：未创建
    private uint _graphicsQueueFamily = uint.MaxValue;

    // ── RGBA GPU→CPU 回读（Android Tier2：Skia 合成路径消费 GPU 帧）──
    // 独立命令池/命令缓冲/栅栏/staging buffer：与解码线程的 YCbCr/NV12 转换命令资源完全隔离。
    // 提交共用同一图形队列 —— VkQueue 非线程安全（vkQueueSubmit/QueueWaitIdle 须宿主同步），
    // 故与转换路径共享 _queueGate 串行化提交段（两段均为毫秒级，30fps 下无争用压力）。
    private readonly object _queueGate = new();
    private CommandPool _readbackCommandPool;
    private CommandBuffer _readbackCommandBuffer;
    private Fence _readbackFence;
    private Buffer _readbackStagingBuffer;
    private DeviceMemory _readbackStagingMemory;
    private void* _readbackStagingMapped;
    private ulong _readbackStagingSize;

    // 零拷贝导入诊断：仅首帧打印具体失败步骤 + Vulkan Result 码，避免逐帧刷屏（详见 FFmpeg 侧 warn 同步定位）。
    private int _diagRemain = 3;
    // AHB 首帧进入日志（与解码器侧 [AHB-TRACE] 括号定位原生崩溃的具体 vk 步骤）
    private bool _ahbEntryLogged;

    /// <inheritdoc/>
    public GPUApiType ApiType => GPUApiType.Vulkan;

    /// <summary>
    /// 预检导入能力（解码器据此决定是否启用对应零拷贝路径，如 Android Tier2 的 ImageReader Surface configure）。
    /// Android AHB 双判据：VK_ANDROID_external_memory_android_hardware_buffer 扩展已启用（函数可解析）
    /// <b>且</b> samplerYcbcrConversion 特性已在设备创建期启用（HasSamplerYcbcrConversion 双判据——
    /// 特性未启用时 YCbCr 采样属规范违规，驱动 UB 实测 SIGBUS，绝不可走）。
    /// </summary>
    public bool IsImportSupported(GpuFrameImportKind kind) => kind switch
    {
        GpuFrameImportKind.AndroidHardwareBuffer => OperatingSystem.IsAndroid()
            && VulkanNative.HasAndroidHardwareBufferProperties
            && VulkanNative.HasSamplerYcbcrConversion,
        GpuFrameImportKind.D3D11SharedHandle => OperatingSystem.IsWindows(),
        GpuFrameImportKind.LinuxDmaBufFd => OperatingSystem.IsLinux(),
        _ => false,
    };

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

            // Android AHB（YUV 外部格式）：先于 NV12 格式分支——AHB 句柄经
            // VK_ANDROID_external_memory_android_hardware_buffer 导入（UNDEFINED + externalFormat + YCbCr 采样），
            // 与共享句柄 / dma_buf 的 NV12 导入语义不同，不可落入 TryImportNv12。
            if (OperatingSystem.IsAndroid() && source.Kind == GpuFrameImportKind.AndroidHardwareBuffer)
                return TryImportAndroidAHardwareBuffer(source, out texture);

            // NV12 外部纹理（VAAPI·Android NV12 / D3D11VA NV12 共享句柄）：导入为 NV12 VkImage
            // → GPU 内转 RGBA（零 CPU 拷贝）→ 交付 RGBA VulkanImageResource。须先于 RGBA 分支。
            if (source.Format == PixelFormat.NV12)
                return TryImportNv12(source, out texture);

            if (OperatingSystem.IsWindows() && source.Kind == GpuFrameImportKind.D3D11SharedHandle)
                return TryImportWin32(source, out texture);
            if (OperatingSystem.IsLinux() && source.Kind == GpuFrameImportKind.LinuxDmaBufFd)
                return TryImportLinux(source, out texture);

            // iOS：后续端点（IOSurface），当前回落软解。
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

    /// <summary>
    /// Android AHardwareBuffer 零拷贝导入：把解码器产出的 AHB（YUV 外部格式）经
    /// <c>VK_ANDROID_external_memory_android_hardware_buffer</c> 导入为外部格式 VkImage（UNDEFINED + externalFormat，
    /// 仅 SAMPLED 用法），再经 <see cref="VulkanYcbcrToRgbaConverter"/> 用 YCbCr 采样器在 GPU 内转 RGBA，
    /// 交付 RGBA <see cref="VulkanImageResource"/> 供渲染器 blit 上屏（零 CPU 回读，「1 GPU hop」）。
    /// </summary>
    /// <remarks>
    /// <para><b>句柄所有权契约（与 Win32/fd 路径不同）</b>：AHB 指针由解码器<b>借用</b>给本方法——
    /// 导入为 dedicated VkDeviceMemory 后资源引用已由 Vulkan 持有，方法返回前即完成转换与 GPU 等待，
    /// 故本生产者不 acquire、不 release、不关闭 AHB 句柄；解码器在调用返回后自行释放 AImage/AHB 引用。</para>
    /// <para><b>导入失败</b>（扩展缺失 / externalFormat 无效 / 内存导入被拒）返回 <see langword="false"/>，
    /// 调用方回落软解，绝不报「已就绪」假绿。</para>
    /// </remarks>
    private unsafe bool TryImportAndroidAHardwareBuffer(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;

        // 扩展/函数指针自检（设备未启用 AHB 扩展或 YCbCr 转换不可用 → 诚实回落）。
        if (!VulkanNative.HasAndroidHardwareBufferProperties || !VulkanNative.HasSamplerYcbcrConversion)
        {
            if (_diagRemain > 0)
            {
                _diagRemain--;
                _logger?.LogWarning("[VKFDIAG] AHB 导入不可用：AHB 扩展或 samplerYcbcrConversion 函数未解析（设备未启用）");
            }
            return false;
        }

        // 首帧进入日志：此后崩溃即定位在 vkGetAndroidHardwareBufferPropertiesANDROID（与其后步骤）
        if (!_ahbEntryLogged)
        {
            _ahbEntryLogged = true;
            _logger?.LogInformation("[VKFDIAG] [AHB-TRACE] 首帧进入 TryImportAndroidAHardwareBuffer：ahb=0x{Ahb:X} {W}x{H}，即将 vkGetAndroidHardwareBufferPropertiesANDROID",
                source.Handle, source.Width, source.Height);
        }

        // 1) 查询 AHB 属性：内存（allocationSize/memoryTypeBits，AHB 导入的权威值）+ 格式（externalFormat/转换建议值）。
        AndroidHardwareBufferFormatPropertiesANDROID formatProps = new()
        {
            SType = StructureType.AndroidHardwareBufferFormatPropertiesAndroid,
        };
        AndroidHardwareBufferPropertiesANDROID props = new()
        {
            SType = StructureType.AndroidHardwareBufferPropertiesAndroid,
            PNext = &formatProps,
        };
        if (VulkanNative.GetAndroidHardwareBufferPropertiesANDROID(_device, source.Handle, &props) != Result.Success)
        {
            if (_diagRemain > 0) { _diagRemain--; _logger?.LogWarning("[VKFDIAG] vkGetAndroidHardwareBufferPropertiesANDROID 失败"); }
            return false;
        }
        // externalFormat==0 表示该 AHB 无外部格式（如有等价 VkFormat 的 RGBA AHB）——
        // 当前视频零拷贝路径仅覆盖 YUV 外部格式，诚实回落软解。
        if (formatProps.ExternalFormat == 0)
        {
            if (_diagRemain > 0)
            {
                _diagRemain--;
                _logger?.LogWarning("[VKFDIAG] AHB 无外部格式（externalFormat=0），当前仅支持 YUV AHB 导入");
            }
            return false;
        }

        // 2) 创建外部格式 VkImage：UNDEFINED + AHB 句柄类型 + externalFormat；外部格式图像仅允许 SAMPLED 用法。
        var extFormat = new ExternalFormatANDROID
        {
            SType = StructureType.ExternalFormatAndroid,
            ExternalFormat = formatProps.ExternalFormat,
        };
        var extMem = new ExternalMemoryImageCreateInfo
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.AndroidHardwareBufferBitAndroid,
            PNext = &extFormat,
        };
        var ci = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.Undefined,
            Extent = new Extent3D((uint)source.Width, (uint)source.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            PNext = &extMem,
        };

        Image image;
        if (VulkanNative.CreateImage(_device, &ci, null, out image) != Result.Success)
        {
            if (_diagRemain > 0) { _diagRemain--; _logger?.LogWarning("[VKFDIAG] vkCreateImage（AHB 外部格式）失败"); }
            return false;
        }

        DeviceMemory memory = default;
        ImageView ahbView = default;
        Image rgbaImage = default;
        DeviceMemory rgbaMemory = default;
        ImageView rgbaView = default;
        bool memoryBound = false, rgbaBuilt = false, viewCreated = false;
        try
        {
            // 3) dedicated 内存导入：AHB 导入强制 dedicated（VkMemoryDedicatedAllocateInfo 挂 ImportAndroidHardwareBufferInfoANDROID
            //    的 pNext），allocationSize/memoryTypeBits 以属性查询为权威（规范 VUID，勿用 image memory requirements）。
            // 【SIGBUS 根因】Buffer 字段承载 AHardwareBuffer* 的【值】——必须赋源指针本身（对齐 Windows 路径
            //   TryImportWin32 的 Handle = source.Handle）。此前误写 &ahbHandle 传的是栈局部变量地址，驱动
            //   vkAllocateMemory 导入时把栈地址当 AHardwareBuffer 解引用其引用计数 → BUS_ADRALN SIGBUS（真机实证）。
            var dedicated = new MemoryDedicatedAllocateInfo
            {
                SType = StructureType.MemoryDedicatedAllocateInfo,
                Image = image,
            };
            var imp = new ImportAndroidHardwareBufferInfoANDROID
            {
                SType = StructureType.ImportAndroidHardwareBufferInfoAndroid,
                // Buffer 字段类型 nint*（Silk.NET 把 struct AHardwareBuffer 的指针置为 nint*）。
                // 【SIGBUS 根因】应存 AHardwareBuffer* 的【值】= (nint*)source.Handle（bitcast AHB 指针本身）；
                // 此前误传 &ahbHandle（指向局部变量）→ 驱动把那栈地址当 AHardwareBuffer 解引用引用计数 →
                // BUS_ADRALN。对齐 Windows 路径 Handle=source.Handle 传值的语义。
                Buffer = (nint*)source.Handle,
                PNext = &dedicated,
            };
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = &imp,
                AllocationSize = props.AllocationSize,
                MemoryTypeIndex = ExternalCompatibleMemoryType(props.MemoryTypeBits),
            };
            if (VulkanNative.AllocateMemory(_device, &ai, null, out memory) != Result.Success)
            {
                if (_diagRemain > 0)
                {
                    _diagRemain--;
                    _logger?.LogWarning($"[VKFDIAG] vkAllocateMemory（AHB 导入）失败: size={props.AllocationSize} memTypeBits=0x{props.MemoryTypeBits:X} 选用memType={ai.MemoryTypeIndex}");
                }
                VulkanNative.DestroyImage(_device, image, null);
                return false;
            }

            if (VulkanNative.BindImageMemory(_device, image, memory, 0) != Result.Success)
            {
                if (_diagRemain > 0) { _diagRemain--; _logger?.LogWarning("[VKFDIAG] vkBindImageMemory（AHB 导入）失败"); }
                VulkanNative.FreeMemory(_device, memory, null);
                VulkanNative.DestroyImage(_device, image, null);
                return false;
            }
            memoryBound = true;

            // 4) RGBA 目标（内部 Vulkan 图像，交付给调用方拥有）
            Format rgbaFormat = Format.B8G8R8A8Unorm;
            if (!TryCreateRgbaTarget((uint)source.Width, (uint)source.Height, rgbaFormat, out rgbaImage, out rgbaMemory, out rgbaView))
            {
                VulkanNative.DestroyImage(_device, image, null);
                VulkanNative.FreeMemory(_device, memory, null);
                return false;
            }
            rgbaBuilt = true;

            // 5) YCbCr 转换管线 + 采样视图 + 命令记录/提交/等待
            //    提交段持 _queueGate：与回读路径（Skia 合成消费线程）共用图形队列，VkQueue 须宿主同步。
            lock (_queueGate)
            {
                _ycbcrConverter ??= new VulkanYcbcrToRgbaConverter(_device, _physicalDevice, _logger);
                if (!EnsureConvertResources())
                {
                    ReleaseRgbaTarget(rgbaImage, rgbaMemory, rgbaView);
                    rgbaBuilt = false;
                    VulkanNative.DestroyImage(_device, image, null);
                    VulkanNative.FreeMemory(_device, memory, null);
                    return false;
                }

                try
                {
                    _ycbcrConverter.EnsurePipeline(rgbaFormat, formatProps.ExternalFormat, formatProps);
                }
                catch (Exception ex)
                {
                    if (_diagRemain > 0) { _diagRemain--; _logger?.LogWarning(ex, "[VKFDIAG] YCbCr 转换管线构建失败"); }
                    ReleaseRgbaTarget(rgbaImage, rgbaMemory, rgbaView);
                    rgbaBuilt = false;
                    VulkanNative.DestroyImage(_device, image, null);
                    VulkanNative.FreeMemory(_device, memory, null);
                    return false;
                }

                ahbView = _ycbcrConverter.CreateImageView(image);
                viewCreated = true;

                if (!RecordAndSubmitAhbConversion(image, ahbView, rgbaImage, rgbaView, (uint)source.Width, (uint)source.Height, rgbaFormat))
                {
                    _ycbcrConverter.DestroyImageView(ahbView);
                    viewCreated = false;
                    ReleaseRgbaTarget(rgbaImage, rgbaMemory, rgbaView);
                    rgbaBuilt = false;
                    VulkanNative.DestroyImage(_device, image, null);
                    VulkanNative.FreeMemory(_device, memory, null);
                    return false;
                }

                // 6) 转换完成（GPU 已等待）：销毁瞬态 AHB 侧资源；RGBA 目标所有权转移给调用方。
                _ycbcrConverter.DestroyImageView(ahbView);
                viewCreated = false;
            }
            VulkanNative.DestroyImage(_device, image, null);
            VulkanNative.FreeMemory(_device, memory, null);

            texture = new VulkanImageResource(_device, rgbaImage, rgbaMemory,
                source.Width, source.Height, PixelFormat.BGRA32, 0, ImageLayout.TransferSrcOptimal,
                readback: r => ReadbackRgbaToCpu(r));
            return true;
        }
        catch
        {
            if (viewCreated) _ycbcrConverter?.DestroyImageView(ahbView);
            if (rgbaBuilt) ReleaseRgbaTarget(rgbaImage, rgbaMemory, rgbaView);
            if (memoryBound) VulkanNative.FreeMemory(_device, memory, null);
            VulkanNative.DestroyImage(_device, image, null);
            throw;
        }
    }

    /// <summary>
    /// 在一次性命令缓冲内记录 AHB 外部格式图像 → RGBA 的 YCbCr 采样转换，提交并等待 GPU 完成。
    /// 与 NV12 转码共用命令池/命令缓冲/图形队列（<see cref="EnsureConvertResources"/> 惰性创建）。
    /// </summary>
    private unsafe bool RecordAndSubmitAhbConversion(Image ahbImage, ImageView ahbView, Image rgbaImage, ImageView rgbaView, uint width, uint height, Format rgbaFormat)
    {
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
            _ycbcrConverter!.Convert(_cmdBuffer, ahbImage, ahbView, width, height, rgbaFormat, rgbaImage, rgbaView);
        }
        catch
        {
            // 把命令缓冲移出 recording 状态（与 NV12 转码同手法），避免 ResetCommandBuffer 返回 VK_NOT_READY。
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

    /// <summary>
    /// 把 <see cref="VulkanImageResource"/>（RGBA，TransferSrcOptimal）同步回读为紧凑 BGRA32。
    /// Android Tier2 显示桥：Skia 合成路径（<c>SkiaVideoPresenter</c>）经 <see cref="IGpuTextureResource.ReadbackToCpu"/>
    /// 消费 GPU 帧 —— 解码与 YCbCr→RGBA 转换仍在 GPU（零 CPU 像素转换），仅最终一帧一次 GPU→CPU 拷贝。
    /// </summary>
    /// <remarks>
    /// <para>独立命令池/命令缓冲/栅栏/staging buffer（<see cref="EnsureReadbackResources"/> 惰性创建），
    /// 与解码线程的转换命令资源隔离；提交段持 <c>_queueGate</c>（与转换路径共用图形队列，VkQueue 须宿主同步）。
    /// staging buffer 持久映射、grow-only 复用；输出数组经 <see cref="System.Buffers.ArrayPool{T}"/> 租借
    /// （<see cref="GpuTextureReadback"/> 池化构造，Dispose 自动归还，消除 1080p 每帧 ~8MB 的 LOH 分配）。</para>
    /// <para>图像内存屏障（布局保持 TransferSrcOptimal 的 no-op 转移）保证上一提交（转换 draw，已 QueueWaitIdle）
    /// 的写入对本提交的拷贝可见；拷贝后缓冲屏障（TRANSFER_WRITE→HOST/MemoryRead）保证 fence 等待后 host 读有效。</para>
    /// </remarks>
    private unsafe GpuTextureReadback ReadbackRgbaToCpu(VulkanImageResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        int w = resource.Width, h = resource.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"[VK-READBACK] 尺寸无效 {w}x{h}");
        ulong size = (ulong)w * (uint)h * 4;

        lock (_queueGate)
        {
            if (_readbackCommandPool.Handle == 0 && !EnsureReadbackResources())
                throw new InvalidOperationException("[VK-READBACK] 回读命令资源创建失败");
            EnsureReadbackStaging(size);

            VulkanNative.ResetCommandBuffer(_readbackCommandBuffer, CommandBufferResetFlags.None);
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            if (VulkanNative.BeginCommandBuffer(_readbackCommandBuffer, ref beginInfo) != Result.Success)
                throw new InvalidOperationException("[VK-READBACK] BeginCommandBuffer 失败");

            try
            {
                // 图像内存屏障：上一提交的转换写入 → 本次拷贝读（布局 no-op，同为 TransferSrcOptimal）。
                var subresource = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                };
                var imgBarrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.MemoryWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.TransferSrcOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = ~0u,
                    DstQueueFamilyIndex = ~0u,
                    Image = resource.Image,
                    SubresourceRange = subresource,
                };
                VulkanNative.CmdPipelineBarrier(_readbackCommandBuffer,
                    PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &imgBarrier);

                var copy = new BufferImageCopy
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,   // 0 = 紧密打包（stride = w * 4）
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    ImageOffset = new Offset3D(0, 0, 0),
                    ImageExtent = new Extent3D((uint)w, (uint)h, 1),
                };
                VulkanNative.CmdCopyImageToBuffer(_readbackCommandBuffer,
                    resource.Image, ImageLayout.TransferSrcOptimal, _readbackStagingBuffer, 1, &copy);

                // 缓冲内存屏障：TRANSFER_WRITE → HOST 读（HOST_COHERENT，fence 等待后映射数据有效）。
                var bufBarrier = new BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.MemoryReadBit,
                    SrcQueueFamilyIndex = ~0u,
                    DstQueueFamilyIndex = ~0u,
                    Buffer = _readbackStagingBuffer,
                    Offset = 0,
                    Size = size,
                };
                VulkanNative.CmdPipelineBarrier(_readbackCommandBuffer,
                    PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit,
                    0, 0, null, 1, &bufBarrier, 0, null);
            }
            catch
            {
                // 把命令缓冲移出 recording 状态，避免 ResetCommandBuffer 返回 VK_NOT_READY。
                VulkanNative.EndCommandBuffer(_readbackCommandBuffer);
                throw;
            }

            if (VulkanNative.EndCommandBuffer(_readbackCommandBuffer) != Result.Success)
                throw new InvalidOperationException("[VK-READBACK] EndCommandBuffer 失败");

            var cb = _readbackCommandBuffer;
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cb,
            };
            fixed (Fence* pFence = &_readbackFence)
            {
                // 第 4 参是 VkFence 句柄值（64 位不透明句柄），非结构指针（与 VulkanVideoGpuReadbackContext 同手法）。
                if (VulkanNative.QueueSubmit(_graphicsQueue, 1, &submitInfo, (nint)_readbackFence.Handle) != Result.Success)
                    throw new InvalidOperationException("[VK-READBACK] QueueSubmit 失败");
                if (VulkanNative.WaitForFences(_device, 1, pFence, 1, 5_000_000_000UL) != Result.Success)
                    throw new InvalidOperationException("[VK-READBACK] WaitForFences 失败");
                if (VulkanNative.ResetFences(_device, 1, pFence) != Result.Success)
                    throw new InvalidOperationException("[VK-READBACK] ResetFences 失败");
            }

            // staging 持久映射 → 池化托管数组（GpuTextureReadback 池化构造，Dispose 归还池）。
            byte[] data = System.Buffers.ArrayPool<byte>.Shared.Rent((int)size);
            new Span<byte>(_readbackStagingMapped, (int)size).CopyTo(data.AsSpan(0, (int)size));
            return new GpuTextureReadback(w, h, PixelFormat.BGRA32, data, w * 4, (int)size);
        }
    }

    /// <summary>惰性创建回读命令资源：独立命令池 + 命令缓冲 + 栅栏（复用 <see cref="EnsureConvertResources"/> 的图形队列）。</summary>
    private unsafe bool EnsureReadbackResources()
    {
        if (!EnsureConvertResources()) return false; // 图形队列 + 转换命令资源（含 _graphicsQueue/_graphicsQueueFamily）

        var poolCi = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _graphicsQueueFamily,
            // 回读命令缓冲每次 Begin 前 Reset，须此位（VUID-00046/00050）。
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        if (VulkanNative.CreateCommandPool(_device, ref poolCi, null, out _readbackCommandPool) != Result.Success)
            return false;

        var alloc = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _readbackCommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer cb;
        if (VulkanNative.AllocateCommandBuffers(_device, ref alloc, &cb) != Result.Success)
        {
            VulkanNative.DestroyCommandPool(_device, _readbackCommandPool, null);
            _readbackCommandPool = default;
            return false;
        }
        _readbackCommandBuffer = cb;

        var fenceCi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        return VulkanNative.CreateFence(_device, &fenceCi, null, out _readbackFence) == Result.Success;
    }

    /// <summary>确保回读 staging buffer 容纳 <paramref name="size"/> 字节（HOST_VISIBLE|HOST_COHERENT，持久映射，grow-only）。</summary>
    private unsafe void EnsureReadbackStaging(ulong size)
    {
        if (_readbackStagingBuffer.Handle != 0 && _readbackStagingSize >= size) return;

        if (_readbackStagingBuffer.Handle != 0)
        {
            VulkanNative.UnmapMemory(_device, _readbackStagingMemory);
            VulkanNative.DestroyBuffer(_device, _readbackStagingBuffer, null);
            VulkanNative.FreeMemory(_device, _readbackStagingMemory, null);
            _readbackStagingBuffer = default;
            _readbackStagingMemory = default;
            _readbackStagingMapped = null;
            _readbackStagingSize = 0;
        }
        _readbackStagingSize = Math.Max(size, 4096);

        var bufCi = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = _readbackStagingSize,
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };
        if (VulkanNative.CreateBuffer(_device, ref bufCi, null, out _readbackStagingBuffer) != Result.Success)
            throw new InvalidOperationException("[VK-READBACK] 创建 staging 缓冲失败");

        MemoryRequirements memReq;
        VulkanNative.GetBufferMemoryRequirements(_device, _readbackStagingBuffer, &memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindReadbackMemoryType(memReq.MemoryTypeBits),
        };
        if (VulkanNative.AllocateMemory(_device, ref allocInfo, null, out _readbackStagingMemory) != Result.Success)
            throw new InvalidOperationException("[VK-READBACK] 分配 staging 内存失败");
        if (VulkanNative.BindBufferMemory(_device, _readbackStagingBuffer, _readbackStagingMemory, 0) != Result.Success)
            throw new InvalidOperationException("[VK-READBACK] 绑定 staging 内存失败");

        void* mapped = null;
        if (VulkanNative.MapMemory(_device, _readbackStagingMemory, 0, _readbackStagingSize, 0, &mapped) != Result.Success)
            throw new InvalidOperationException("[VK-READBACK] 映射 staging 内存失败");
        _readbackStagingMapped = mapped;
    }

    /// <summary>回读 staging 内存类型：优先 HOST_VISIBLE|HOST_COHERENT，兜底任意满足 memoryTypeBits 的类型。</summary>
    private unsafe uint FindReadbackMemoryType(uint memoryTypeBits)
    {
        PhysicalDeviceMemoryProperties props;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &props);
        const MemoryPropertyFlags required =
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        for (uint i = 0; i < props.MemoryTypeCount; i++)
            if ((memoryTypeBits & (1u << (int)i)) != 0 &&
                (props.MemoryTypes[(int)i].PropertyFlags & required) == required)
                return i;
        for (uint i = 0; i < props.MemoryTypeCount; i++)
            if ((memoryTypeBits & (1u << (int)i)) != 0)
                return i;
        throw new InvalidOperationException("[VK-READBACK] 未找到 host-visible coherent 内存类型。");
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
            //    提交段持 _queueGate：与 AHB 转换/回读路径共用图形队列，VkQueue 须宿主同步。
            lock (_queueGate)
            {
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

        // 回读资源（staging 持久映射，先 Unmap 再销毁；命令池销毁级联释放其命令缓冲）
        if (_readbackStagingBuffer.Handle != 0)
        {
            VulkanNative.UnmapMemory(_device, _readbackStagingMemory);
            VulkanNative.DestroyBuffer(_device, _readbackStagingBuffer, null);
            VulkanNative.FreeMemory(_device, _readbackStagingMemory, null);
            _readbackStagingBuffer = default;
            _readbackStagingMemory = default;
            _readbackStagingMapped = null;
            _readbackStagingSize = 0;
        }
        if (_readbackFence.Handle != 0)
        {
            VulkanNative.DestroyFence(_device, _readbackFence, null);
            _readbackFence = default;
        }
        if (_readbackCommandPool.Handle != 0)
        {
            VulkanNative.DestroyCommandPool(_device, _readbackCommandPool, null);
            _readbackCommandPool = default;
        }

        _converter?.Dispose();
        _converter = null;
        _ycbcrConverter?.Dispose();
        _ycbcrConverter = null;
    }
}

using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanGpuFrameProducer 的 Import 职责段（部分类，由 VulkanGpuFrameProducer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/TryImport 分派/Dispose）在主文件；导入/转换/回读各路径零反射原生调用。
public sealed unsafe partial class VulkanGpuFrameProducer
{
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
}

using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanGpuFrameProducer 的 Android 职责段（部分类，由 VulkanGpuFrameProducer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/TryImport 分派/Dispose）在主文件；导入/转换/回读各路径零反射原生调用。
public sealed unsafe partial class VulkanGpuFrameProducer
{
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
            // 【关键】Buffer 字段承载 AHardwareBuffer* 的【值】——必须赋源指针本身（对齐 Windows 路径
            //   TryImportWin32 的 Handle = source.Handle）。若误写 &ahbHandle 传的是栈局部变量地址，驱动
            //   vkAllocateMemory 导入时会把栈地址当 AHardwareBuffer 解引用其引用计数 → 对齐错误的原生崩溃。
            var dedicated = new MemoryDedicatedAllocateInfo
            {
                SType = StructureType.MemoryDedicatedAllocateInfo,
                Image = image,
            };
            var imp = new ImportAndroidHardwareBufferInfoANDROID
            {
                SType = StructureType.ImportAndroidHardwareBufferInfoAndroid,
                // Buffer 字段类型 nint*（Silk.NET 把 struct AHardwareBuffer 的指针置为 nint*）。
                // 【关键】应存 AHardwareBuffer* 的【值】= (nint*)source.Handle（bitcast AHB 指针本身）；
                // 若误传 &ahbHandle（指向局部变量）→ 驱动把那栈地址当 AHardwareBuffer 解引用引用计数 →
                // 原生崩溃。对齐 Windows 路径 Handle=source.Handle 传值的语义。
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
}

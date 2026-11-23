using LingFan.Media.GPUShare.Android;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanSharedSurfaceSource 的 Android AHB 直采样职责段（部分类，按职责拆分，编译期等价）。
// 0 次队列提交的 RGBA AHB 导入直采路径（含退役环）；YCbCr/RGBA 转换路径在 Android 主段。
internal sealed unsafe partial class VulkanSharedSurfaceSource
{
    /// <summary>
    /// Android RGBA AHB 直采样导入（<b>0 次队列提交</b>）：把解码侧 RGBA AHardwareBuffer 导入为普通
    /// R8G8B8A8Unorm VkImage（纯 CPU：CreateImage + AHB 内存导入 + BindImageMemory），不记录任何命令、
    /// 不提交队列——Skia 在自身帧提交内直接采样该导入图像（AHB-DIRECT 路径）。
    /// </summary>
    /// <remarks>
    /// <para><b>背景</b>：旧「转换+拷贝」双提交与 Skia 帧提交共用同一 VkQueue（device 仅单一队列族），
    /// 在 Adreno 上以极低频率随机触发 vkQueueSubmit ErrorInitializationFailed（规范外错误码），
    /// 并殃及同队列的 Skia 提交 → Avalonia 渲染循环停摆 → 画面永久定格（管线侧照常出帧而绘制侧心跳终止）。</para>
    /// <para><b>生命周期</b>：导入内存持有驱动侧 AHB 引用（钉住 gralloc 缓冲）——本源侧
    /// AHardwareBuffer_release（ReturnFrame/池回收）不会回收缓冲，ImageReader 无法复用，Skia 采样
    /// 窗口内内容稳定；导入由 <see cref="_ahbDirectRetire"/> 退役环在 <see cref="AhbDirectRetireDepth"/>
    /// 帧后销毁（GPU 早已执行完采样）。</para>
    /// <para><b>布局</b>：导入图像实际布局按 Android AHB 惯例视作 GENERAL（全程无屏障无过渡；采样在
    /// GENERAL 下合法）。交付描述符<b>如实声明该实际布局</b>（<c>ImageLayout.General</c>）——采样型包装
    /// 不做布局屏障，声明值仅作 Skia 内部记账，唯一要求是<b>声明与实际一致</b>：若声明
    /// SHADER_READ_ONLY 而实际 GENERAL，包装可能通过但采样结果黑屏。</para>
    /// </remarks>
    /// <returns>是否成功建立直采样描述符（false 时调用方落回旧转换路径）。</returns>
    private bool TryBuildAhbDirectDescriptor(
        AndroidHardwareBufferFrameResource ahb, int w, int h, int rotationDegrees,
        out SharedGpuSurfaceDescriptor descriptor)
    {
        descriptor = default;

        MarkStage("AHB-DIRECT.GetAhbProperties");
        AndroidHardwareBufferFormatPropertiesANDROID formatProps = new()
        {
            SType = StructureType.AndroidHardwareBufferFormatPropertiesAndroid,
        };
        AndroidHardwareBufferPropertiesANDROID props = new()
        {
            SType = StructureType.AndroidHardwareBufferPropertiesAndroid,
            PNext = &formatProps,
        };
        if (VulkanNative.GetAndroidHardwareBufferPropertiesANDROID(_device, ahb.AhbHandle, &props) != Result.Success)
        {
            _logger.LogTrace("vkGetAndroidHardwareBufferPropertiesANDROID 失败（AHB 直采样）。");
            return false;
        }

        // 仅 concrete RGBA（vkFormat 非 UNDEFINED）可直采。判 format 而非 externalFormat：Adreno 上
        // concrete R8G8B8A8Unorm 存在时 externalFormat 仍报实现定义值 0x1C（规范允许），
        // 判 externalFormat!=0 会恒误退回旧路径。
        // format=UNDEFINED 且 externalFormat!=0 才是 YCbCr 外部格式，需 samplerYcbcrConversion，
        // Skia 的 GRBackendTexture 无对应包装 → 落回旧 YCbCr 转换路径。
        if (formatProps.Format == Format.Undefined)
            return false;

        ExternalMemoryImageCreateInfo extMem = new()
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.AndroidHardwareBufferBitAndroid,
        };
        // 用法 = 0x17（SAMPLED|TRANSFER_SRC|TRANSFER_DST|COLOR_ATTACHMENT），与旧双提交路径被 Skia
        // 接受的 usage 逐位一致（SAMPLED-only(0x4) 会被拒）。
        // AHB 按 GPU_FRAMEBUFFER 分配，COLOR_ATTACHMENT 在 formatFeatures 内；万一驱动拒绝再回落 0x4。
        ImageCreateInfo ci = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = _surfaceVkFormat,
            Extent = new Extent3D((uint)w, (uint)h, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit
                  | ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            PNext = &extMem,
        };
        MarkStage("AHB-DIRECT.CreateImage");
        if (VulkanNative.CreateImage(_device, &ci, null, out Image ahbImage) != Result.Success)
        {
            _logger.LogTrace("vkCreateImage（AHB 直采样 usage=0x17）失败，回落 SAMPLED-only。");
            ci.Usage = ImageUsageFlags.SampledBit;
            if (VulkanNative.CreateImage(_device, &ci, null, out ahbImage) != Result.Success)
            {
                _logger.LogTrace("vkCreateImage（AHB 直采样 usage=0x4）仍失败。");
                return false;
            }
        }

        var dedicated = new MemoryDedicatedAllocateInfo
        {
            SType = StructureType.MemoryDedicatedAllocateInfo,
            Image = ahbImage,
        };
        var imp = new ImportAndroidHardwareBufferInfoANDROID
        {
            SType = StructureType.ImportAndroidHardwareBufferInfoAndroid,
            Buffer = (nint*)ahb.AhbHandle,
            PNext = &dedicated,
        };
        var ai = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = &imp,
            AllocationSize = props.AllocationSize,
            MemoryTypeIndex = ExternalCompatibleMemoryType(props.MemoryTypeBits),
        };
        MarkStage("AHB-DIRECT.AllocateMemory");
        if (VulkanNative.AllocateMemory(_device, &ai, null, out DeviceMemory ahbMemory) != Result.Success)
        {
            _logger.LogTrace("vkAllocateMemory（AHB 直采样导入）失败。");
            VulkanNative.DestroyImage(_device, ahbImage, null);
            return false;
        }
        MarkStage("AHB-DIRECT.BindImageMemory");
        if (VulkanNative.BindImageMemory(_device, ahbImage, ahbMemory, 0) != Result.Success)
        {
            _logger.LogTrace("vkBindImageMemory（AHB 直采样导入）失败。");
            VulkanNative.FreeMemory(_device, ahbMemory, null);
            VulkanNative.DestroyImage(_device, ahbImage, null);
            return false;
        }

        // 一次性锚点日志（直采路径启用确认；usage 记录实际创建值，供包装校验对表）。
        if (!_ahbDirectDiagLogged)
        {
            _ahbDirectDiagLogged = true;
            _logger.LogInformation(
                "[AHB-DIRECT] RGBA AHB 直采样启用（0 次队列提交，导入图像交 Skia 直采）{W}x{H} AllocationSize={Size} vkFormat={Fmt} externalFormat=0x{Ext:X} usage=0x{Usage:X}",
                w, h, (ulong)props.AllocationSize, formatProps.Format, formatProps.ExternalFormat, (uint)ci.Usage);
        }

        // 退役环：满员即销毁最老导入（深度 8 帧 ≫ vsync 节奏下的 GPU 在飞深度 2~3 帧）。
        while (_ahbDirectRetire.Count >= AhbDirectRetireDepth)
        {
            (Image oldImage, DeviceMemory oldMemory) = _ahbDirectRetire.Dequeue();
            MarkStage("AHB-DIRECT.RetireDestroyImage");
            VulkanNative.DestroyImage(_device, oldImage, null);
            MarkStage("AHB-DIRECT.RetireFreeMemory");
            VulkanNative.FreeMemory(_device, oldMemory, null);
        }
        _ahbDirectRetire.Enqueue((ahbImage, ahbMemory));

        // 描述符直指导入图像：声明布局=GENERAL、独占共享（同队列族），Skia 按此包装采样。
        // 布局声明=GENERAL 是【诚实的实际布局】（AHB 外部内存惯例：GL 写入后无任何 Vulkan 屏障，
        // 实际即 GENERAL），也是 Adreno 采样契约的正确声明——descriptor imageLayout 与实际一致时
        // 采样才可靠（声明 SHADER_READ_ONLY(5) 而实际 GENERAL 时，包装可能通过但采样黑屏）。
        // 【防回归】NativeImageUsage 须如实引用 ci.Usage（创建=声明，逐位一致）：若声明值与创建值
        // 不一致，Skia 侧包装校验会以声明 usage 判定而失败，创建侧的正确 usage 从未到达包装校验。
        descriptor = new SharedGpuSurfaceDescriptor(
            (nint)ahbImage.Handle,
            _handleKind,
            w, h,
            _surfaceFormatEnum,
            _version,
            _syncMode,
            (ulong)props.AllocationSize,
            0,
            NativeImage: (nint)ahbImage.Handle,
            NativeDeviceMemory: (nint)ahbMemory.Handle,
            NativeImageLayout: (uint)ImageLayout.General,
            NativeVkFormat: (uint)_surfaceVkFormat,
            NativeQueueFamilyIndex: _queueFamilyIndex,
            NativeImageUsage: (uint)ci.Usage,
            NativeImageTiling: (uint)ImageTiling.Optimal,
            RotationDegrees: rotationDegrees);
        return true;
    }
}

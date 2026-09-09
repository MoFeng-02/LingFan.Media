using LingFan.Media.GPUShare.Android;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan 共享表面源（no-airspace 纯无空域生产者）：把软帧渲染进一块<b>可外部导出</b>的
/// Vulkan 离屏 <see cref="Image"/>，经外部内存句柄交给宿主合成器直接导入采样，从而实现
/// 「无空域、纯控件级」的 GPU 上屏——<b>Vulkan 渲染 Vulkan 自己的</b>，不跨界喂 D3D11 组合器。
/// </summary>
/// <remarks>
/// <para><b>这是渲染器层唯一碰 Vulkan 具体 API 的地方</b>。其余层（Avalonia <c>CompositionVideoRenderer</c>）
/// 只看到 <see cref="SharedGpuSurfaceDescriptor"/>（外部内存句柄 + 信号量对），<b>不引用任何 GPU 库</b>，
/// 从而达成「不绑定具体 GPU、低耦合」的架构诉求，严守「各 Renderer 管好自身（无头/有头/无空域）」架构原则。</para>
/// <para><b>零拷贝路径</b>：软帧（NV12/NV21/YUV420P/YUV422P/YUV444P/BGRA/RGBA）的 Y/U/V 平面上传到 GPU 纹理后
/// 由 <see cref="VulkanShaderPipeline"/> 完成 YUV→RGB + 缩放，写入可导出离屏图像；该图像由宿主合成器
/// 经 <c>VkImportMemoryWin32HandleInfoKHR</c> / <c>VkImportMemoryFdInfoKHR</c> 直接导入采样，无 CPU 回读、无独占 HWND。</para>
/// <para><b>同步模型（Semaphores，Vulkan 原生机制）</b>：</para>
/// <list type="bullet">
/// <item>生产者（本源）写完后 signal <c>ConsumerWait</c> 信号量（消费方据此等待「内容就绪」）；</item>
/// <item>消费方采样完成后 signal <c>ConsumerSignal</c> 信号量（生产者据此等待「表面已归还，可覆写」）；</item>
/// <item>生产者每帧以<b>有限超时（16ms，与 D3D11 keyed mutex 超时对称）</b>等待 <c>ConsumerSignal</c> 后再写，
/// 超时即丢帧——绝不无限阻塞管线线程。两信号量均经外部句柄导出，消费方导入一次长期使用。</item>
/// </list>
/// <para><b>握手初始化</b>：二进制信号量默认未信号。生产者首帧须等待 <c>ConsumerSignal</c>，故创建时以一次
/// signal-only 提交将其初始化为信号态，避免首帧永久阻塞。</para>
/// <para><b>异步策略</b>：<see cref="TryWriteFrame"/> 同步（native 分类）——GPU 命令提交无真实 I/O await，
/// 补 async 即伪异步；且用<b>有限超时</b> Fence 等待，超时即丢帧，绝不阻塞管线线程。</para>
/// <para><b>线程</b>：由管线线程调用；共享 Vulkan 设备已开启多线程保护（见 <see cref="VulkanRendererFactory"/>），
/// 信号量握手负责与 Avalonia 合成线程的跨线程同步。</para>
/// <para><b>AOT 兼容</b>：sealed 类，裸 vtable 互操作，无反射。</para>
/// </remarks>
internal sealed unsafe partial class VulkanSharedSurfaceSource : ISharedGpuSurfaceSource
{
    // 信号量握手键（Semaphores 模型不使用 keyed mutex，恒为 0）
    // Android 平台实现（AHB 零拷贝全家：YCbCr/RGBA 转换、直采样描述符、分步提交、退役环）

    // Android 零拷贝稳健层：解码侧 AHB 仅作 SOURCE——经 YCbCr 转换渲进 plain 内部 RGBA 图像
    // （_convertImage，用法与 VulkanGpuFrameProducer.TryCreateRgbaTarget 完全同款），再 vkCmdCopyImage
    // 拷进普通 Vulkan 外部图像 _sharedImage（OpaqueFd 导出交合成器）。此 GPU→GPU 拷贝为零 CPU 像素拷贝；
    // _sharedImage 现为普通 Vulkan 图像（与 Linux 完全一致），规避 AHB gralloc fd 经 OPAQUE_FD 重导入的
    // stride 失配（VK_ERROR_INVALID_EXTERNAL_HANDLE_KHR）。仅 Android 启用内部 _convertImage。
    private Image _convertImage;
    private DeviceMemory _convertMemory;
    private ImageView _convertView;

    // _sharedImage 当前交付槽位是否已进入 TransferDstOptimal（跨命令缓冲持久；尺寸变化时重建归零）。

    // Android AHB 诊断/自提交标志：AHB 路径改为「转换」「拷贝」两步分提交以隔离 Mali DEVICE_LOST
    // （发生在 AHB YCbCr 采样，还是写入导入的 AHB 离屏）。置位后 TryWriteFrame 跳过公共提交段（已在内部完成）。
    private bool _ahbSelfSubmitted;

    // AHB YCbCr 转换建议值诊断仅打印一次。
    private bool _ahbYcbcrDiagLogged;
    private bool _ahbRgbaDiagLogged;

    // AHB 直采样（DIRECT，Android RGBA 零队列提交路径）
    // 导入图像退役环：导入内存持有驱动侧 AHB 引用（钉住 gralloc 缓冲，ImageReader 无法复用），
    // 交付后延迟 N 帧销毁导入，确保 Skia 在自身帧提交内的采样早已执行完毕。
    private readonly Queue<(Image Image, DeviceMemory Memory)> _ahbDirectRetire = new();
    private const int AhbDirectRetireDepth = 8;
    private bool _ahbDirectDiagLogged;
    /// <summary>
    /// 记录 AHB 帧的 GPU 零拷贝转换：把 MediaCodec 产出的 AHardwareBuffer 经
    /// <c>VK_ANDROID_external_memory_android_hardware_buffer</c> 导入为 VkImage，再在同命令缓冲内
    /// 把像素转进共享离屏表面（<c>_sharedImage</c>），全程零 CPU 像素拷贝、零跨队列竞态
    /// （与生产者自身 <c>_queue</c> 同一提交）。按 AHB 是否含外部格式分两条路径：
    /// <list type="bullet">
    /// <item><description>外部格式（YUV）：经 <see cref="VulkanYcbcrToRgbaConverter"/> 做 YUV→RGB。</description></item>
    /// <item><description>非外部格式（RGBA，ImageReader 以 Rgba8888 产出）：经 <see cref="VulkanRgbaToRgbaConverter"/>
    /// 普通采样直渲，绕开 Adreno 对「外部格式 YUV AHB + YCbCr 采样」的原生空指针崩溃。</description></item>
    /// </list>
    /// 复用 <c>VulkanGpuFrameProducer.TryImportAndroidAHardwareBuffer</c> 已验证的导入范式
    /// （含 <c>Buffer=(nint*)ahb.AhbHandle</c> 传值，规避把栈地址当 AHB 解引用的原生崩溃）。
    /// </summary>
    /// <returns>是否成功记录命令（true 时命令缓冲由本方法或调用方提交）。</returns>
    private bool TryRecordAhbConversion(AndroidHardwareBufferFrameResource ahb, int w, int h)
    {
        if (!_isAndroid)
            return false; // AHB 导入仅 Android 路径
        if (!VulkanNative.HasAndroidHardwareBufferProperties || !VulkanNative.HasSamplerYcbcrConversion)
        {
            _logger.LogTrace("AHB 导入不可用：AHB 扩展或 samplerYcbcrConversion 未解析，交回软帧回退。");
            return false;
        }

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        if (VulkanNative.BeginCommandBuffer(_commandBuffer, ref beginInfo) != Result.Success)
        {
            _logger.LogWarning("Vulkan 共享表面 BeginCommandBuffer（AHB）失败。");
            return false;
        }

        // 1) 查询 AHB 属性：externalFormat / 转换建议值 / 内存参数（AHB 导入权威值）。
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
            _logger.LogTrace("vkGetAndroidHardwareBufferPropertiesANDROID 失败（AHB 转换）。");
            VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
            return false;
        }
        // 2) 分支：按 AHB 是否真·YUV 外部格式选择转换路径。
        //    判别权威信号 = SuggestedYcbcrModel（vkGetAndroidHardwareBufferPropertiesANDROID 返回）：
        //    - RgbIdentity：concrete RGBA AHB。GLES 桥接以 R8G8B8A8 产出 RGBA AHB，已把 YUV→RGB 留在 GLES 阶段完成，
        //      Vulkan 侧只需普通 RGBA 采样直渲（TryRecordAhbConversionRgba），绝不能再做 YCbCr 转换。
        //      注意 RGBA AHB 的 ExternalFormat 也非零，故 ExternalFormat==0 不足以判别 RGBA。
        //    - 其余 Ycbcr* 模型：真 YUV AHB，才走 YCbCr 转换路径。
        //    旧逻辑以 ExternalFormat==0 判 RGBA 是错的：RGBA AHB 会被误送 YCbCr 路径，对其创建
        //    VkSamplerYcbcrConversion 并采样会触发驱动空指针崩溃。
        if (formatProps.SuggestedYcbcrModel == SamplerYcbcrModelConversion.RgbIdentity)
            return TryRecordAhbConversionRgba(ahb, w, h);

        // 2) 外部格式 VkImage：UNDEFINED + AHB 句柄类型 + externalFormat；仅 SAMPLED 用法。
        ExternalFormatANDROID extFormat = new()
        {
            SType = StructureType.ExternalFormatAndroid,
            ExternalFormat = formatProps.ExternalFormat,
        };
        ExternalMemoryImageCreateInfo extMem = new()
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.AndroidHardwareBufferBitAndroid,
            PNext = &extFormat,
        };
        ImageCreateInfo ci = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.Undefined,
            Extent = new Extent3D((uint)w, (uint)h, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            PNext = &extMem,
        };
        Image ahbImage = default;
        if (VulkanNative.CreateImage(_device, &ci, null, out ahbImage) != Result.Success)
        {
            _logger.LogTrace("vkCreateImage（AHB 外部格式）失败。");
            VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
            return false;
        }

        DeviceMemory ahbMemory = default;
        ImageView ahbView = default;
        bool memBound = false, viewCreated = false;
        try
        {
            // 3) dedicated 内存导入：Buffer 须存 AHardwareBuffer* 的【值】=(nint*)ahb.AhbHandle
            //    （防回归：误传 &localVar 会致驱动解引用栈地址 → 对齐错误的原生崩溃）。
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
            if (VulkanNative.AllocateMemory(_device, &ai, null, out ahbMemory) != Result.Success)
            {
                _logger.LogTrace("vkAllocateMemory（AHB 导入）失败。");
                VulkanNative.DestroyImage(_device, ahbImage, null);
                VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
                return false;
            }
            if (VulkanNative.BindImageMemory(_device, ahbImage, ahbMemory, 0) != Result.Success)
            {
                _logger.LogTrace("vkBindImageMemory（AHB 导入）失败。");
                VulkanNative.FreeMemory(_device, ahbMemory, null);
                VulkanNative.DestroyImage(_device, ahbImage, null);
                VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
                return false;
            }
            memBound = true;

            // 4) YCbCr 转换管线 + 采样视图
            _ycbcrConverter ??= new VulkanYcbcrToRgbaConverter(_device, _physicalDevice, _logger);
            _ycbcrConverter.EnsurePipeline(_surfaceVkFormat, formatProps.ExternalFormat, formatProps);
            ahbView = _ycbcrConverter.CreateImageView(ahbImage);
            viewCreated = true;

            // 【诊断】一次性打印解码侧 AHB 的 YCbCr 转换建议值（Mali 采样正确性锚点）。
            if (!_ahbYcbcrDiagLogged)
            {
                _ahbYcbcrDiagLogged = true;
                _logger.LogInformation(
                    "[AHB-DIAG] 解码侧 AHB 外部格式 externalFormat=0x{Ext:X} model={Model} range={Range} " +
                    "xOff={XOff} yOff={YOff} comp=({R},{G},{B},{A}) formatFeatures=0x{Feat:X}",
                    formatProps.ExternalFormat, formatProps.SuggestedYcbcrModel, formatProps.SuggestedYcbcrRange,
                    formatProps.SuggestedXChromaOffset, formatProps.SuggestedYChromaOffset,
                    formatProps.SamplerYcbcrConversionComponents.R, formatProps.SamplerYcbcrConversionComponents.G,
                    formatProps.SamplerYcbcrConversionComponents.B, formatProps.SamplerYcbcrConversionComponents.A,
                    (ulong)formatProps.FormatFeatures);
            }

            // 5) AHB 源 → 渲染进【plain 内部 RGBA 目标】_convertImage（Android）/ _sharedImage（Win/Linux）。
            //    Android 走「分步提交」以隔离 Mali DEVICE_LOST 的发生步：先单独提交转换（AHB YCbCr 采样），
            //    再单独提交拷贝（写入共享离屏 _sharedImage）。任一步 DEVICE_LOST 即在日志定位。
            Image convertTarget = _isAndroid ? _convertImage : _sharedImages[0];
            ImageView convertView = _isAndroid ? _convertView : _sharedImageViews[0];
            _logger.LogTrace("[AHB-DIAG] 进入 Convert（AHB→_convertImage GPU 绘制）{W}x{H}", w, h);
            _ycbcrConverter.Convert(_commandBuffer, ahbImage, ahbView, (uint)w, (uint)h,
                _surfaceVkFormat, convertTarget, convertView);
            _logger.LogTrace("[AHB-DIAG] Convert 记录完成，准备分步提交");

            // 6) Android：分步提交。先提交并等待转换（AHB→_convertImage），隔离 AHB 采样是否触发设备级错误。
            if (_isAndroid)
            {
                if (!SubmitAhbStep("AHB-YCbCr采样转换", w, h))
                {
                    _ycbcrConverter.DestroyImageView(ahbView);
                    viewCreated = false;
                    VulkanNative.DestroyImage(_device, ahbImage, null);
                    VulkanNative.FreeMemory(_device, ahbMemory, null);
                    return false;
                }
                // 销毁瞬态 AHB 侧资源（转换已完成并等待）。
                _ycbcrConverter.DestroyImageView(ahbView);
                viewCreated = false;
                VulkanNative.DestroyImage(_device, ahbImage, null);
                VulkanNative.FreeMemory(_device, ahbMemory, null);

                // 7) 拷贝：_convertImage(TransferSrcOptimal) → 共享离屏 _sharedImage（仅 TRANSFER_DST）。
                VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
                CommandBufferBeginInfo begin2 = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                if (VulkanNative.BeginCommandBuffer(_commandBuffer, ref begin2) != Result.Success)
                {
                    _logger.LogWarning("[AHB-DIAG] 拷贝 BeginCommandBuffer 失败。");
                    return false;
                }
                CopyToSharedImage(_commandBuffer, ImageLayout.TransferSrcOptimal, w, h);
                if (!SubmitAhbStep("拷贝进共享离屏_sharedImage", w, h))
                    return false;
                _ahbSelfSubmitted = true; // 已自提交，TryWriteFrame 跳过公共提交段
                return true;
            }

            // 非 Android：同命令缓冲内转换完成，公共提交段负责提交。销毁瞬态 AHB 侧资源（GPU 等待在公共提交段）。
            _ycbcrConverter.DestroyImageView(ahbView);
            viewCreated = false;
            VulkanNative.DestroyImage(_device, ahbImage, null);
            VulkanNative.FreeMemory(_device, ahbMemory, null);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vulkan 共享表面 AHB 转换记录失败，交回软帧回退。");
            if (viewCreated) _ycbcrConverter?.DestroyImageView(ahbView);
            if (memBound) VulkanNative.FreeMemory(_device, ahbMemory, null);
            if (ahbImage.Handle != 0) VulkanNative.DestroyImage(_device, ahbImage, null);
            VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
            return false;
        }
    }

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
    /// GENERAL 下合法）。交付描述符声明为 ShaderReadOnlyOptimal——与旧双提交路径一致的可包装形状
    /// （实测声明 GENERAL 会被 SKImage.FromTexture 拒收）；采样型包装无布局屏障，声明值
    /// 仅作 Skia 内部记账。</para>
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
        // 接受的 usage 逐位一致（实测 SAMPLED-only(0x4) 会被拒）。
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
        // 采样才可靠（实测声明 SHADER_READ_ONLY(5) 而实际 GENERAL，包装可能通过但采样黑屏）。
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

    /// <summary>
    /// 记录 RGBA AHB 帧的 GPU 零拷贝转换（绕开 Adreno YCbCr 崩溃路径）：把 MediaCodec 产出的
    /// <b>非外部格式</b> RGBA AHardwareBuffer 经 <c>VK_ANDROID_external_memory_android_hardware_buffer</c>
    /// 以 concrete <see cref="Format.R8G8B8A8Unorm"/> 导入为普通 RGBA VkImage（不带 externalFormat、无 YCbCr 转换），
    /// 再经 <see cref="VulkanRgbaToRgbaConverter"/> 用普通（可变）采样器采样，把 RGBA 直渲进共享离屏表面
    /// （<c>_sharedImage</c>），全程零 CPU 像素拷贝、零跨队列竞态。
    /// </summary>
    /// <returns>是否成功记录命令（true 时命令缓冲由本方法自提交，TryWriteFrame 跳过公共提交段）。</returns>
    private bool TryRecordAhbConversionRgba(AndroidHardwareBufferFrameResource ahb, int w, int h)
    {
        // 1) 查询 AHB 属性（AllocationSize / MemoryTypeBits，供专用内存导入；formatProps 取 format/externalFormat 诊断）。
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
            _logger.LogTrace("vkGetAndroidHardwareBufferPropertiesANDROID 失败（RGBA AHB 转换）。");
            VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
            return false;
        }

        // 2) concrete RGBA VkImage：R8G8B8A8Unorm（与 _surfaceVkFormat(Android) 一致），仅 SAMPLED 用法。
        //    无 ExternalFormat pNext：RGBA 是 concrete 格式，不走外部格式（UNDEFINED）范式。
        ExternalMemoryImageCreateInfo extMem = new()
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.AndroidHardwareBufferBitAndroid,
        };
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
            Usage = ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            PNext = &extMem,
        };
        Image ahbImage = default;
        if (VulkanNative.CreateImage(_device, &ci, null, out ahbImage) != Result.Success)
        {
            _logger.LogTrace("vkCreateImage（RGBA AHB）失败。");
            VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
            return false;
        }

        DeviceMemory ahbMemory = default;
        ImageView ahbView = default;
        bool memBound = false, viewCreated = false;
        try
        {
            // 3) dedicated 内存导入：Buffer 须存 AHardwareBuffer* 的【值】=(nint*)ahb.AhbHandle。
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
            if (VulkanNative.AllocateMemory(_device, &ai, null, out ahbMemory) != Result.Success)
            {
                _logger.LogTrace("vkAllocateMemory（RGBA AHB 导入）失败。");
                VulkanNative.DestroyImage(_device, ahbImage, null);
                VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
                return false;
            }
            if (VulkanNative.BindImageMemory(_device, ahbImage, ahbMemory, 0) != Result.Success)
            {
                _logger.LogTrace("vkBindImageMemory（RGBA AHB 导入）失败。");
                VulkanNative.FreeMemory(_device, ahbMemory, null);
                VulkanNative.DestroyImage(_device, ahbImage, null);
                VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
                return false;
            }
            memBound = true;

            // 4) 普通 RGBA 转换管线 + 采样视图（无 YCbCr）。
            _rgbaConverter ??= new VulkanRgbaToRgbaConverter(_device, _physicalDevice, _logger);
            _rgbaConverter.EnsurePipeline(_surfaceVkFormat);
            ahbView = _rgbaConverter.CreateImageView(ahbImage);
            viewCreated = true;

            // 【诊断】一次性打印 RGBA AHB 导入（Adreno 兼容路径锚点）。
            if (!_ahbRgbaDiagLogged)
            {
                _ahbRgbaDiagLogged = true;
                _logger.LogInformation(
                    "[AHB-DIAG] 解码侧 RGBA AHB 导入（绕开 YCbCr）{W}x{H} AllocationSize={Size} memTypeBits=0x{Bits:X} vkFormat={Fmt} externalFormat=0x{Ext:X} ycbcrModel={Model}",
                    w, h, (ulong)props.AllocationSize, props.MemoryTypeBits,
                    formatProps.Format, formatProps.ExternalFormat, formatProps.SuggestedYcbcrModel);
            }

            // 5) AHB 源（普通 RGBA）→ 渲染进内部 _convertImage（Android）/ _sharedImages[0]（非 Android）。
            Image convertTarget = _isAndroid ? _convertImage : _sharedImages[0];
            ImageView convertView = _isAndroid ? _convertView : _sharedImageViews[0];
            _logger.LogTrace("[AHB-DIAG] 进入 Convert（RGBA AHB→_convertImage 普通采样）{W}x{H}", w, h);
            _rgbaConverter.Convert(_commandBuffer, ahbImage, ahbView, (uint)w, (uint)h, convertTarget, convertView);
            _logger.LogTrace("[AHB-DIAG] Convert 记录完成，准备分步提交");

            // 6) Android：分步提交。先提交并等待转换（RGBA AHB 采样），隔离采样是否触发设备级错误。
            if (_isAndroid)
            {
                if (!SubmitAhbStep("RGBA-AHB采样转换", w, h))
                {
                    _rgbaConverter.DestroyImageView(ahbView);
                    viewCreated = false;
                    VulkanNative.DestroyImage(_device, ahbImage, null);
                    VulkanNative.FreeMemory(_device, ahbMemory, null);
                    return false;
                }
                _rgbaConverter.DestroyImageView(ahbView);
                viewCreated = false;
                VulkanNative.DestroyImage(_device, ahbImage, null);
                VulkanNative.FreeMemory(_device, ahbMemory, null);

                // 7) 拷贝：_convertImage(TransferSrcOptimal) → 共享离屏 _sharedImage（仅 TRANSFER_DST）。
                VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
                CommandBufferBeginInfo begin2 = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                if (VulkanNative.BeginCommandBuffer(_commandBuffer, ref begin2) != Result.Success)
                {
                    _logger.LogWarning("[AHB-DIAG] 拷贝 BeginCommandBuffer 失败（RGBA）。");
                    return false;
                }
                CopyToSharedImage(_commandBuffer, ImageLayout.TransferSrcOptimal, w, h);
                if (!SubmitAhbStep("拷贝进共享离屏_sharedImage", w, h))
                    return false;
                _ahbSelfSubmitted = true; // 已自提交，TryWriteFrame 跳过公共提交段
                return true;
            }

            // 非 Android：同命令缓冲内转换完成，公共提交段负责提交。销毁瞬态 AHB 侧资源。
            _rgbaConverter.DestroyImageView(ahbView);
            viewCreated = false;
            VulkanNative.DestroyImage(_device, ahbImage, null);
            VulkanNative.FreeMemory(_device, ahbMemory, null);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vulkan 共享表面 RGBA AHB 转换记录失败，交回软帧回退。");
            if (viewCreated) _rgbaConverter?.DestroyImageView(ahbView);
            if (memBound) VulkanNative.FreeMemory(_device, ahbMemory, null);
            if (ahbImage.Handle != 0) VulkanNative.DestroyImage(_device, ahbImage, null);
            VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
            return false;
        }
    }

    /// <summary>
    /// Android AHB 分步提交：把当前记录的 <see cref="_commandBuffer"/> 提交并等待，检测 Mali DEVICE_LOST，
    /// 精确定位错误发生在 AHB YCbCr/RGBA 采样步还是写入共享离屏步。返回是否成功（无 device lost）。
    /// </summary>
    private bool SubmitAhbStep(string stepTag, int w, int h)
    {
        _logger.LogTrace("[AHB-DIAG] {Step} 进入提交（EndCommandBuffer 前）", stepTag);
        Result endR = VulkanNative.EndCommandBuffer(_commandBuffer);
        if (endR != Result.Success)
        {
            _logger.LogWarning("[AHB-DIAG] {Step} EndCommandBuffer 失败：{Result}", stepTag, endR);
            return false;
        }
        // 本机 Roslyn 对「&字段」判定为 CS0212，故栈上取副本再取地址（跨环境安全写法）。
        Fence fence = _frameFence;
        VulkanNative.ResetFences(_device, 1, &fence);
        CommandBuffer cb = _commandBuffer;
        SubmitInfo si = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
        };
        Result subR = VulkanNative.QueueSubmit(_queue, 1, &si, (nint)_frameFence.Handle);
        if (subR != Result.Success)
        {
            _logger.LogWarning("[AHB-DIAG] {Step} QueueSubmit 失败：{Result}", stepTag, subR);
            return false;
        }
        Result waitR = VulkanNative.WaitForFences(_device, 1, &fence, 1u, WriteWaitTimeoutNs);
        if (waitR == Result.ErrorDeviceLost)
        {
            _logger.LogWarning("[AHB-DIAG] {Step} 触发 Mali DEVICE_LOST（GROUP_ERROR_FATAL）—— 定位到此步。", stepTag);
            return false;
        }
        if (waitR != Result.Success)
        {
            _logger.LogWarning("[AHB-DIAG] {Step} WaitForFences 失败：{Result}", stepTag, waitR);
            return false;
        }
        _logger.LogTrace("[AHB-DIAG] {Step} 提交成功（无 DEVICE_LOST）{W}x{H}", stepTag, w, h);
        return true;
    }
}
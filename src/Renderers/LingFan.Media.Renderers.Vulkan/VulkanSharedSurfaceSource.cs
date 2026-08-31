using LingFan.Media.GPUShare.Vulkan;
using LingFan.Media.Renderers.Shared;
using System.Runtime.InteropServices;

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
    // ── 信号量握手键（Semaphores 模型不使用 keyed mutex，恒为 0）──
    private const ulong UnusedKey = 0;
    // 生产者等待消费方归还表面的有限超时（纳秒，与 D3D11 的 16ms AcquireSync 超时对称）。
    private const ulong WriteWaitTimeoutNs = 16_000_000;

    private readonly VulkanRendererFactory _factory;
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Queue _queue;
    private readonly uint _queueFamilyIndex;
    private readonly ILogger<VulkanSharedSurfaceSource> _logger;
    private readonly bool _isWindows;
    private readonly bool _isApple;
    private readonly bool _isAndroid;
    private readonly Format _surfaceVkFormat;
    private readonly SharedGpuSurfaceFormat _surfaceFormatEnum;
    private readonly SharedGpuSyncMode _syncMode;
    private readonly SharedGpuHandleKind _handleKind;
    private readonly SharedGpuSemaphoreKind _semaphoreKind;
    private readonly ExternalMemoryHandleTypeFlags _memHandleType;
    private readonly ExternalSemaphoreHandleTypeFlags _semHandleType;

    // 离屏渲染管线（与 SwapChain 路径共用着色器/描述符/管线布局，仅 RenderPass/Pipeline/Framebuffer 独立）。
    private VulkanShaderPipeline? _pipeline;

    // AHB（Android 硬件缓冲）YUV→RGBA 转换核心（GPUShare.Vulkan，零反射 AOT 友好）。
    // 仅 Android 真零拷贝路径（AHB 帧）使用；CPU 软帧路径不经此转换器。
    private VulkanYcbcrToRgbaConverter? _ycbcrConverter;
    private VulkanRgbaToRgbaConverter? _rgbaConverter;

    // 命令提交（每帧复用）
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private Fence _frameFence;

    // 可外部导出离屏图像（尺寸变化时重建；_version 随之递增）。
    private Image _sharedImage;
    private DeviceMemory _sharedMemory;
    private ImageView _sharedImageView;
    private int _texW, _texH;
    private nint _exportedMemoryHandle;   // 导出的外部内存句柄：Windows=HANDLE，Linux/Android=fd（int 经 nint 传递）
    private ulong _version;
    /// <summary>共享离屏外部内存的真实分配字节数（= 生产者侧 vkGetImageMemoryRequirements().size）。
    /// 随 <see cref="SharedGpuSurfaceDescriptor"/> 交合成器：Avalonia 导入 OPAQUE_FD 时以此与自身
    /// 计算的内存需求做严格相等校验，不符即抛 "Invalid memory size"（真机实证：留 0 必不出画）。
    /// 注意不是 w*h*4 —— 驱动按 tile/对齐会扩到更大值，只能以 vkGetImageMemoryRequirements 为准。</summary>
    private ulong _sharedMemorySize;

    // 当前共享图像的创建参数快照（仅诊断用）：requirements 是 usage/flags 的函数，
    // 转移口日志打印它们可与宿主合成器侧的建图参数逐位对表。
    private ImageUsageFlags _sharedUsage;
    private ImageCreateFlags _sharedFlags;

    // Android 零拷贝稳健层：解码侧 AHB 仅作 SOURCE——经 YCbCr 转换渲进 plain 内部 RGBA 图像
    // （_convertImage，用法与 VulkanGpuFrameProducer.TryCreateRgbaTarget 完全同款），再 vkCmdCopyImage
    // 拷进普通 Vulkan 外部图像 _sharedImage（OpaqueFd 导出交合成器）。此 GPU→GPU 拷贝为零 CPU 像素拷贝；
    // _sharedImage 现为普通 Vulkan 图像（与 Linux 完全一致），规避 AHB gralloc fd 经 OPAQUE_FD 重导入的
    // stride 失配（VK_ERROR_INVALID_EXTERNAL_HANDLE_KHR，见 3.txt）。仅 Android 启用内部 _convertImage。
    private Image _convertImage;
    private DeviceMemory _convertMemory;
    private ImageView _convertView;

    // _sharedImage（导出离屏）当前是否已进入 TransferDstOptimal（跨命令缓冲持久；尺寸变化时重建归零）。
    private bool _sharedImageCopyReady;

    // Android AHB 诊断/自提交标志：AHB 路径改为「转换」「拷贝」两步分提交以隔离 Mali DEVICE_LOST 真因
    // （① AHB YCbCr 采样 还是 ② 写入导入的 AHB 离屏）。置位后 TryWriteFrame 跳过公共提交段（已在内部完成）。
    private bool _ahbSelfSubmitted;

    // AHB YCbCr 转换建议值诊断仅打印一次。
    private bool _ahbYcbcrDiagLogged;
    private bool _ahbRgbaDiagLogged;

    // 信号量对（长期对象，随源创建/释放；消费方导入一次长期使用）。
    private Semaphore _consumerWaitSem;
    private Semaphore _consumerSignalSem;
    private nint _consumerWaitHandle;
    private nint _consumerSignalHandle;

    private bool _disposed;

    // 出帧路径区分（零拷贝 vs 软帧上传）：一次性公告 + 周期计数，供真机日志区分帧来源与转移口。
    private long _writtenFrames;
    private bool _announcedZeroCopy;
    private bool _announcedSoft;
    private const int FrameLogInterval = 30;

    // ── Android 共享离屏：与 Linux 走完全相同的 OPAQUE_FD 导出路径 ──
    // 治根结论（本轮联网核实 + 2.txt 实证）：Android Vulkan（Mali/Adreno 等）完全支持
    // VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_FD_BIT，可经 vkGetMemoryFdKHR 导出 dma_buf fd 交合成器
    // VulkanOpaquePosixFileDescriptor 导入上屏。AHB 句柄类型仅是「额外支持」而非 OPAQUE_FD 的替代品。
    // 此前数轮误入「自分配 AHardwareBuffer + 反向解析 GraphicBuffer/native_handle_t 的 C++ 内存布局抠 fd」
    // 的脆弱死路（android_native_base_t 版本/厂商布局不兼容、AHardwareBuffer 与 GraphicBuffer 无继承契约，
    // 硬编码偏移读到错误 magic → 2.txt:473 `读得 0xFCDF8028，期望 0x6E4AA411` → 优雅回退 Skia、Android 不出画）。
    // 现删除整条 AHB 自分配+结构反向解析代码，Android 直接复用 Linux 已验证可用的 ExportMemoryAllocateInfo(OPAQUE_FD)
    // + vkGetMemoryFdKHR 写法（版本/厂商无关、AOT 安全、零 P/Invoke 到 libandroid.so）。

    /// <summary>
    /// 初始化 <see cref="VulkanSharedSurfaceSource"/> 的新实例。
    /// </summary>
    /// <param name="factory">Vulkan 渲染器工厂（持有共享 Vulkan 设备与设备身份）。</param>
    /// <param name="logger">日志。</param>
    internal VulkanSharedSurfaceSource(VulkanRendererFactory factory, ILogger<VulkanSharedSurfaceSource> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _device = factory.SharedDevice;
        _physicalDevice = factory.SharedPhysicalDevice;
        _queue = factory.SharedQueue;
        _queueFamilyIndex = factory.SharedQueueFamilyIndex;
        _isWindows = OperatingSystem.IsWindows();
        _isApple = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();
        _isAndroid = OperatingSystem.IsAndroid();
        // Android 共享离屏与整条 RGBA 链路统一用 R8G8B8A8：与合成器导入侧 VulkanOpaquePosixFileDescriptor
        // 的 R8G8B8A8UNorm 严格一致（OPAQUE_FD 跨进程不携带格式元数据，须生产者/消费者约定相同 RGBA 排列）。
        // 其余平台保持 B8G8R8A8（与桌面合成器约定一致）。
        _surfaceVkFormat = _isAndroid ? Format.R8G8B8A8Unorm : Format.B8G8R8A8Unorm;
        _surfaceFormatEnum = _isAndroid ? SharedGpuSurfaceFormat.R8G8B8A8UNorm : SharedGpuSurfaceFormat.B8G8R8A8UNorm;

        if (_isWindows)
        {
            _handleKind = SharedGpuHandleKind.VulkanOpaqueNtHandle;
            _semaphoreKind = SharedGpuSemaphoreKind.VulkanOpaqueNtHandle;
            _memHandleType = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit;
            _semHandleType = ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit;
            _syncMode = SharedGpuSyncMode.Semaphores;
        }
        else if (_isApple)
        {
            // Apple / MoltenVK：无空域零拷贝经 VK_EXT_metal_objects 导出 IOSurface（图像）
            // 与 MTLSharedEvent（信号量），不使用 external_memory / external_semaphore 扩展。
            // 这两个 Vulkan 标志字段在 Apple 路径下不被使用（保持 0）。
            _handleKind = SharedGpuHandleKind.IOSurfaceRef;
            _semaphoreKind = SharedGpuSemaphoreKind.MetalSharedEvent;
            _memHandleType = 0;
            _semHandleType = 0;
            _syncMode = SharedGpuSyncMode.Semaphores;
        }
        else if (_isAndroid)
        {
            // Android 共享离屏：Adreno 等移动 GPU 拒绝把「普通 Vulkan 外部图像」经 OPAQUE_FD 导出
            // （vkBindImageMemory 报 ErrorInvalidExternalHandle），但 AHB 外部内存（VK_ANDROID_external_
            // memory_android_hardware_buffer）是原生支持的。故共享面改用 AHB 承载（图像/分配均标 AHB 句柄
            // 类型），再用 vkGetMemoryAndroidHardwareBufferANDROID 取回 AHardwareBuffer，经 AndroidAhbFdExport
            // 抽其底层 dma_buf fd（native_handle_t.data[0] + dup）作为 OPAQUE_FD 交合成器——Avalonia Android
            // ImportImage 仅接受 OPAQUE_FD。_handleKind 仍是 VulkanOpaquePosixFileDescriptor（交付给合成器的就是 fd）。
            // 此前数轮误以为「Android 拿不到 dma_buf fd 必须自分配 AHardwareBuffer 再反向解析
            // GraphicBuffer/native_handle_t 的 C++ 内存布局抠 fd」——该前提错误（硬编码偏移读到错误 magic，
            // 2.txt:473 `读得 0xFCDF8028，期望 0x6E4AA411` → 回退 Skia、不出画）。现改为「AHB 承载 + 标准
            // dlsym(libnativewindow.so, AHardwareBuffer_getNativeHandle) 取 fd」，版本/厂商无关、AOT 安全。
            // 同步：移动端驱动不支持二进制外部信号量导出（较早轮次 vkCreateSemaphore →
            // VK_ERROR_OUT_OF_HOST_MEMORY），故用 SharedGpuSyncMode.None，由合成器（UpdateAsync）自管同步。
            _handleKind = SharedGpuHandleKind.VulkanOpaquePosixFileDescriptor;
            _semaphoreKind = SharedGpuSemaphoreKind.VulkanOpaquePosixFileDescriptor;
            _memHandleType = ExternalMemoryHandleTypeFlags.AndroidHardwareBufferBitAndroid;
            _semHandleType = ExternalSemaphoreHandleTypeFlags.OpaqueFDBit;
            _syncMode = SharedGpuSyncMode.None;
        }
        else
        {
            // Linux：Vulkan 合成器经 VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_FD_BIT 导入 POSIX
            // 文件描述符（dma_buf），并以外部信号量（OpaqueFDBit）做跨端握手同步。
            _handleKind = SharedGpuHandleKind.VulkanOpaquePosixFileDescriptor;
            _semaphoreKind = SharedGpuSemaphoreKind.VulkanOpaquePosixFileDescriptor;
            _memHandleType = ExternalMemoryHandleTypeFlags.OpaqueFDBit;
            _semHandleType = ExternalSemaphoreHandleTypeFlags.OpaqueFDBit;
            _syncMode = SharedGpuSyncMode.Semaphores;
        }

        // 离屏渲染管线：self-contained（着色器/描述符/管线布局/采样器/离屏 RenderPass 由
        // EnsureOffscreenResources 懒创建），每共享表面源实例独立持有，与 SwapChain 路径的
        // VulkanRenderer._shaderPipeline 对称（VulkanRenderer.cs:1300）。不初始化则 TryWriteFrame
        // 在 _pipeline!.EnsureOffscreenResources 处 NullReferenceException。
        _pipeline = new VulkanShaderPipeline(_physicalDevice, _device);

        CreateCommandResources();
        if (_syncMode != SharedGpuSyncMode.None)
            CreateSemaphores();
    }

    /// <inheritdoc/>
    public SharedGpuHandleKind HandleKind => _handleKind;

    /// <inheritdoc/>
    public SharedGpuSyncMode SyncMode => _syncMode;

    /// <inheritdoc/>
    public ulong ConsumerAcquireKey => UnusedKey;

    /// <inheritdoc/>
    public ulong ConsumerReleaseKey => UnusedKey;

    /// <inheritdoc/>
    public SharedGpuSemaphorePair? Semaphores =>
        _consumerWaitHandle != IntPtr.Zero && _consumerSignalHandle != IntPtr.Zero
            ? new SharedGpuSemaphorePair(_consumerWaitHandle, _consumerSignalHandle, _semaphoreKind)
            : null;

    /// <inheritdoc/>
    public bool TryWriteFrame(VideoFrame frame, out SharedGpuSurfaceDescriptor descriptor)
    {
        descriptor = default;
        if (_disposed)
            return false;
        if (frame.Resource is null)
            return false;

        // 每帧重置 AHB 自提交标志（仅 Android AHB 零拷贝路径在 TryRecordAhbConversion 内部分步自提交后置位）。
        _ahbSelfSubmitted = false;

        int w, h;
        bool recorded;
        string? path = null;
        if (frame.Resource is AndroidHardwareBufferFrameResource ahb)
        {
            // 真零拷贝路径（Layer 3）：AHB（YUV 外部格式）经 Vulkan 导入 + YCbCr 转换直转进共享离屏表面，
            // 全程零 CPU 像素拷贝；复用 Layer 2 已就绪的导出/上屏机制（NDK AHB → dma_buf fd → 合成器）。
            // 这是「GPU 出帧」：解码侧产出的 AHardwareBuffer 直接在 GPU 内被采样转换，无像素回读。
            if (!_announcedZeroCopy)
            {
                _announcedZeroCopy = true;
                _logger.LogInformation("[VULKAN-SHARED] 路径锁定=ZERO-COPY(AHB→GPU YCbCr零拷贝) — GPU 出帧，无 CPU 像素拷贝。");
            }
            path = "ZERO-COPY(AHB→GPU)";
            w = ahb.Width;
            h = ahb.Height;
            EnsureSharedSurface(w, h);
            _pipeline!.EnsureOffscreenResources(_surfaceVkFormat, new Extent2D((uint)w, (uint)h), _isAndroid ? _convertView : _sharedImageView);
            recorded = TryRecordAhbConversion(ahb, w, h);
        }
        else if (frame.Resource is SoftwareFrameResource sw)
        {
            // 软帧路径：YUV 平面经 CPU 上传 + YUV→RGB shader 写入共享离屏表面（仍 GPU 上屏，但非真零拷贝——
            // 解码侧已做 CPU 像素提取，此处仅为「软帧上屏」）。
            if (!_announcedSoft)
            {
                _announcedSoft = true;
                _logger.LogInformation("[VULKAN-SHARED] 路径锁定=SOFT-UPLOAD(CPU YUV→GPU 上传) — 软帧上屏，非零拷贝。");
            }
            path = "SOFT-UPLOAD(CPU→GPU)";
            w = sw.Width;
            h = sw.Height;
            EnsureSharedSurface(w, h);
            _pipeline!.EnsureOffscreenResources(_surfaceVkFormat, new Extent2D((uint)w, (uint)h), _isAndroid ? _convertView : _sharedImageView);
            recorded = TryRecordSoftwareUpload(sw, w, h);
        }
        else
        {
            return false; // 不支持的帧类型 → 交回回退
        }

        if (!recorded)
            return false;

        // Android AHB 零拷贝路径已在 TryRecordAhbConversion 内部分步自提交（隔离 Mali DEVICE_LOST 真因），
        // 命令缓冲不归公共提交段管理，跳过 EndCommandBuffer/QueueSubmit/WaitForFences，直接导出描述符。
        if (!_ahbSelfSubmitted)
        {
            // 公共提交段：EndCommandBuffer → QueueSubmit（可选信号量握手）→ 有限超时 WaitForFences。
            Result result = VulkanNative.EndCommandBuffer(_commandBuffer);
            if (result != Result.Success)
            {
                _logger.LogWarning("Vulkan 共享表面 EndCommandBuffer 失败：{Result}", result);
                return false;
            }

            // 生产者等待消费方归还（ConsumerSignal）→ 写完后 signal 消费方等待（ConsumerWait）。
            // None 模型（Android）：移动端驱动不支持二进制外部信号量导出，提交不带信号量，
            // 由 Avalonia 合成器（UpdateAsync）自管跨端同步；仅以 Fence 保证自身 GPU 写完成。
            Fence fence = _frameFence;
            CommandBuffer cmdBuf = _commandBuffer;
            VulkanNative.ResetFences(_device, 1, &fence);
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmdBuf,
            };
            if (_syncMode != SharedGpuSyncMode.None)
            {
                Semaphore waitSem = _consumerSignalSem;
                Semaphore signalSem = _consumerWaitSem;
                PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
                submitInfo.WaitSemaphoreCount = 1;
                submitInfo.PWaitSemaphores = &waitSem;
                submitInfo.PWaitDstStageMask = &waitStage;
                submitInfo.SignalSemaphoreCount = 1;
                submitInfo.PSignalSemaphores = &signalSem;
            }

            result = VulkanNative.QueueSubmit(_queue, 1, &submitInfo, (nint)fence.Handle);
            if (result != Result.Success)
            {
                _logger.LogWarning("Vulkan 共享表面 QueueSubmit 失败：{Result}", result);
                return false;
            }

            // 有限超时等待 GPU 完成（与 D3D11 16ms keyed mutex 超时对称）——超时=消费方未归还 → 丢帧。
            Result waitR = VulkanNative.WaitForFences(_device, 1, &fence, 1u, WriteWaitTimeoutNs);
            if (waitR == Result.Timeout)
            {
                _logger.LogTrace("Vulkan 共享表面等待消费方归还超时，跳过本帧。");
                return false;
            }
            if (waitR != Result.Success)
            {
                _logger.LogWarning("Vulkan 共享表面 WaitForFences 失败：{Result}", waitR);
                return false;
            }
        }

        // ── 0拷贝帧出参 / 转移口：将本帧共享离屏表面的外部内存句柄 + 版本 + 同步模型打包交合成器 ──
        // 此处即「帧转移不拷贝」的交付点：调用方（合成器）仅持 SharedGpuSurfaceDescriptor，不感知源像素布局。
        // 零拷贝 = 本描述符携带外部内存句柄（fd / HANDLE / IOSurface），而非像素副本；软帧同样经此口交付。
        // 【兜底】绝不允许带着 MemorySize=0 交付 —— Avalonia 导入 OPAQUE_FD 时以此做严格相等校验，
        // 0 必然抛 "Invalid memory size"（真机实证：连续 27 帧导入失败 → 30 帧后整链回退 Skia）。
        // 正常路径已在分配点记录；此处现查只是防御，代价一次 vkGetImageMemoryRequirements（纳秒级）。
        if (_sharedMemorySize == 0)
        {
            MemoryRequirements fallbackReq;
            VulkanNative.GetImageMemoryRequirements(_device, _sharedImage, &fallbackReq);
            _sharedMemorySize = fallbackReq.Size;
            _logger.LogWarning(
                "[VULKAN-SHARED] MemorySize 在分配点未记录，交付前现查兜底={Size}（应排查分配点为何漏记）。",
                fallbackReq.Size);
        }
        if ((_writtenFrames % FrameLogInterval) == 0)
            _logger.LogInformation(
                "[VULKAN-SHARED] 转移口 帧#{N} 路径={Path} 出参Kind={Kind} version={Ver} sync={Sync} {W}x{H} mem={Mem} usage=0x{Usage:X} flags=0x{Flags:X}",
                _writtenFrames, path, _handleKind, _version, _syncMode, w, h, _sharedMemorySize,
                (uint)_sharedUsage, (uint)_sharedFlags);
        _writtenFrames++;

        descriptor = new SharedGpuSurfaceDescriptor(
            _exportedMemoryHandle,
            _handleKind,
            w, h,
            _surfaceFormatEnum,
            _version,
            _syncMode,
            _sharedMemorySize,
            0);
        return true;
    }

    /// <summary>记录软帧上传（CPU YUV 平面 → GPU 纹理 → YUV→RGB shader 写入共享离屏表面）。返回是否成功记录。</summary>
    private bool TryRecordSoftwareUpload(SoftwareFrameResource sw, int w, int h)
    {
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        if (VulkanNative.BeginCommandBuffer(_commandBuffer, ref beginInfo) != Result.Success)
        {
            _logger.LogWarning("Vulkan 共享表面 BeginCommandBuffer（软帧）失败。");
            return false;
        }
        try
        {
            _pipeline!.PresentOffscreen(sw, _commandBuffer, (0, 0, w, h), (0f, 0f, 1f, 1f));

            // Android：软帧已渲进 plain 内部 _convertImage（离屏 RenderPass FinalLayout=ColorAttachmentOptimal），
            // 拷进 AHB 离屏 _sharedImage（仅 TRANSFER_DST，导出交合成器）。
            if (_isAndroid)
                CopyToSharedImage(_commandBuffer, ImageLayout.ColorAttachmentOptimal, w, h);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vulkan 共享表面软帧上传记录失败。");
            VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
            return false;
        }
    }

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
    /// 复用 <c>VulkanGpuFrameProducer.TryImportAndroidAHardwareBuffer</c> 已真机验证的导入范式
    /// （含 <c>Buffer=(nint*)ahb.AhbHandle</c> 传值，规避 SIGBUS）。
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
            //    （SIGBUS 根因：此前误传 &localVar 致驱动解引用栈地址 → BUS_ADRALN）。
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
            //    Android 走「分步提交」以隔离 Mali DEVICE_LOST 真因：先单独提交 ① 转换（AHB YCbCr 采样），
            //    再单独提交 ② 拷贝（写入导入的 AHB 离屏 _sharedImage）。任一步 DEVICE_LOST 即在日志定位。
            Image convertTarget = _isAndroid ? _convertImage : _sharedImage;
            ImageView convertView = _isAndroid ? _convertView : _sharedImageView;
            _logger.LogInformation("[AHB-DIAG] ▶ 进入 Convert（AHB→_convertImage GPU 绘制）{W}x{H}", w, h);
            _ycbcrConverter.Convert(_commandBuffer, ahbImage, ahbView, (uint)w, (uint)h,
                _surfaceVkFormat, convertTarget, convertView);
            _logger.LogInformation("[AHB-DIAG] ✓ Convert 记录完成，准备分步提交①");

            // 6) Android：分步提交。先提交并等待 ① 转换（AHB→_convertImage），隔离 AHB 采样是否触发 fault。
            if (_isAndroid)
            {
                if (!SubmitAhbStep("①AHB-YCbCr采样转换", w, h))
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

                // 7) ② 拷贝：_convertImage(TransferSrcOptimal) → AHB 离屏 _sharedImage（仅 TRANSFER_DST）。
                VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
                CommandBufferBeginInfo begin2 = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                if (VulkanNative.BeginCommandBuffer(_commandBuffer, ref begin2) != Result.Success)
                {
                    _logger.LogWarning("[AHB-DIAG] ② 拷贝 BeginCommandBuffer 失败。");
                    return false;
                }
                CopyToSharedImage(_commandBuffer, ImageLayout.TransferSrcOptimal, w, h);
                if (!SubmitAhbStep("②拷贝进AHB离屏_sharedImage", w, h))
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

            // 5) AHB 源（普通 RGBA）→ 渲染进内部 _convertImage（Android）/ _sharedImage（非 Android）。
            Image convertTarget = _isAndroid ? _convertImage : _sharedImage;
            ImageView convertView = _isAndroid ? _convertView : _sharedImageView;
            _logger.LogInformation("[AHB-DIAG] ▶ 进入 Convert（RGBA AHB→_convertImage 普通采样）{W}x{H}", w, h);
            _rgbaConverter.Convert(_commandBuffer, ahbImage, ahbView, (uint)w, (uint)h, convertTarget, convertView);
            _logger.LogInformation("[AHB-DIAG] ✓ Convert 记录完成，准备分步提交①");

            // 6) Android：分步提交。先提交并等待 ① 转换（RGBA AHB 采样），隔离采样是否触发 fault。
            if (_isAndroid)
            {
                if (!SubmitAhbStep("①RGBA-AHB采样转换", w, h))
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

                // 7) ② 拷贝：_convertImage(TransferSrcOptimal) → AHB 离屏 _sharedImage（仅 TRANSFER_DST）。
                VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
                CommandBufferBeginInfo begin2 = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                if (VulkanNative.BeginCommandBuffer(_commandBuffer, ref begin2) != Result.Success)
                {
                    _logger.LogWarning("[AHB-DIAG] ② 拷贝 BeginCommandBuffer 失败（RGBA）。");
                    return false;
                }
                CopyToSharedImage(_commandBuffer, ImageLayout.TransferSrcOptimal, w, h);
                if (!SubmitAhbStep("②拷贝进AHB离屏_sharedImage", w, h))
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
    /// Android 稳健层：把已渲染进 plain 内部 <see cref="_convertImage"/> 的帧经 vkCmdCopyImage 拷进 AHB 离屏
    /// <see cref="_sharedImage"/>（仅 TRANSFER_DST，导出交合成器）。<see cref="_sharedImage"/> 为 AHB 支持内存，
    /// 直接作 color attachment 在 Mali 上触发 GROUP_ERROR_FATAL / DEVICE_LOST，故一律经内部图中转。
    /// 软帧路径 srcLayout=ColorAttachmentOptimal（离屏 RenderPass FinalLayout）；AHB 路径 srcLayout=TransferSrcOptimal
    /// （转换器 Convert 末态）。<see cref="_sharedImage"/> 首帧由 Undefined 转入 TransferDstOptimal，后续帧保持
    /// TransferDstOptimal（由 <see cref="_sharedImageCopyReady"/> 追踪，跨命令缓冲持久）。
    /// </summary>
    private void CopyToSharedImage(CommandBuffer cmd, ImageLayout srcLayout, int w, int h)
    {
        // 1) _convertImage → TransferSrcOptimal（软帧路径需转；AHB 路径已是，跳过）。
        if (srcLayout != ImageLayout.TransferSrcOptimal)
        {
            ImageMemoryBarrier srcBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = srcLayout,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = ~0u,
                DstQueueFamilyIndex = ~0u,
                Image = _convertImage,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            VulkanNative.CmdPipelineBarrier(cmd, PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &srcBarrier);
        }

        // 2) _sharedImage → TransferDstOptimal（首帧 Undefined，后续帧已是 TransferDstOptimal）。
        //    首帧无前序写入可等（TopOfPipe + 0 访问）；后续帧须等上一帧 TRANSFER 写入完成再覆盖。
        bool firstCopy = !_sharedImageCopyReady;
        ImageLayout dstOld = firstCopy ? ImageLayout.Undefined : ImageLayout.TransferDstOptimal;
        PipelineStageFlags dstSrcStage = firstCopy ? PipelineStageFlags.TopOfPipeBit : PipelineStageFlags.TransferBit;
        AccessFlags dstSrcAccess = firstCopy ? 0 : AccessFlags.TransferWriteBit;
        ImageMemoryBarrier dstBarrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = dstSrcAccess,
            DstAccessMask = AccessFlags.TransferWriteBit,
            OldLayout = dstOld,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = ~0u,
            DstQueueFamilyIndex = ~0u,
            Image = _sharedImage,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        VulkanNative.CmdPipelineBarrier(cmd, dstSrcStage,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &dstBarrier);

        // 3) vkCmdCopyImage：_convertImage(TransferSrcOptimal) → _sharedImage(TransferDstOptimal)。
        ImageCopy region = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            SrcOffset = new Offset3D(0, 0, 0),
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            DstOffset = new Offset3D(0, 0, 0),
            Extent = new Extent3D((uint)w, (uint)h, 1),
        };
        VulkanNative.CmdCopyImage(cmd, _convertImage, ImageLayout.TransferSrcOptimal,
            _sharedImage, ImageLayout.TransferDstOptimal, 1, &region);

        // 标记 _sharedImage 已处于 TransferDstOptimal（供后续帧复用，避免重复 Undefined 重排）。
        _sharedImageCopyReady = true;
    }

    /// <summary>
    /// Android AHB 分步提交：把当前记录的 <see cref="_commandBuffer"/> 提交并等待，检测 Mali DEVICE_LOST，
    /// 精确定位 fault 落在 ① AHB YCbCr 采样 还是 ② 写入导入的 AHB 离屏。返回是否成功（无 device lost）。
    /// </summary>
    private bool SubmitAhbStep(string stepTag, int w, int h)
    {
        _logger.LogInformation("[AHB-DIAG] ▶ {Step} 进入提交（EndCommandBuffer 前）", stepTag);
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
            _logger.LogWarning("[AHB-DIAG] ★ {Step} 触发 Mali DEVICE_LOST（GROUP_ERROR_FATAL）—— 真因定位在此步。", stepTag);
            return false;
        }
        if (waitR != Result.Success)
        {
            _logger.LogWarning("[AHB-DIAG] {Step} WaitForFences 失败：{Result}", stepTag, waitR);
            return false;
        }
        _logger.LogInformation("[AHB-DIAG] ✓ {Step} 提交成功（无 DEVICE_LOST）{W}x{H}", stepTag, w, h);
        return true;
    }

    // ── 命令资源（命令池 + 命令缓冲 + 每帧 Fence）──
    private void CreateCommandResources()
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _queueFamilyIndex,
        };
        Result result = VulkanNative.CreateCommandPool(_device, ref poolInfo, null, out _commandPool);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateCommandPool（共享表面源）失败: {result}");

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        var cmds = stackalloc CommandBuffer[1];
        result = VulkanNative.AllocateCommandBuffers(_device, &allocInfo, cmds);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateCommandBuffers（共享表面源）失败: {result}");
        _commandBuffer = cmds[0];

        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        result = VulkanNative.CreateFence(_device, &fenceInfo, null, out _frameFence);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateFence（共享表面源）失败: {result}");
    }

    // ── 信号量对（导出 + 握手初始化）──
    private void CreateSemaphores()
    {
        if (_isApple)
        {
            CreateSemaphoresApple();
            return;
        }

        ExportSemaphoreCreateInfo extSemInfo = new()
        {
            SType = StructureType.ExportSemaphoreCreateInfo,
            HandleTypes = _semHandleType,
        };
        SemaphoreCreateInfo semInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = (void*)&extSemInfo,
        };

        Result r1 = VulkanNative.CreateSemaphore(_device, ref semInfo, null, out _consumerWaitSem);
        Result r2 = VulkanNative.CreateSemaphore(_device, ref semInfo, null, out _consumerSignalSem);
        if (r1 != Result.Success || r2 != Result.Success)
            throw new InvalidOperationException($"vkCreateSemaphore（共享表面信号量）失败: {r1}/{r2}");

        // 导出两个信号量的外部句柄，供消费方导入。
        if (_isWindows)
        {
            SemaphoreGetWin32HandleInfoKHR getWait = new()
            {
                SType = StructureType.SemaphoreGetWin32HandleInfoKhr,
                Semaphore = _consumerWaitSem,
                HandleType = _semHandleType,
            };
            SemaphoreGetWin32HandleInfoKHR getSignal = new()
            {
                SType = StructureType.SemaphoreGetWin32HandleInfoKhr,
                Semaphore = _consumerSignalSem,
                HandleType = _semHandleType,
            };
            Result h1 = VulkanNative.GetSemaphoreWin32HandleKHR(_device, &getWait, out nint hWait);
            Result h2 = VulkanNative.GetSemaphoreWin32HandleKHR(_device, &getSignal, out nint hSignal);
            if (h1 != Result.Success || h2 != Result.Success)
                throw new InvalidOperationException($"vkGetSemaphoreWin32HandleKHR 失败: {h1}/{h2}");
            _consumerWaitHandle = hWait;
            _consumerSignalHandle = hSignal;
        }
        else
        {
            SemaphoreGetFdInfoKHR getWait = new()
            {
                SType = StructureType.SemaphoreGetFDInfoKhr,
                Semaphore = _consumerWaitSem,
                HandleType = _semHandleType,
            };
            SemaphoreGetFdInfoKHR getSignal = new()
            {
                SType = StructureType.SemaphoreGetFDInfoKhr,
                Semaphore = _consumerSignalSem,
                HandleType = _semHandleType,
            };
            Result h1 = VulkanNative.GetSemaphoreFdKHR(_device, &getWait, out int fdWait);
            Result h2 = VulkanNative.GetSemaphoreFdKHR(_device, &getSignal, out int fdSignal);
            if (h1 != Result.Success || h2 != Result.Success)
                throw new InvalidOperationException($"vkGetSemaphoreFdKHR 失败: {h1}/{h2}");
            _consumerWaitHandle = (nint)fdWait;
            _consumerSignalHandle = (nint)fdSignal;
        }

        // 握手初始化：以一次 signal-only 提交把 ConsumerSignal 置为信号态，
        // 否则生产者首帧等待 ConsumerSignal 将永久阻塞。
        Semaphore bootstrapSem = _consumerSignalSem;
        SubmitInfo bootstrap = new()
        {
            SType = StructureType.SubmitInfo,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &bootstrapSem,
        };
        Result bootR = VulkanNative.QueueSubmit(_queue, 1, &bootstrap, default);
        if (bootR != Result.Success)
            throw new InvalidOperationException($"vkQueueSubmit（信号量握手初始化）失败: {bootR}");
        VulkanNative.QueueWaitIdle(_queue);
    }

    // ── Apple / MoltenVK：经 VK_EXT_metal_objects 把 Vulkan 信号量导出为 MTLSharedEvent ──
    private void CreateSemaphoresApple()
    {
        // 创建信号量时 pNext 链 ExportMetalObjectCreateInfoEXT（SharedEvent 位），
        // 告知 MoltenVK 该信号量应作为 Metal 共享事件创建。
        ExportMetalObjectCreateInfoEXT exportInfo = new()
        {
            SType = StructureType.ExportMetalObjectCreateInfoExt,
            ExportObjectType = ExportMetalObjectTypeFlagsEXT.SharedEventBitExt,
        };
        SemaphoreCreateInfo semInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = (void*)&exportInfo,
        };
        Result r1 = VulkanNative.CreateSemaphore(_device, ref semInfo, null, out _consumerWaitSem);
        Result r2 = VulkanNative.CreateSemaphore(_device, ref semInfo, null, out _consumerSignalSem);
        if (r1 != Result.Success || r2 != Result.Success)
            throw new InvalidOperationException($"vkCreateSemaphore（Apple 共享表面信号量）失败: {r1}/{r2}");

        _consumerWaitHandle = ExportMtlSharedEvent(_consumerWaitSem);
        _consumerSignalHandle = ExportMtlSharedEvent(_consumerSignalSem);
        if (_consumerWaitHandle == IntPtr.Zero || _consumerSignalHandle == IntPtr.Zero)
            throw new InvalidOperationException("vkExportMetalObjectsEXT 未能导出 MTLSharedEvent（VK_EXT_metal_objects 可能未启用）。");

        // 握手初始化：以一次 signal-only 提交把 ConsumerSignal 置为信号态，否则生产者首帧永久阻塞。
        Semaphore bootstrapSem = _consumerSignalSem;
        SubmitInfo bootstrap = new()
        {
            SType = StructureType.SubmitInfo,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &bootstrapSem,
        };
        Result bootR = VulkanNative.QueueSubmit(_queue, 1, &bootstrap, default);
        if (bootR != Result.Success)
            throw new InvalidOperationException($"vkQueueSubmit（Apple 信号量握手初始化）失败: {bootR}");
        VulkanNative.QueueWaitIdle(_queue);
    }

    private nint ExportMtlSharedEvent(Semaphore sem)
    {
        // 每次调用单独导出：pNext 链中同一结构体类型不应出现两次（规避 Vulkan valid usage），
        // 故两个信号量各走一次 vkExportMetalObjectsEXT。
        ExportMetalSharedEventInfoEXT evt = new()
        {
            SType = StructureType.ExportMetalSharedEventInfoExt,
            Semaphore = sem,
            Event = default,
        };
        ExportMetalObjectsInfoEXT metalsInfo = new()
        {
            SType = StructureType.ExportMetalObjectsInfoExt,
            PNext = (void*)&evt,
        };
        VulkanNative.ExportMetalObjectsEXT(_device, &metalsInfo);
        return evt.MtlSharedEvent;
    }

    /// <summary>
    /// 确保可外部导出离屏图像就绪，尺寸变化时重建底层图像并重新导出句柄（_version 递增）。
    /// </summary>
    private void EnsureSharedSurface(int w, int h)
    {
        if (_sharedImage.Handle != 0 && _texW == w && _texH == h)
            return;

        // 拆除旧图像（保留 _version 语义：重建才 +1）
        ReleaseSharedSurface();

        if (_isApple)
        {
            EnsureSharedSurfaceApple(w, h);
            return;
        }

        // 图像创建：启用外部内存导出（pNext=ExternalMemoryImageCreateInfo）。
        ExternalMemoryImageCreateInfo extImageInfo = new()
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = _memHandleType,
        };
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = _surfaceVkFormat,
            Extent = new Extent3D((uint)w, (uint)h, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            // _sharedImage 现为普通 Vulkan 外部图像（Android 同 Linux）：生产者把转换后的 RGBA 经
            // vkCmdCopyImage 拷入（TRANSFER_DST_BIT），合成器经 OPAQUE_FD 导入后作为采样纹理上屏。
            // Usage 与 Flags 必须与宿主合成器（Avalonia VulkanImageBase）逐位一致：
            // vkGetImageMemoryRequirements 是 image 创建参数的函数，Usage/Flags 不同会让
            // 驱动（实测 Adreno 650）给出不同 size，而 Avalonia 导入时按其自身 requirements
            // 对 MemorySize 做严格相等校验，不符即抛"Invalid memory size"→ 每帧导入失败。
            // 对齐值取自 Avalonia 12.1.1 反汇编：UsageFlags=0x17(TransferSrc|TransferDst|Sampled|
            // ColorAttachment)、Flags=MUTABLE_FORMAT。
            Usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit
                    | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit,
            Flags = ImageCreateFlags.CreateMutableFormatBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            PNext = (void*)&extImageInfo,
        };
        _sharedUsage = imageInfo.Usage;
        _sharedFlags = imageInfo.Flags;

        Result result = VulkanNative.CreateImage(_device, &imageInfo, null, out _sharedImage);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage（共享表面离屏）失败: {result}");

        // 内存分配：Windows 经 ExportMemoryAllocateInfo(OpaqueWin32)；Linux 经 ExportMemoryAllocateInfo
        // (OpaqueFd)；Android 经 ExportMemoryAllocateInfo(AndroidHardwareBufferBitAndroid)——AHB 导出强制
        // dedicated（与解码侧 AHB 导入对称：AHB 内存与图像 1:1）。Adreno 拒绝普通 Vulkan 图像的 OPAQUE_FD
        // 导出（vkBindImageMemory 报 ErrorInvalidExternalHandle），故 Android 改走 AHB 承载。
        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, _sharedImage, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);

        MemoryDedicatedAllocateInfo dedicated = default;
        if (_isAndroid)
        {
            // AHB 导出：内存须 dedicated 绑定到本图像（VK_ANDROID_external_memory_android_hardware_buffer）。
            dedicated = new MemoryDedicatedAllocateInfo
            {
                SType = StructureType.MemoryDedicatedAllocateInfo,
                Image = _sharedImage,
            };
        }

        ExternalMemoryHandleTypeFlags memHandle = _memHandleType;
        ExportMemoryAllocateInfo extMemInfo = new()
        {
            SType = StructureType.ExportMemoryAllocateInfo,
            HandleTypes = memHandle,
            PNext = _isAndroid ? (void*)&dedicated : null,
        };
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
            PNext = (void*)&extMemInfo,
        };
        result = VulkanNative.AllocateMemory(_device, &allocInfo, null, out _sharedMemory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory（共享表面离屏）失败: {result}");
        result = VulkanNative.BindImageMemory(_device, _sharedImage, _sharedMemory, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindImageMemory（共享表面离屏）失败: {result}");
        // 记录本次分配的真实字节数（= vkGetImageMemoryRequirements().size），随描述符交给合成器。
        // 【为什么必须填】Avalonia 的 VulkanExternalObjectsFeature.ImportedImage.CreateMemory 会拿
        // properties.MemorySize 与它自己 vkGetImageMemoryRequirements(导入图像).size 做**严格相等**校验，
        // 不等即抛 "Invalid memory size"（真机实证：留 0 → 每帧导入失败 → 不出画）。OPAQUE_FD 不携带
        // 内存元数据，此值只能由生产者如实上报。注意：不是 w*h*4 —— 驱动会按 tile/对齐扩到更大。
        _sharedMemorySize = memReq.Size;

        // 导出内存句柄（交合成器）：
        //  - Windows：HANDLE（OpaqueWin32）。
        //  - Android：AHB 承载 → vkGetMemoryAndroidHardwareBufferANDROID 取回 AHardwareBuffer →
        //    AndroidAhbFdExport 抽 dma_buf fd（OPAQUE_FD）交合成器（Avalonia Android 仅接受 OPAQUE_FD）。
        //  - Linux：OpaqueFd dma_buf（vkGetMemoryFdKHR）。
        if (_isWindows)
        {
            MemoryGetWin32HandleInfoKHR getInfo = new()
            {
                SType = StructureType.MemoryGetWin32HandleInfoKhr,
                Memory = _sharedMemory,
                HandleType = memHandle,
            };
            Result hR = VulkanNative.GetMemoryWin32HandleKHR(_device, &getInfo, out nint hMem);
            if (hR != Result.Success)
                throw new InvalidOperationException($"vkGetMemoryWin32HandleKHR 失败: {hR}");
            _exportedMemoryHandle = hMem;
        }
        else if (_isAndroid)
        {
            // AHB 承载导出：取回 AHardwareBuffer 并抽取底层 dma_buf fd（dup 后独立所有）。
            MemoryGetAndroidHardwareBufferInfoANDROID getAhb = new()
            {
                SType = StructureType.MemoryGetAndroidHardwareBufferInfoAndroid,
                Memory = _sharedMemory,
                PNext = null,
            };
            Result ahbR = VulkanNative.GetMemoryAndroidHardwareBufferANDROID(_device, &getAhb, out nint ahb);
            if (ahbR != Result.Success)
                throw new InvalidOperationException($"vkGetMemoryAndroidHardwareBufferANDROID 失败: {ahbR}");
            try
            {
                if (!AndroidAhbFdExport.TryGetDmaBufFd(ahb, out int dmaFd))
                    throw new InvalidOperationException("AHB→dma_buf fd 提取失败（libnativewindow 符号缺失或 fd 无效）");
                _exportedMemoryHandle = (nint)dmaFd;
            }
            finally
            {
                // 释放我们导出的 AHB 引用；Vulkan 内存仍持有 dma_buf，fd 独立存活。
                AndroidAhbFdExport.Release(ahb);
            }
        }
        else
        {
            // Linux：经 vkGetMemoryFdKHR 导出 opaque fd（dma_buf）。
            MemoryGetFdInfoKHR getInfo = new()
            {
                SType = StructureType.MemoryGetFDInfoKhr,
                Memory = _sharedMemory,
                HandleType = memHandle,
            };
            Result hR = VulkanNative.GetMemoryFdKHR(_device, &getInfo, out int fd);
            if (hR != Result.Success)
                throw new InvalidOperationException($"vkGetMemoryFdKHR 失败: {hR}");
            _exportedMemoryHandle = (nint)fd;
        }

        // ImageView（由本源持有并随图像释放；传给管线作离屏渲染目标）。
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _sharedImage,
            ViewType = ImageViewType.Type2D,
            Format = _surfaceVkFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        result = VulkanNative.CreateImageView(_device, &viewInfo, null, out _sharedImageView);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView（共享表面离屏）失败: {result}");

        // Android：额外创建 plain 内部 RGBA 转换目标（color attachment + transfer src），
        // 所有转换/上传先渲进它，再 vkCmdCopyImage 拷进 AHB 离屏（规避 AHB 作 color attachment
        // 在 Mali 上触发的 GROUP_ERROR_FATAL / DEVICE_LOST）。仅 Android 启用。
        if (_isAndroid)
        {
            ImageCreateInfo convertInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = _surfaceVkFormat,
                Extent = new Extent3D((uint)w, (uint)h, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            if (VulkanNative.CreateImage(_device, &convertInfo, null, out _convertImage) != Result.Success)
                throw new InvalidOperationException("vkCreateImage（Android 内部转换目标）失败。");
            MemoryRequirements convertMemReq;
            VulkanNative.GetImageMemoryRequirements(_device, _convertImage, &convertMemReq);
            uint convertMemType = FindMemoryType(convertMemReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
            var convertAlloc = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = convertMemReq.Size,
                MemoryTypeIndex = convertMemType,
            };
            if (VulkanNative.AllocateMemory(_device, &convertAlloc, null, out _convertMemory) != Result.Success)
                throw new InvalidOperationException("vkAllocateMemory（Android 内部转换目标）失败。");
            if (VulkanNative.BindImageMemory(_device, _convertImage, _convertMemory, 0) != Result.Success)
                throw new InvalidOperationException("vkBindImageMemory（Android 内部转换目标）失败。");
            ImageViewCreateInfo convertViewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _convertImage,
                ViewType = ImageViewType.Type2D,
                Format = _surfaceVkFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            if (VulkanNative.CreateImageView(_device, &convertViewInfo, null, out _convertView) != Result.Success)
                throw new InvalidOperationException("vkCreateImageView（Android 内部转换目标）失败。");
        }

        _texW = w;
        _texH = h;
        _version++;
    }

        // ── Apple / MoltenVK：经 VK_EXT_metal_objects 把 Vulkan 离屏图像导出为 IOSurface ──
    private void EnsureSharedSurfaceApple(int w, int h)
    {
        // 图像创建：pNext 链 ExportMetalObjectCreateInfoEXT（IOSurface 位），告知 MoltenVK
        // 此图像可经 vkExportMetalObjectsEXT 导出为 IOSurface。当前 VK_EXT_metal_objects
        // 不再要求 VK_IMAGE_CREATE_METAL_COMPATIBLE_BIT_EXT 创建标志（已从 VkImageCreateFlagBits 移除）。
        ExportMetalObjectCreateInfoEXT metalImageInfo = new()
        {
            SType = StructureType.ExportMetalObjectCreateInfoExt,
            ExportObjectType = ExportMetalObjectTypeFlagsEXT.IosurfaceBitExt,
        };
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.B8G8R8A8Unorm,
            Extent = new Extent3D((uint)w, (uint)h, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            PNext = (void*)&metalImageInfo,
        };
        Result result = VulkanNative.CreateImage(_device, &imageInfo, null, out _sharedImage);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage（Apple 共享表面离屏）失败: {result}");

        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, _sharedImage, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);

        // MoltenVK 自行管理 IOSurface 底层内存，普通设备本地分配即可（无需 ExportMemoryAllocateInfo）。
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
        };
        result = VulkanNative.AllocateMemory(_device, &allocInfo, null, out _sharedMemory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory（Apple 共享表面离屏）失败: {result}");
        result = VulkanNative.BindImageMemory(_device, _sharedImage, _sharedMemory, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindImageMemory（Apple 共享表面离屏）失败: {result}");
        // 与 OPAQUE_FD 路径同理：记录真实分配字节数（IOSurface 路径合成器侧同样做严格相等校验）。
        _sharedMemorySize = memReq.Size;

        // 导出 IOSurface（持久，随图像生命周期；消费方 Avalonia 经 IOSurfaceRef 直接导入采样）。
        _exportedMemoryHandle = ExportIOSurface(_sharedImage);
        if (_exportedMemoryHandle == IntPtr.Zero)
            throw new InvalidOperationException("vkExportMetalObjectsEXT 未能导出 IOSurface（VK_EXT_metal_objects 可能未启用）。");

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _sharedImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.B8G8R8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        result = VulkanNative.CreateImageView(_device, &viewInfo, null, out _sharedImageView);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView（Apple 共享表面离屏）失败: {result}");

        _texW = w;
        _texH = h;
        _version++;
    }

    private nint ExportIOSurface(Image image)
    {
        ExportMetalIOSurfaceInfoEXT iosurf = new()
        {
            SType = StructureType.ExportMetalIOSurfaceInfoExt,
            Image = image,
        };
        ExportMetalObjectsInfoEXT metalsInfo = new()
        {
            SType = StructureType.ExportMetalObjectsInfoExt,
            PNext = (void*)&iosurf,
        };
        VulkanNative.ExportMetalObjectsEXT(_device, &metalsInfo);
        return iosurf.IoSurface;
    }

    private void ReleaseSharedSurface()
    {
        if (_device.Handle == 0) return;
        if (_sharedImageView.Handle != 0)
        {
            VulkanNative.DestroyImageView(_device, _sharedImageView, null);
            _sharedImageView = default;
        }
        if (_sharedImage.Handle != 0)
        {
            VulkanNative.DestroyImage(_device, _sharedImage, null);
            _sharedImage = default;
        }
        if (_sharedMemory.Handle != 0)
        {
            VulkanNative.FreeMemory(_device, _sharedMemory, null);
            _sharedMemory = default;
        }
        if (_convertView.Handle != 0)
        {
            VulkanNative.DestroyImageView(_device, _convertView, null);
            _convertView = default;
        }
        if (_convertImage.Handle != 0)
        {
            VulkanNative.DestroyImage(_device, _convertImage, null);
            _convertImage = default;
        }
        if (_convertMemory.Handle != 0)
        {
            VulkanNative.FreeMemory(_device, _convertMemory, null);
            _convertMemory = default;
        }
        // 导出的外部句柄随内存释放自动失效，无需 CloseHandle/dup；_version 保留递增语义。
        _exportedMemoryHandle = IntPtr.Zero;
        _sharedImageCopyReady = false;
        _texW = 0;
        _texH = 0;
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memProps;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &memProps);
        for (int i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << i)) != 0 &&
                (memProps.MemoryTypes[i].PropertyFlags & properties) == properties)
                return (uint)i;
        }
        throw new InvalidOperationException("未找到合适的 Vulkan 内存类型（共享表面离屏）。");
    }

    // AHB 导入选内存类型：取 memoryTypeBits 中首个 device-local 类型，无则回退首个可用位
    // （与解码侧 VulkanGpuFrameProducer.ExternalCompatibleMemoryType 一致）。
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

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_device.Handle != 0)
            VulkanNative.DeviceWaitIdle(_device);

        _pipeline?.Dispose();
        _pipeline = null;
        _ycbcrConverter?.Dispose();
        _ycbcrConverter = null;
        _rgbaConverter?.Dispose();
        _rgbaConverter = null;

        ReleaseSharedSurface();

        if (_frameFence.Handle != 0)
        {
            VulkanNative.DestroyFence(_device, _frameFence, null);
            _frameFence = default;
        }
        if (_commandPool.Handle != 0)
        {
            VulkanNative.DestroyCommandPool(_device, _commandPool, null);
            _commandPool = default;
        }
        if (_consumerWaitSem.Handle != 0)
        {
            VulkanNative.DestroySemaphore(_device, _consumerWaitSem, null);
            _consumerWaitSem = default;
        }
        if (_consumerSignalSem.Handle != 0)
        {
            VulkanNative.DestroySemaphore(_device, _consumerSignalSem, null);
            _consumerSignalSem = default;
        }
        _consumerWaitHandle = IntPtr.Zero;
        _consumerSignalHandle = IntPtr.Zero;
    }
}

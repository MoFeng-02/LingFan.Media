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
    private readonly SharedGpuHandleKind? _handleKindOverride;
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
    // Android 双缓冲（2 槽轮换写入/交付）：Skia Ganesh 对【同一 VkImage 句柄】的包装会命中纹理缓存、
    // 内容永不更新（真机实证：管线 985 帧全部呈现、渲染回调 30fps 活跃，屏幕永远定格第一次采样内容）；
    // 每帧换一个 VkImage 交付即绕开缓存。非 Android 走合成器 OPAQUE_FD 导入 + version 重建机制，单槽即可。
    private readonly Image[] _sharedImages = new Image[2];
    private readonly DeviceMemory[] _sharedMemories = new DeviceMemory[2];
    private readonly ImageView[] _sharedImageViews = new ImageView[2];
    private readonly bool[] _sharedCopyReady = new bool[2];
    private readonly ulong[] _sharedMemorySizes = new ulong[2];
    /// <summary>本帧写入并交付的槽位（Android 每帧翻转；非 Android 恒 0）。</summary>
    private int _sharedActive;
    // 槽位数：NativeImage（同 device 直采样）双缓冲轮换交付；fd/NT/IOSurface 形态单槽（与 Linux 一致）。
    private int _slotCount => _handleKind == SharedGpuHandleKind.VulkanNativeImage ? 2 : 1;
    private int _texW, _texH;
    private nint _exportedMemoryHandle;   // 导出的外部内存句柄：Windows=HANDLE，Linux/Android=fd（int 经 nint 传递）
    private ulong _version;
    /// <summary>当前交付槽位的共享离屏外部内存真实分配字节数（= vkGetImageMemoryRequirements().size）。
    /// 随 <see cref="SharedGpuSurfaceDescriptor"/> 交合成器：Avalonia 导入 OPAQUE_FD 时以此与自身
    /// 计算的内存需求做严格相等校验，不符即抛 "Invalid memory size"（真机实证：留 0 必不出画）。
    /// 注意不是 w*h*4 —— 驱动按 tile/对齐会扩到更大值，只能以 vkGetImageMemoryRequirements 为准。</summary>
    private ulong _sharedMemorySize => _sharedMemorySizes[_sharedActive];

    // 当前共享图像的创建参数快照（仅诊断用）：requirements 是 usage/flags 的函数，
    // 转移口日志打印它们可与宿主合成器侧的建图参数逐位对表。
    private ImageUsageFlags _sharedUsage;
    private ImageCreateFlags _sharedFlags;


    // ── 写入停摆看门狗 ──
    // TryWriteFrame 只在管线线程串行执行。此处仅记录「当前阶段 + 进入时刻」，由独立定时器在
    // 写入久未返回时告警，用于定位卡死在哪个原生调用；正常播放期间零日志输出。
    private long _writeSeq;
    private int _stageActive; // 0=空闲 1=写入中
    private string? _lastStage;
    private long _lastStageQpc;
    private long _stallMarkQpc;       // 上次告警对应的阶段进入时刻（阶段推进后重新武装）
    private long _stallNextReportQpc; // 同一阶段的重复告警节流（10s）
    private System.Threading.Timer? _stallTimer;

    /// <summary>记录当前写入阶段（Volatile 写，供看门狗定时器线程读取）。</summary>
    private void MarkStage(string stage)
    {
        Volatile.Write(ref _lastStage, stage);
        Volatile.Write(ref _lastStageQpc, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    /// <summary>懒启动停摆看门狗定时器（首次写入时创建，Dispose 时停止）。</summary>
    private void EnsureStallTimer()
    {
        if (_stallTimer is not null)
            return;
        var timer = new System.Threading.Timer(StallTick, null, 2000, 2000);
        if (Interlocked.CompareExchange(ref _stallTimer, timer, null) is not null)
            timer.Dispose();
    }

    private void StallTick(object? state)
    {
        if (_disposed || Volatile.Read(ref _stageActive) == 0)
            return;
        long stageStart = Volatile.Read(ref _lastStageQpc);
        double stuckSec = System.Diagnostics.Stopwatch.GetElapsedTime(stageStart).TotalSeconds;
        if (stuckSec < 3.0)
            return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (stageStart == Volatile.Read(ref _stallMarkQpc) && now < Volatile.Read(ref _stallNextReportQpc))
            return;
        Volatile.Write(ref _stallMarkQpc, stageStart);
        Volatile.Write(ref _stallNextReportQpc, now + 10 * System.Diagnostics.Stopwatch.Frequency);
        _logger.LogError(
            "[VSS-STALL] 共享表面写入疑似停摆：帧={Seq} 卡在={Stage} 已停留={Sec:F1}s（未返回=阻塞在该阶段内部的原生调用）",
            Volatile.Read(ref _writeSeq), Volatile.Read(ref _lastStage) ?? "<unknown>", stuckSec);
    }

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
    /// <param name="handleKindOverride">句柄形态覆盖（null = 按平台默认；见工厂同参数说明）。</param>
    internal VulkanSharedSurfaceSource(VulkanRendererFactory factory, ILogger<VulkanSharedSurfaceSource> logger, SharedGpuHandleKind? handleKindOverride = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _handleKindOverride = handleKindOverride;

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
            if (_handleKindOverride == SharedGpuHandleKind.VulkanOpaquePosixFileDescriptor)
            {
                // Android fd 形态（合成器零拷贝）：与 Linux 完全同路径——dedicated + OPAQUE_FD 导出
                //（vkGetMemoryFdKHR）+ 外部信号量握手。跨实例时代的 dedicated 导入死结（Adreno
                // 消费侧 INITIALIZATION_FAILED）源于跨设备/跨实例导入；宿主注入共享 device 后 fd 由
                // 同一 VkDevice 导入，该前提已化解（见 VulkanDeviceFactory remarks）。导入自检失败时
                // 消费方干净回退下一工厂，不影响既有 NativeImage 路径。
                _handleKind = SharedGpuHandleKind.VulkanOpaquePosixFileDescriptor;
                _semaphoreKind = SharedGpuSemaphoreKind.VulkanOpaquePosixFileDescriptor;
                _memHandleType = ExternalMemoryHandleTypeFlags.OpaqueFDBit;
                _semHandleType = ExternalSemaphoreHandleTypeFlags.OpaqueFDBit;
                _syncMode = SharedGpuSyncMode.Semaphores;
            }
            else
            {
                // Android（同 device Skia 直绘，R2/M2 2026-09-02）：宿主（Avalonia Android 入口）已把自建
                // VkDevice 注入本工厂（UseExternalDevice），UI 层的 Skia GPU 上下文与本源共用<b>同一 device、
                // 同一图形队列</b>（device 仅启用单一队列族）。故共享表面改为交付原生 VkImage
                // （VulkanNativeImage），消费方直接包装采样绘制——零外部内存导出/导入、零 fd、零 dedicated。
                // 旧 OPAQUE_FD 路径（fd 导出交 Avalonia 合成器 ImportImage）在 Adreno 上存在 dedicated
                // 导入死结（不挂 dedicated ⇒ 生产侧 BindImageMemory ErrorInvalidExternalHandle；挂 ⇒ 消费侧
                // vkAllocateMemory INITIALIZATION_FAILED），已判死不再回头。
                // 同步：无需 keyed mutex / 信号量——生产与消费共用同一 VkQueue，同队列提交天然按提交序串行；
                // 交付前生产者以 fence 等待拷贝完成，且末屏障使写入对后续采样可见（详见 CopyToSharedImage）。
                _handleKind = SharedGpuHandleKind.VulkanNativeImage;
                _semaphoreKind = SharedGpuSemaphoreKind.VulkanOpaquePosixFileDescriptor;
                // 平面图像（无外部导出），外部内存句柄类型不再使用。
                _memHandleType = 0;
                _semHandleType = 0;
                _syncMode = SharedGpuSyncMode.None;
            }
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
        // 停摆看门狗：进入即置位并记录阶段，返回（含异常路径）时复位。
        EnsureStallTimer();
        Volatile.Write(ref _stageActive, 1);
        MarkStage("enter");
        try
        {
            return TryWriteFrameCore(frame, out descriptor);
        }
        finally
        {
            MarkStage("done");
            Volatile.Write(ref _stageActive, 0);
        }
    }

    private bool TryWriteFrameCore(VideoFrame frame, out SharedGpuSurfaceDescriptor descriptor)
    {
        descriptor = default;
        if (_disposed)
            return false;
        if (frame.Resource is null)
            return false;
        Interlocked.Increment(ref _writeSeq);

        // 每帧重置 AHB 自提交标志（仅 Android AHB 零拷贝路径在 TryRecordAhbConversion 内部分步自提交后置位）。
        _ahbSelfSubmitted = false;
        // Android 双缓冲：本帧翻转到另一槽写入并交付（绕开 Skia Ganesh 对同 VkImage 的内容缓存）。
        if (_isAndroid)
            _sharedActive ^= 1;

        int w, h;
        bool recorded;
        string? path = null;
        if (frame.Resource is GPUShare.Android.AndroidHardwareBufferFrameResource ahb)
        {
            // 真零拷贝路径（Layer 3）：AHB 经 Vulkan 导入后在 GPU 内直采/转换，零 CPU 像素拷贝。
            if (!_announcedZeroCopy)
            {
                _announcedZeroCopy = true;
                _logger.LogInformation("[VULKAN-SHARED] 路径锁定=ZERO-COPY(AHB→GPU) — GPU 出帧，无 CPU 像素拷贝。");
            }
            path = "ZERO-COPY(AHB→GPU)";
            w = ahb.Width;
            h = ahb.Height;

            // Android RGBA 直采样捷径（0 队列提交）：导入图像直接交 Skia 采样，废除我方 ①转换+②拷贝
            // 双提交。真机实证（2026-09-04 drawop2）：我方提交与 Skia 帧提交共用同一 VkQueue，逐帧
            // AHB 导入+双提交在 Adreno 上以 ~1/2000 频率随机触发 vkQueueSubmit
            // ErrorInitializationFailed（规范外错误码），同秒殃及 Skia 提交 → Avalonia 渲染循环死亡
            // → 画面永久定格（[DRAW-OP] 心跳与 ② 失败同秒消失，而同线程管线侧照常跑完 985 帧）。
            // 导入是纯 CPU 调用（无队列命令），-3 从此无我方触发源。失败或 YCbCr 外部格式 → 落回旧转换路径。
            if (_isAndroid && TryBuildAhbDirectDescriptor(ahb, w, h, frame.RotationDegrees, out descriptor))
                return true;

            MarkStage("EnsureSharedSurface");
            EnsureSharedSurface(w, h);
            _pipeline!.EnsureOffscreenResources(_surfaceVkFormat, new Extent2D((uint)w, (uint)h), _isAndroid ? _convertView : _sharedImageViews[0]);
            MarkStage("AhbConvert.Record");
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
            MarkStage("EnsureSharedSurface");
            EnsureSharedSurface(w, h);
            _pipeline!.EnsureOffscreenResources(_surfaceVkFormat, new Extent2D((uint)w, (uint)h), _isAndroid ? _convertView : _sharedImageViews[0]);
            MarkStage("SoftwareUpload.Record");
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
            MarkStage("Submit.EndCommandBuffer");
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

            MarkStage("Submit.QueueSubmit");
            result = VulkanNative.QueueSubmit(_queue, 1, &submitInfo, (nint)fence.Handle);
            if (result != Result.Success)
            {
                _logger.LogWarning("Vulkan 共享表面 QueueSubmit 失败：{Result}", result);
                return false;
            }

            // 有限超时等待 GPU 完成（与 D3D11 16ms keyed mutex 超时对称）——超时=消费方未归还 → 丢帧。
            MarkStage("Submit.WaitForFences");
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
            VulkanNative.GetImageMemoryRequirements(_device, _sharedImages[_sharedActive], &fallbackReq);
            _sharedMemorySizes[_sharedActive] = fallbackReq.Size;
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

        if (_handleKind == SharedGpuHandleKind.VulkanNativeImage)
        {
            // 同 device 直采样交付：句柄即原生 VkImage，配套 Native* 字段描述内存绑定与采样前置状态
            //（布局=ShaderReadOnlyOptimal、格式/用法/队列族如实上报）。MemorySize/MemoryOffset 兼作
            // 消费方包装所需的分配尺寸/偏移。图像生命周期归本源；尺寸变化时 _version 递增，
            // 消费方据此重建包装。
            descriptor = new SharedGpuSurfaceDescriptor(
                (nint)_sharedImages[_sharedActive].Handle,
                _handleKind,
                w, h,
                _surfaceFormatEnum,
                _version,
                _syncMode,
                _sharedMemorySize,
                0,
                NativeImage: (nint)_sharedImages[_sharedActive].Handle,
                NativeDeviceMemory: (nint)_sharedMemories[_sharedActive].Handle,
                NativeImageLayout: (uint)ImageLayout.ShaderReadOnlyOptimal,
                NativeVkFormat: (uint)_surfaceVkFormat,
                NativeQueueFamilyIndex: _queueFamilyIndex,
                NativeImageUsage: (uint)_sharedUsage,
                NativeImageTiling: (uint)ImageTiling.Optimal,
                RotationDegrees: frame.RotationDegrees);
            return true;
        }

        descriptor = new SharedGpuSurfaceDescriptor(
            _exportedMemoryHandle,
            _handleKind,
            w, h,
            _surfaceFormatEnum,
            _version,
            _syncMode,
            _sharedMemorySize,
            0,
            RotationDegrees: frame.RotationDegrees);
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
    /// Android 稳健层：把已渲染进 plain 内部 <see cref="_convertImage"/> 的帧经 vkCmdCopyImage 拷进 AHB 离屏
    /// <see cref="_sharedImage"/>（仅 TRANSFER_DST，导出交合成器）。<see cref="_sharedImage"/> 为 AHB 支持内存，
    /// 直接作 color attachment 在 Mali 上触发 GROUP_ERROR_FATAL / DEVICE_LOST，故一律经内部图中转。
    /// 软帧路径 srcLayout=ColorAttachmentOptimal（离屏 RenderPass FinalLayout）；AHB 路径 srcLayout=TransferSrcOptimal
    /// （转换器 Convert 末态）。<see cref="_sharedImage"/> 首帧由 Undefined 转入 TransferDstOptimal，后续帧保持
    /// TransferDstOptimal（由 <see cref="_sharedCopyReady"/> 追踪，跨命令缓冲持久）。
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
        //    Android（VulkanNativeImage→Skia 直采样）：上一帧图像已按 ShaderReadOnlyOptimal 交付给
        //    消费方采样（采样不改布局），本帧覆盖屏障的旧布局统一用 Undefined——规范允许的
        //    「不保留旧内容」语义（与图像实际状态无关恒合法），整幅重写故无内容损失；
        //    执行序由同队列（生产/消费共用同一 VkQueue）隐式按提交序串行保证。
        bool firstCopy = !_sharedCopyReady[_sharedActive];
        bool dstFromUndefined = firstCopy || _isAndroid;
        ImageLayout dstOld = dstFromUndefined ? ImageLayout.Undefined : ImageLayout.TransferDstOptimal;
        PipelineStageFlags dstSrcStage = dstFromUndefined ? PipelineStageFlags.TopOfPipeBit : PipelineStageFlags.TransferBit;
        AccessFlags dstSrcAccess = dstFromUndefined ? 0 : AccessFlags.TransferWriteBit;
        ImageMemoryBarrier dstBarrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = dstSrcAccess,
            DstAccessMask = AccessFlags.TransferWriteBit,
            OldLayout = dstOld,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = ~0u,
            DstQueueFamilyIndex = ~0u,
            Image = _sharedImages[_sharedActive],
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
            _sharedImages[_sharedActive], ImageLayout.TransferDstOptimal, 1, &region);

        // 4) Android（VulkanNativeImage→Skia 直采样）：交付前把 _sharedImage 转入
        //    ShaderReadOnlyOptimal 并使拷贝写入对后续采样可见（dstStage 覆盖片元/计算着色器读取）。
        //    消费方按 GRVkImageInfo.ImageLayout 认定布局——采样 ShaderReadOnlyOptimal 无需再转换；
        //    同一 VkQueue 上晚于此提交的采样命令（Skia 帧绘制）自动落入本屏障的可视性作用域。
        if (_isAndroid)
        {
            ImageMemoryBarrier readyBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = ~0u,
                DstQueueFamilyIndex = ~0u,
                Image = _sharedImages[_sharedActive],
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            VulkanNative.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                0, 0, null, 0, null, 1, &readyBarrier);
        }

        // 标记当前槽位已进入交付布局（Android=ShaderReadOnlyOptimal / 其余=TransferDstOptimal，
        // 供后续帧复用，避免重复 Undefined 重排）。
        _sharedCopyReady[_sharedActive] = true;
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
    /// <summary>
    /// 确保可外部导出离屏图像就绪，尺寸变化时重建底层图像并重新导出句柄（_version 递增）。
    /// </summary>
    private void EnsureSharedSurface(int w, int h)
    {
        if (_sharedImages[0].Handle != 0 && _texW == w && _texH == h)
            return;

        // 拆除旧图像（保留 _version 语义：重建才 +1）
        ReleaseSharedSurface();

        if (_isApple)
        {
            EnsureSharedSurfaceApple(w, h);
            return;
        }

        // 图像创建：启用外部内存导出（pNext=ExternalMemoryImageCreateInfo）。
        // Android（VulkanNativeImage 同 device 直采样）为<b>平面图像</b>：无外部导出、无 dedicated、
        // 无 fd——彻底绕开 Adreno external-memory 分配的厂商坑；Usage 含 Sampled（Skia 采样必需）。
        // Android 建 2 槽（双缓冲轮换交付），其余平台 1 槽。
        for (int slot = 0; slot < _slotCount; slot++)
        {
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
            PNext = _handleKind == SharedGpuHandleKind.VulkanNativeImage ? (void*)null : (void*)&extImageInfo,
        };
        _sharedUsage = imageInfo.Usage;
        _sharedFlags = imageInfo.Flags;

        Result result = VulkanNative.CreateImage(_device, &imageInfo, null, out Image img);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage（共享表面离屏）失败: {result}");

        // 内存分配：ExportMemoryAllocateInfo(OpaqueWin32/OpaqueFd) 导出 + dedicated 分配。
        // dedicated **必须挂**（Adreno 650 真机实证，勿删）：不挂时普通 Vulkan 图像 + OPAQUE_FD
        // 导出内存连 vkBindImageMemory 都过不去，报 vkBindImageMemory 失败: ErrorInvalidExternalHandle
        // （Attach 自检阶段即整链回退 Skia）。宿主合成器（Avalonia ImportedImage.CreateMemory）也以
        // dedicated 语义导入，规范上两侧必须匹配。
        // Android（VulkanNativeImage）：平面内存即可，无导出/dedicated 约束。
        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, img, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);

        MemoryDedicatedAllocateInfo dedicated = new MemoryDedicatedAllocateInfo
        {
            SType = StructureType.MemoryDedicatedAllocateInfo,
            Image = img,
        };

        ExternalMemoryHandleTypeFlags memHandle = _memHandleType;
        ExportMemoryAllocateInfo extMemInfo = new()
        {
            SType = StructureType.ExportMemoryAllocateInfo,
            HandleTypes = memHandle,
            PNext = &dedicated,
        };
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
            PNext = _handleKind == SharedGpuHandleKind.VulkanNativeImage ? (void*)null : (void*)&extMemInfo,
        };
        result = VulkanNative.AllocateMemory(_device, &allocInfo, null, out DeviceMemory mem);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory（共享表面离屏）失败: {result}");
        result = VulkanNative.BindImageMemory(_device, img, mem, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindImageMemory（共享表面离屏）失败: {result}");
        // 记录本次分配的真实字节数（= vkGetImageMemoryRequirements().size），随描述符交给合成器。
        // 【为什么必须填】Avalonia 的 VulkanExternalObjectsFeature.ImportedImage.CreateMemory 会拿
        // properties.MemorySize 与它自己 vkGetImageMemoryRequirements(导入图像).size 做**严格相等**校验，
        // 不等即抛 "Invalid memory size"（真机实证：留 0 → 每帧导入失败 → 不出画）。OPAQUE_FD 不携带
        // 内存元数据，此值只能由生产者如实上报。注意：不是 w*h*4 —— 驱动会按 tile/对齐扩到更大。
        _sharedMemorySizes[slot] = memReq.Size;

        // 导出内存句柄（交合成器）：
        //  - Windows：HANDLE（OpaqueWin32）。
        //  - Android/Linux：vkGetMemoryFdKHR 导出 opaque fd（dma_buf）——两条路径完全同代码。
        //    （旧 Android 曾经 AHB 承载：vkGetMemoryAndroidHardwareBufferANDROID 取回 AHardwareBuffer
        //    再抽 dma_buf fd。已废弃：AHB 兼容约束的 requirements 与宿主按普通 OPAQUE_FD 建图算出的
        //    不一致，严格相等校验必失败，真机实证 1080x1920 全部 Invalid memory size。）
        if (_isWindows)
        {
            MemoryGetWin32HandleInfoKHR getInfo = new()
            {
                SType = StructureType.MemoryGetWin32HandleInfoKhr,
                Memory = mem,
                HandleType = memHandle,
            };
            Result hR = VulkanNative.GetMemoryWin32HandleKHR(_device, &getInfo, out nint hMem);
            if (hR != Result.Success)
                throw new InvalidOperationException($"vkGetMemoryWin32HandleKHR 失败: {hR}");
            _exportedMemoryHandle = hMem;
        }
        else if (_handleKind == SharedGpuHandleKind.VulkanNativeImage)
        {
            // VulkanNativeImage（同 device 直采样）：无需导出任何外部句柄——
            // 消费方（UI 层 Skia）直接按描述符的 Native* 字段包装本图像采样绘制。
            _exportedMemoryHandle = IntPtr.Zero;
            if (slot == 0)
                _logger.LogInformation(
                    "[VULKAN-SHARED] VulkanNativeImage：平面图像+平面内存（无 fd/无 dedicated），双缓冲轮换直接采样。");
        }
        else
        {
            // Linux / Android fd 形态：经 vkGetMemoryFdKHR 导出 opaque fd（dma_buf）——两条路径完全同代码。
            MemoryGetFdInfoKHR getInfo = new()
            {
                SType = StructureType.MemoryGetFDInfoKhr,
                Memory = mem,
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
            Image = img,
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
        result = VulkanNative.CreateImageView(_device, &viewInfo, null, out ImageView view);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView（共享表面离屏）失败: {result}");

        _sharedImages[slot] = img;
        _sharedMemories[slot] = mem;
        _sharedImageViews[slot] = view;
        } // end slot loop

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

    private void ReleaseSharedSurface()
    {
        if (_device.Handle == 0) return;
        if (_sharedImageViews[0].Handle != 0)
        {
            VulkanNative.DestroyImageView(_device, _sharedImageViews[0], null);
            _sharedImageViews[0] = default;
        }
        if (_sharedImages[0].Handle != 0)
        {
            VulkanNative.DestroyImage(_device, _sharedImages[0], null);
            _sharedImages[0] = default;
        }
        if (_sharedMemories[0].Handle != 0)
        {
            VulkanNative.FreeMemory(_device, _sharedMemories[0], null);
            _sharedMemories[0] = default;
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
        _sharedCopyReady[0] = false; _sharedCopyReady[1] = false; _sharedActive = 0;
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
        _stallTimer?.Dispose();
        _stallTimer = null;

        if (_device.Handle != 0)
            VulkanNative.DeviceWaitIdle(_device);

        _pipeline?.Dispose();
        _pipeline = null;
        _ycbcrConverter?.Dispose();
        _ycbcrConverter = null;
        _rgbaConverter?.Dispose();
        _rgbaConverter = null;

        ReleaseSharedSurface();

        // AHB 直采样导入退役环清空（上方 DeviceWaitIdle 已保证 Skia 采样全部执行完毕）。
        while (_ahbDirectRetire.Count > 0)
        {
            (Image oldImage, DeviceMemory oldMemory) = _ahbDirectRetire.Dequeue();
            VulkanNative.DestroyImage(_device, oldImage, null);
            VulkanNative.FreeMemory(_device, oldMemory, null);
        }

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

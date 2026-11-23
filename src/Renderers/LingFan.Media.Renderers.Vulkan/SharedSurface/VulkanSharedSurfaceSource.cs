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
/// <para><b>这是共享表面路径中唯一碰 Vulkan 具体 API 的地方</b>。其余层（Avalonia <c>CompositionVideoRenderer</c>）
/// 只看到 <see cref="SharedGpuSurfaceDescriptor"/>（外部内存句柄 + 信号量对），<b>不引用任何 GPU 库</b>，
/// 从而达成「不绑定具体 GPU、低耦合」的架构诉求，严守「各 Renderer 管好自身（无头/有头/无空域）」架构原则。</para>
/// <para><b>零拷贝路径</b>：软帧（NV12/NV21/YUV420P/YUV422P/YUV444P/BGRA/RGBA）的 Y/U/V 平面上传到 GPU 纹理后
/// 由 <see cref="VulkanShaderPipeline"/> 完成 YUV→RGB + 缩放，写入可导出离屏图像；该图像由宿主合成器
/// 经 <c>VkImportMemoryWin32HandleInfoKHR</c> / <c>VkImportMemoryFdInfoKHR</c> 直接导入采样，无 CPU 回读、无独占 HWND。</para>
/// <para><b>同步模型（Semaphores，fd/NT/IOSurface 句柄形态）</b>：</para>
/// <list type="bullet">
/// <item>生产者（本源）写完后 signal <c>ConsumerWait</c> 信号量（消费方据此等待「内容就绪」）；</item>
/// <item>消费方采样完成后 signal <c>ConsumerSignal</c> 信号量（生产者据此等待「表面已归还，可覆写」）；</item>
/// <item>生产者每帧以<b>有限超时（16ms，与 D3D11 keyed mutex 超时对称）</b>等待 <c>ConsumerSignal</c> 后再写，
/// 超时即丢帧——绝不无限阻塞管线线程。两信号量均经外部句柄导出，消费方导入一次长期使用。</item>
/// </list>
/// <para><b>同步模型（None，Android 默认 VulkanNativeImage 形态）</b>：生产与消费共用同一 VkDevice、同一图形队列，
/// 同队列提交天然按提交序串行，无需跨端信号量/keyed mutex 握手；交付前生产者以 fence 等待拷贝完成，
/// 末屏障使写入对后续采样可见（见 <c>CopyToSharedImage</c>）。</para>
/// <para><b>握手初始化（Semaphores 形态）</b>：二进制信号量默认未信号。生产者首帧须等待 <c>ConsumerSignal</c>，故创建时以一次
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
    // VulkanNativeImage 形态（Android 同 device 直采样）双缓冲（2 槽轮换写入/交付）：Skia Ganesh 对【同一 VkImage 句柄】
    // 的包装会命中纹理缓存、内容不更新（管线持续出帧而屏幕定格首次采样内容）；每帧换一个 VkImage 交付即绕开缓存。
    // fd/NT/IOSurface 形态走合成器导入 + version 重建机制，单槽即可。
    private readonly Image[] _sharedImages = new Image[2];
    private readonly DeviceMemory[] _sharedMemories = new DeviceMemory[2];
    private readonly ImageView[] _sharedImageViews = new ImageView[2];
    private readonly bool[] _sharedCopyReady = new bool[2];
    private readonly ulong[] _sharedMemorySizes = new ulong[2];
    /// <summary>本帧写入并交付的槽位（VulkanNativeImage 形态每帧翻转；其余形态恒 0）。</summary>
    private int _sharedActive;
    // 槽位数：NativeImage（同 device 直采样）双缓冲轮换交付；fd/NT/IOSurface 形态单槽（与 Linux 一致）。
    private int _slotCount => _handleKind == SharedGpuHandleKind.VulkanNativeImage ? 2 : 1;
    private int _texW, _texH;
    private nint _exportedMemoryHandle;   // 导出的外部内存句柄：Windows=HANDLE，Linux/Android=fd（int 经 nint 传递）
    private ulong _version;
    /// <summary>当前交付槽位的共享离屏外部内存真实分配字节数（= vkGetImageMemoryRequirements().size）。
    /// 随 <see cref="SharedGpuSurfaceDescriptor"/> 交合成器：Avalonia 导入 OPAQUE_FD 时以此与自身
    /// 计算的内存需求做严格相等校验，不符即抛 "Invalid memory size"（留 0 时导入校验必失败、不出画）。
    /// 注意不是 w*h*4 —— 驱动按 tile/对齐会扩到更大值，只能以 vkGetImageMemoryRequirements 为准。</summary>
    private ulong _sharedMemorySize => _sharedMemorySizes[_sharedActive];

    // 当前共享图像的创建参数快照（仅诊断用）：requirements 是 usage/flags 的函数，
    // 转移口日志打印它们可与宿主合成器侧的建图参数逐位对表。
    private ImageUsageFlags _sharedUsage;
    private ImageCreateFlags _sharedFlags;


    // 写入停摆看门狗
    // TryWriteFrame 只在管线线程串行执行。此处仅记录「当前阶段 + 进入时刻」，由独立定时器在
    // 写入久未返回时告警，用于定位卡死在哪个原生调用；正常播放期间零日志输出。
    private long _writeSeq;
    private int _stageActive; // 0=空闲 1=写入中
    private string? _lastStage;
    private long _lastStageQpc;
    private long _stallMarkQpc;       // 上次告警对应的阶段进入时刻（阶段推进后重新武装）
    private long _stallNextReportQpc; // 同一阶段的重复告警节流（10s）
    private System.Threading.Timer? _stallTimer;


    // 信号量对（长期对象，随源创建/释放；消费方导入一次长期使用）。
    private Semaphore _consumerWaitSem;
    private Semaphore _consumerSignalSem;
    private nint _consumerWaitHandle;
    private nint _consumerSignalHandle;

    private bool _disposed;

    // 出帧路径区分（零拷贝 vs 软帧上传）：一次性公告 + 周期计数，供运行日志区分帧来源与转移口。
    private long _writtenFrames;
    private bool _announcedZeroCopy;
    private bool _announcedSoft;
    private const int FrameLogInterval = 30;

    // Android 共享离屏：默认 VulkanNativeImage 形态——宿主注入共享 VkDevice，Skia 同 device 直采样，
    // 零外部内存导出/导入、零 fd、零 dedicated（见构造函数 Android 分支）。
    // 仅当 handleKindOverride 显式指定 VulkanOpaquePosixFileDescriptor 时，才走与 Linux 相同的 OPAQUE_FD 导出路径：
    // ExportMemoryAllocateInfo(OPAQUE_FD) + vkGetMemoryFdKHR 导出 dma_buf fd 交合成器导入上屏
    //（版本/厂商无关、AOT 安全、零 P/Invoke 到 libandroid.so）。
    // 防回归：不要走「自分配 AHardwareBuffer + 反向解析 GraphicBuffer/native_handle_t 的 C++ 内存布局抠 fd」
    // 的脆弱路线（android_native_base_t 版本/厂商布局不兼容、AHardwareBuffer 与 GraphicBuffer 无继承契约，
    // 硬编码偏移会读到错误 magic → 优雅回退 Skia、Android 不出画）。

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
                // Android（同 device Skia 直绘）：宿主（Avalonia Android 入口）已把自建
                // VkDevice 注入本工厂（UseExternalDevice），UI 层的 Skia GPU 上下文与本源共用<b>同一 device、
                // 同一图形队列</b>（device 仅启用单一队列族）。故共享表面改为交付原生 VkImage
                // （VulkanNativeImage），消费方直接包装采样绘制——零外部内存导出/导入、零 fd、零 dedicated。
                // 旧 OPAQUE_FD 路径（fd 导出交 Avalonia 合成器 ImportImage）在 Adreno 上存在 dedicated
                // 导入死结（不挂 dedicated ⇒ 生产侧 BindImageMemory ErrorInvalidExternalHandle；挂 ⇒ 消费侧
                // vkAllocateMemory INITIALIZATION_FAILED），已弃用不再回头。
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
        // VulkanRenderer._shaderPipeline 对称。不初始化则 TryWriteFrame
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

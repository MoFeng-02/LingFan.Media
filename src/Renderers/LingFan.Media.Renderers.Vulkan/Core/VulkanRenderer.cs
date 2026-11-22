using System.Diagnostics;
using LingFan.Media.GPUShare.Vulkan;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// 渲染器诊断接口（本程序集 public，供探针跨程序集读取分相计时，不污染契约层 IVideoRenderer）。
/// </summary>
public interface IRendererProfiler
{
    /// <summary>返回 CPU 转换耗时与 GPU 同步耗时的分相统计摘要。</summary>
    string GetProfile();
}

/// <summary>
/// Vulkan 视频渲染器。将 <see cref="VideoFrame"/> 呈现到 Vulkan SwapChain。
/// </summary>
/// <remarks>
/// <para>跨平台 GPU 渲染器（Windows / Linux / Android；macOS/iOS 经 MoltenVK 覆盖——
/// 仅引入 MoltenVK 让 Vulkan 后端在 Apple 平台初始化/跑 SwapChain，无空域零拷贝上屏属第二类，待 Apple 合成栈落地）。
/// Surface 创建用 Vulkan 自己的 WSI 扩展（VK_KHR_*_surface / VK_EXT_metal_surface），不需要平台互操作文件。</para>
/// <para>WSI 扩展（Surface/Swapchain/Present）由 <c>VulkanNative</c> 零反射绑定在运行时经三阶段解析
/// （实例句柄解析实例级 / WSI 实例扩展，设备句柄解析设备级 / WSI 设备扩展），
/// 不依赖 Silk.NET 的 <c>Khr*</c> 扩展对象与加载层。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：contract，返回 <see cref="Task.CompletedTask"/></item>
/// <item><see cref="Attach"/>/<see cref="Detach"/>：native sync，UI 线程</item>
/// <item><see cref="Present"/>/<see cref="Clear"/>：native sync，渲染线程</item>
/// <item><see cref="DisposeAsync"/>：contract，委托 Dispose + CompletedTask，非伪异步</item>
/// </list>
/// <para><b>线程安全</b>：<c>_gate</c> 锁串行化所有公开方法。
/// <see cref="Present"/> 的 vkAcquireNextImage 以 <c>AcquireTimeoutNs</c> 超时
/// 在 <c>_gate</c> 锁内阻塞——并发调用 <see cref="Dispose"/>/<see cref="Detach"/> 最坏被卡至超时才能获得锁。
/// 这是「有限超时替代无限等待」权衡的已知副作用，属预期行为而非死锁。</para>
/// <para><b>已知性能限制</b>：<see cref="RecordAndSubmitFrame"/> 中使用 <c>vkQueueWaitIdle</c>
/// 每帧同步 GPU——确保 Command Buffer 可安全复用但消除 GPU 并行。将改用 Fence 或环形 Command Buffer。</para>
/// <para><b>Linux Surface</b>：X11 / Wayland 经中性句柄类型 <c>X11WindowHandle</c> / <c>WaylandWindowHandle</c>
/// （定义于 <c>LingFan.Media.Renderers.Shared</c>，非契约层）携带 Display* 指针，由
/// <c>VulkanNative.CreateXlibSurfaceKHR</c> / <c>CreateWaylandSurfaceKHR</c> 创建 Surface；
/// 对应 WSI 扩展由 <c>VulkanRendererFactory.GetPlatformExtensions</c> 经 <c>AddIfAvailable</c> 注册。</para>
/// <para><b>软帧 YUV 平面格式</b>（NV12/NV21/YUV420P/YUV422P/YUV444P）走 GPU Shader 路径：由 Fragment Shader
/// 采样 Y/U/V 平面并完成 YUV→RGB（与 D3D11 Shader 路径共用 BT.601 全范围矩阵），CPU 仅做原始平面搬运；
/// 缩放支持三种 <see cref="AspectRatioMode"/>（见 <see cref="ScaleMode"/>）。</para>
/// ErrorOutOfDateKhr 已由 <c>RecreateSwapchain</c> 就地重建（含信号量重建，消除 signaled 残留）；
/// 其余 QueuePresent 硬失败仍抛异常，由会话层重新 Attach 恢复（信号量在重 Attach 时重建，无 double-signal 风险）。</para>
/// <para>AOT 兼容：sealed unsafe 类，无反射，pattern matching。</para>
/// </remarks>
internal sealed unsafe partial class VulkanRenderer : IVideoRenderer, IRendererProfiler
{
    // 共享资源（工厂注入，不由本类释放）
    private readonly Instance _instance;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly uint _queueFamilyIndex;
    private readonly ILogger<VulkanRenderer> _logger;

    // GPU Shader 管线（软帧 YUV 路径）
    private VulkanShaderPipeline? _shaderPipeline;

    // 中性 NV12→RGBA 转换器（GPUShare.Vulkan）：把硬解 DPB 的 NV12 VkImage 转 RGBA 后交 BlitVulkanImageResource 上屏。
    // 与 _shaderPipeline 同生命周期（Attach 创建 / ReleaseSessionResources 释放）。
    private VulkanNv12ToRgbaConverter? _nv12Converter;

    // Session 级资源
    private SurfaceKHR _surface;
    private SwapchainKHR _swapchain;
    private Image[] _swapchainImages = [];
    // _swapchainImageViews 已移除——仅用 CmdCopyBufferToImage（直接操作 VkImage），
    // 不需要 ImageView。实现 Shader 渲染时需重新添加。
    private Format _swapchainFormat;
    private Extent2D _swapchainExtent;
    // Attach 时记录目标尺寸，供 OutOfDate 重建 SwapChain 使用
    //（CreateSwapchain 优先取 Surface CurrentExtent，此值仅作 CurrentExtent 不可用时的回退）。
    private uint _targetWidth;
    private uint _targetHeight;
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private Semaphore _imageAvailableSemaphore;
    // 每个 SwapChain 图像独立持有一个 renderFinished 信号量，按 imageIndex 索引。
    // 修复 VUID-vkQueueSubmit-pSignalSemaphores-00067：vkQueueSubmit 信号的信号量在提交时必须
    // 处于 unsignaled；若全帧共用单一 renderFinished，上一帧 present 尚未完成（image 未 re-acquire）
    // 时下一帧 submit 又去 signal 同一信号量，校验层即报 00067（硬解无 QueueWaitIdle 串行化时直接绿屏）。
    private Semaphore[] _renderFinishedSemaphores = [];

    // 预分配 PresentInfo 数组，避免每帧 GC 分配
    private readonly PresentInfoKHR[] _presentInfoArr = [new PresentInfoKHR()];

    // 暂存缓冲
    private Buffer _stagingBuffer;
    private DeviceMemory _stagingMemory;
    private ulong _stagingBufferSize;
    // staging 为 HOST_COHERENT 内存（见 EnsureStagingBuffer），整段映射一次后长期复用，
    // 免去每帧 MapMemory/UnmapMemory 的仪式开销；写入经一致性语义自动对 GPU 可见。
    private void* _stagingMapped;

    // 缩放模式（契约层 AspectRatioMode）：软帧尺寸与 SwapChain 不一致时据此适配。
    // 默认 Uniform（信箱）——无畸变、留黑边，与 VideoView/Pipeline 默认一致。
    public AspectRatioMode ScaleMode { get; set; } = AspectRatioMode.Uniform;

    // 缩放 blit 用暂存图像（软帧 1:1 不匹配时经此中转）
    private Image _stagingImage;
    private DeviceMemory _stagingImageMem;
    private uint _stagingImageW;
    private uint _stagingImageH;
    private Format _stagingImageFormat;

    // 诊断计时（定位每帧开销归属，由诊断开关门控）
    // 累加 CPU 转换耗时（UploadSoftwareFrame 全程）与 GPU 同步耗时（QueueWaitIdle 全程），
    // 收尾由探针读取打印，指导进一步优化方向（不改热路径逻辑）。
    private long _profConvertTicks;
    private long _profGpuTicks;
    private int _profFrames;

    public string GetProfile()
    {
        double convMs = _profFrames == 0 ? 0
            : (double)_profConvertTicks / _profFrames / TimeSpan.TicksPerMillisecond;
        double gpuMs = _profFrames == 0 ? 0
            : (double)_profGpuTicks / _profFrames / TimeSpan.TicksPerMillisecond;
        return $"帧数={_profFrames} 平均CPU转换={convMs:F2}ms 平均GPU同步(QueueWaitIdle)={gpuMs:F2}ms";
    }

    private bool _disposed;
    private bool _attached;
    private readonly object _gate = new();

    // AcquireNextImage 超时——2 秒（纳秒），避免窗口最小化时永久阻塞
    private const ulong AcquireTimeoutNs = 2_000_000_000;

    internal VulkanRenderer(
        Instance instance, PhysicalDevice physicalDevice,
        Device device, Queue queue, uint queueFamilyIndex,
        ILogger<VulkanRenderer> logger)
    {
        _instance = instance;
        _physicalDevice = physicalDevice;
        _device = device;
        _queue = queue;
        _queueFamilyIndex = queueFamilyIndex;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal bool IsDisposed => _disposed;

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    // Attach

    /// <inheritdoc/>
    public void Attach(IRenderTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            if (_disposed) return;
            if (_attached) { _logger.LogWarning("Vulkan 渲染器已附加，先 Detach。"); Detach(); }

            if (target.HandleType != RenderHandleType.Pointer)
                throw new NotSupportedException(
                    $"Vulkan SwapChain 直绘需要原生窗口句柄（{nameof(RenderHandleType.Pointer)}：Windows=HWND / Android=ANativeWindow / Linux=X11·Wayland 句柄）；" +
                    $"当前目标为 {target.HandleType}。无原生窗口的控件内场景（无空域）不受 SwapChain 窗口独占模型支持" +
                    "（同一原生窗口同时只允许一个表面，且与宿主渲染器的表面互斥），应改用共享表面源 + 宿主直采路线。");
            if (OperatingSystem.IsLinux())
            {
                // Linux X11/Wayland 经中性句柄类型 X11WindowHandle / WaylandWindowHandle 携带 Display* 指针（定义于 Renderers.Shared，非契约层）。
                if (target.NativeHandle is not (X11WindowHandle or WaylandWindowHandle))
                    throw new ArgumentException("Linux 渲染目标句柄须为 X11WindowHandle 或 WaylandWindowHandle（携带 Display* 与 Window/Surface 指针）。", nameof(target));
            }
            else if (target.NativeHandle is not IntPtr handle || handle == IntPtr.Zero)
            {
                throw new ArgumentException("渲染目标句柄无效。", nameof(target));
            }
            if (target.Width <= 0 || target.Height <= 0)
                throw new ArgumentException($"尺寸无效：{target.Width}x{target.Height}。", nameof(target));

            try
            {
                _targetWidth = (uint)target.Width;
                _targetHeight = (uint)target.Height;
                // 原生死亡定位踪迹（stderr 直写，DECODER-DISPOSE 同款）：Attach 链五阶段，
                // 卡死/崩溃时最后一条阶段行即定位点。
                Console.Error.WriteLine("[VULKAN-ATTACH] → CreateSurface");
                CreateSurface(target);
                Console.Error.WriteLine("[VULKAN-ATTACH] → CreateSwapchain");
                CreateSwapchain((uint)target.Width, (uint)target.Height);
                Console.Error.WriteLine("[VULKAN-ATTACH] → CreateShaderPipeline");
                CreateShaderPipeline();
                Console.Error.WriteLine("[VULKAN-ATTACH] → CommandPool");
                CreateCommandPoolAndBuffer();
                // NV12→RGBA 转换器：与 Shader 管线同生命周期；Present 时把硬解 DPB 的 NV12 图像转 RGBA。
                _nv12Converter = new VulkanNv12ToRgbaConverter(_device, _physicalDevice, _logger);
                Console.Error.WriteLine("[VULKAN-ATTACH] → Semaphores");
                CreateSemaphores();
                _attached = true;
                Console.Error.WriteLine("[VULKAN-ATTACH] Attach 完成");
                _logger.LogDebug("Vulkan 渲染器已附加：{W}x{H}", target.Width, target.Height);
            }
            catch { ReleaseSessionResources(); throw; }
        }
    }

    // Present

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            if (_disposed) return;
            if (!_attached || _swapchain.Handle == 0)
                throw new InvalidOperationException("渲染器未附加渲染目标。");

            // 1. 获取下一张 SwapChain 图像
            // 超时在 _gate 锁内阻塞，并发 Dispose/Detach 最坏等至超时（预期行为，见类级注释）
            Span<uint> imageIndexSpan = stackalloc uint[1];
            Result result = VulkanNative.AcquireNextImageKHR(
                _device, _swapchain, AcquireTimeoutNs,
                _imageAvailableSemaphore, default, imageIndexSpan);
            if (result == Result.Timeout)
            {
                _logger.LogWarning("vkAcquireNextImage 超时，跳过本帧。");
                return;
            }
            if (result == Result.ErrorOutOfDateKhr)
            {
                // SwapChain 过期（窗口尺寸/显示模式变化）——就地重建，跳过本帧，
                // 下一帧用新 SwapChain 正常渲染。规范保证 OutOfDate 时 Acquire 不信号 semaphore。
                RecreateSwapchain();
                return;
            }
            if (result == Result.ErrorDeviceLost)
            {
                // B-DEVLOST: VK_ERROR_DEVICE_LOST——设备及全部资源永久失效，
                // 抛中立 GpuDeviceLostException 供会话层释放并重建。
                _logger.LogError("Vulkan 设备丢失（vkAcquireNextImage）。");
                throw new GpuDeviceLostException("Vulkan 设备已丢失（vkAcquireNextImage 返回 VK_ERROR_DEVICE_LOST）。需释放并重建渲染会话。");
            }
            if (result != Result.Success && result != Result.SuboptimalKhr)
                throw new InvalidOperationException($"vkAcquireNextImage 失败: {result}");
            uint imageIndex = imageIndexSpan[0];

            // 2. 记录+提交命令缓冲
            try
            {
                RecordAndSubmitFrame(frame, imageIndex);
            }
            catch (Exception ex) when (ex is not GpuDeviceLostException)
            {
                // 信号量恢复——AcquireNextImage 已信号 _imageAvailableSemaphore，
                // 但 QueueSubmit 前异常导致信号量未消费。必须提交最小命令缓冲消费信号量，
                // 否则下次 AcquireNextImage 因信号量已信号而永久超时（渲染器永久卡死）。
                // B-DEVLOST: 设备丢失时跳过恢复（设备已死，提交必然失败且徒增日志噪音），直接上抛。
                RecoverSemaphore(imageIndex);
                throw;
            }

            // 3. 呈现（复用预分配数组）
            SwapchainKHR swap = _swapchain;
            Semaphore renderFin = _renderFinishedSemaphores[imageIndex];
            uint idx = imageIndex;

            _presentInfoArr[0] = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFin,
                SwapchainCount = 1,
                PSwapchains = &swap,
                PImageIndices = &idx,
            };

            result = VulkanNative.QueuePresentKHR(_queue, _presentInfoArr);
            if (result == Result.ErrorOutOfDateKhr)
            {
                // Present 阶段过期——丢弃本帧并重建 SwapChain。
                // RecreateSwapchain 内部同时重建信号量（含 _renderFinishedSemaphores 数组），
                // 消除 QueuePresent 失败后 renderFinished 信号量可能的 signaled 残留。
                RecreateSwapchain();
                return;
            }
            if (result == Result.ErrorDeviceLost)
            {
                // B-DEVLOST: 同 AcquireNextImage——类型化异常供会话层重建
                _logger.LogError("Vulkan 设备丢失（vkQueuePresent）。");
                throw new GpuDeviceLostException("Vulkan 设备已丢失（vkQueuePresent 返回 VK_ERROR_DEVICE_LOST）。需释放并重建渲染会话。");
            }
            if (result != Result.Success && result != Result.SuboptimalKhr)
                throw new InvalidOperationException($"vkQueuePresent 失败: {result}");
        }
    }

    // Detach / Dispose

    /// <inheritdoc/>
    public void Detach()
    {
        lock (_gate)
        {
            if (_disposed || !_attached) return;
            ReleaseSessionResources();
            _attached = false;
            _logger.LogDebug("Vulkan 渲染器已解绑");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseSessionResources();
            _attached = false;
            _logger.LogDebug("Vulkan 渲染器已释放");
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    // 内部辅助方法

    // 获取进程模块句柄供 Win32SurfaceCreateInfoKHR.Hinstance 使用。
    // LibraryImport P/Invoke，NativeAOT 兼容（源生成 marshaller，直接 P/Invoke，无运行时反射式封送）。
    [System.Runtime.InteropServices.LibraryImport("kernel32")]
    private static partial nint GetModuleHandleW(nint lpModuleName);
}

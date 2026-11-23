using System;
using Vector3 = System.Numerics.Vector3;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 同 device Skia GPU 直绘视频渲染器（Avalonia UI 层）。
/// </summary>
/// <remarks>
/// <para><b>定位</b>：Android 上「无空域 + 零拷贝 + 无 ByteBuffer」的 GPU 上屏路径——消费
/// <see cref="ISharedGpuSurfaceSource"/> 交付的共享表面描述符（原生 GPU 纹理，与宿主 Skia 上下文
/// 共用同一设备/队列），经 <see cref="IHostSurfacePresenter"/> 适配器包装为可采样图像，
/// 在渲染回调里直接采样绘制。骨架与包装方式解耦：新增宿主/句柄组合 = 新增适配器实现 + DI 注册。</para>
/// <para><b>线程</b>：Attach/Detach 在 UI 线程；Present 在管线线程（只调
/// <see cref="ISharedGpuSurfaceSource.TryWriteFrame"/> 并存描述符快照）；Render 在 Avalonia 渲染线程
///（经 <see cref="IAvaloniaRenderAware"/>）。跨线程交接仅为一个 readonly record struct 快照（lock 保护）。</para>
/// <para><b>生命周期</b>：VkImage/VkDeviceMemory 归表面源所有，本渲染器只借用——每次 Render 现场
/// 创建包装（GRBackendTexture/SKImage）并在绘制后立即释放；Ganesh 对已记录命令持有的代理引用
/// 会保活纹理直至帧冲刷，属 Skia 标准用法。不跨帧缓存包装，规避渲染线程亲和与上下文重建悬挂。</para>
/// <para><b>回退</b>：持续失败（写入/绘制）达阈值触发 <see cref="IRendererHealth.Unhealthy"/>，
/// VideoView 拉黑本工厂并回退 CPU Skia 软渲染，保证总有路径出画。</para>
/// <para><b>异步策略</b>：全部同步（Present 纯 GPU 提交、Render 纯绘制，无 I/O 可 await）。</para>
/// <para><b>AOT 兼容</b>：sealed 类、无反射、全程静态调用（public Skia/Avalonia API）。</para>
/// </remarks>
internal sealed partial class SkiaGpuVideoRenderer : IVideoRenderer, IAvaloniaRenderAware, IRendererHealth
{
    // ≈ 1 个 60Hz 帧刷新周期；VideoPipeline 据其做音画对齐提前量。
    private static readonly TimeSpan PresentLatency = TimeSpan.FromMilliseconds(16);

    private readonly IEnumerable<ISharedGpuSurfaceSourceFactory> _surfaceFactories;
    private readonly IReadOnlyList<IHostSurfacePresenter> _hostPresenters;
    private readonly ILogger _logger;

    private readonly object _gate = new();
    private SharedGpuSurfaceDescriptor _pending;
    private bool _hasPending;

    private Visual? _visual;
    private Control? _control;
    private IDisposable? _stretchSubscription;
    private Vector _controlSize;
    private Stretch _stretch = Stretch.Uniform;

    private ISharedGpuSurfaceSource? _source;
    private bool _disposed;

    // 自适应重绘调度（带运行时自愈探测）
    // Present 存入待呈现帧后预约一次重绘：优先经 TopLevel 动画时钟（RequestAnimationFrame，
    // UI 线程专属 API，回调在 MediaContext 渲染 pass 内执行并同 pass commit——呈现线程调用
    // 须经 Dispatcher 编组）。动画时钟回调未在探测超时（100ms ≫ 任意常见刷新周期）内执行
    // 时，永久切换为逐帧 Post（既有已验证路径），自愈期间至多黑屏一个探测窗。
    private TopLevel? _topLevel;
    // 仅经 Interlocked.Exchange（预约去重）与 Volatile.Write（回调内复位）访问，无需 volatile。
    private int _renderTickScheduled;
    private long _renderTickDispatchQpc;   // 本次预约时刻（探测超时判据）
    private bool _useAnimationClock = true; // 动画时钟探测结果（仅管线线程读写）
    private const double AnimationClockProbeTimeoutMs = 100.0;

    // 源级运行时回退
    // 呈现失败（如共享句柄类型与宿主渲染后端不匹配）时优先切换下一个共享表面源，
    // 全部源穷尽后才触发 Unhealthy 整体回退——保证任何源组合都有出场机会，骨架零句柄分支。
    private List<ISharedGpuSurfaceSourceFactory> _availableFactories = new();
    private volatile int _triedCount;        // 已尝试源数（Attach 记 1）
    private int _sourceSwitchPending;        // 渲染线程失败达阈值 → 请求管线线程切换（0/1）
    private const int SourceFailureThreshold = 3;

    // 运行期健康：全部源穷尽后仍失败才触发 Unhealthy → VideoView 回退（与 CompositionVideoRenderer 同模式）。
    private int _consecutiveFailures;
    private int _unhealthyFired;

    // 诊断标志：首帧一次性；失败原因按内容去重（_lastFailureReason）。
    private bool _firstFrameLogged;
    private string? _lastFailureReason;

    // 渲染线程消费心跳
    // Present（管线线程）持续写入共享表面；DrawOp.Render（Avalonia 渲染线程）消费并绘制。
    // 若写入持续推进而渲染线程长时间未绘制，说明画面可能定格（管线侧计数与真实上屏脱节）。
    // Present 侧据此告警（节流：重新积累写入帧数后才可能再次触发），渲染线程侧零日志开销。
    private long _drawSeq;                  // 渲染线程已成功绘制的帧序号
    private long _deliveredFrames;          // 已交付渲染线程的帧计数（心跳用，不受源 Version 语义影响）
    private long _lastDrawQpc;              // 最近一次成功绘制的时刻
    private int _framesSinceLastDraw;       // 自上次成功绘制以来管线写入的帧数

    /// <inheritdoc/>
    /// <remarks>本渲染器在 <see cref="Present"/> 内预约重绘（动画时钟优先、超时自愈回退
    /// 逐帧 Post），VideoView 无需再逐帧投递 InvalidateVisual（见 <see cref="RequestRenderTick"/>）。</remarks>
    public bool DrivesOwnRenderLoop => true;

    /// <summary>
    /// 预约一次重绘（管线线程调用）：动画时钟模式经 Dispatcher 编组到 UI 线程调用
    /// TopLevel.RequestAnimationFrame（该 API 为 UI 线程专属，VerifyAccess 强制）——回调在
    /// MediaContext 渲染 pass 内执行，InvalidateVisual 与 compositor commit 同 pass 完成；
    /// 同一帧窗口内多次 Present 合并为一拍。预约后 100ms 回调仍未执行（UI 线程饥饿或
    /// 后端异常）即永久切换为逐帧 Post，保证任何后端总有重绘。
    /// </summary>
    private void RequestRenderTick()
    {
        var control = _control;
        if (control is null)
            return;

        if (_renderTickScheduled != 0)
        {
            if (_useAnimationClock)
            {
                // 已有动画时钟预约在途：超时未回调 ⇒ 永久退回逐帧 Post。
                // 退回后继续走下方 Post 分支，本帧立即补一次调度（自愈，不丢待呈现帧）。
                double pendingMs = System.Diagnostics.Stopwatch.GetElapsedTime(
                    Volatile.Read(ref _renderTickDispatchQpc)).TotalMilliseconds;
                if (pendingMs <= AnimationClockProbeTimeoutMs)
                    return;
                _useAnimationClock = false;
                _logger.LogWarning(
                    "合成器动画时钟预约 {Ms:F0}ms 未回调，重绘调度退回逐帧模式。",
                    pendingMs);
            }
            else
            {
                return;
            }
        }

        Interlocked.Exchange(ref _renderTickScheduled, 1);
        Volatile.Write(ref _renderTickDispatchQpc, System.Diagnostics.Stopwatch.GetTimestamp());
        var topLevel = _topLevel;
        if (_useAnimationClock && topLevel is not null)
        {
            // RequestAnimationFrame 为 UI 线程专属（VerifyAccess），呈现线程经 Dispatcher 编组。
            // 回调在渲染 pass 内执行：InvalidateVisual 触发的 commit 与回调同 pass，
            // 重绘与合成节拍对齐；rAF 自身 ScheduleRender 唤醒渲染循环，无空闲死锁。
            Dispatcher.UIThread.Post(() =>
            {
                var tl = _topLevel;
                if (tl is null || _disposed)
                {
                    Volatile.Write(ref _renderTickScheduled, 0);
                    return;
                }
                tl.RequestAnimationFrame(_ =>
                {
                    Volatile.Write(ref _renderTickScheduled, 0);
                    var c = _control;
                    if (c is not null && !_disposed && HasPendingFrame)
                        c.InvalidateVisual();
                });
            });
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _renderTickScheduled, 0);
                var c = _control;
                if (c is not null && !_disposed && HasPendingFrame)
                    c.InvalidateVisual();
            });
        }
    }

    // 渲染线程回调计数（冻结看门狗心跳）。
    private int _renderCallbacks;

    /// <summary>
    /// 初始化 <see cref="SkiaGpuVideoRenderer"/> 的新实例。
    /// </summary>
    /// <param name="surfaceFactories">共享表面源工厂集合（DI 注入）。</param>
    /// <param name="hostPresenters">宿主表面呈现适配器集合（DI 注入，能力自报选择）。</param>
    /// <param name="logger">日志。</param>
    internal SkiaGpuVideoRenderer(
        IEnumerable<ISharedGpuSurfaceSourceFactory> surfaceFactories,
        IEnumerable<IHostSurfacePresenter> hostPresenters,
        ILogger<SkiaGpuVideoRenderer> logger)
    {
        _surfaceFactories = surfaceFactories ?? throw new ArgumentNullException(nameof(surfaceFactories));
        _hostPresenters = hostPresenters?.ToArray() ?? throw new ArgumentNullException(nameof(hostPresenters));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public TimeSpan PresentationLatency => PresentLatency;

    /// <inheritdoc/>
    public event Action? Unhealthy;

    /// <inheritdoc/>
    public void Attach(IRenderTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (target.NativeHandle is not Visual visual)
            throw new NotSupportedException("SkiaGpuVideoRenderer 需要 IRenderTarget.NativeHandle 为 Avalonia Visual。");

        _visual = visual;
        if (visual is Control control)
        {
            _control = control;
            _topLevel = TopLevel.GetTopLevel(control);
            _controlSize = new Vector(control.Bounds.Width, control.Bounds.Height);
            _stretch = control.GetValue(VideoView.StretchProperty);
            _stretchSubscription = control.GetObservable(VideoView.StretchProperty)
                .Subscribe(new StretchObserver(this));
        }

        // 共享表面源能力快照（IsAvailable 轻量判定）；运行期按此列表做源级回退，
        // 骨架不含句柄类型分支——描述符与呈现适配器的兼容性由 IHostSurfacePresenter 匹配。
        _availableFactories = _surfaceFactories.Where(f => f.IsAvailable).ToList();
        if (_availableFactories.Count == 0)
            throw new NotSupportedException(
                "当前环境无可用共享表面源工厂（须由 GPU 胶水/渲染器工程注册 ISharedGpuSurfaceSourceFactory），Skia GPU 直绘不可用。");

        while (_triedCount < _availableFactories.Count && _source is null)
        {
            var factory = _availableFactories[_triedCount];
            _triedCount++;
            try
            {
                _source = factory.Create();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "共享表面源工厂（{Factory}）Create 失败，尝试下一个。",
                    factory.GetType().Name);
            }
        }
        if (_source is null)
            throw new NotSupportedException(
                "全部共享表面源工厂创建失败，Skia GPU 直绘不可用。");

        _logger.LogInformation("[SKIA-GPU] SkiaGpuVideoRenderer 挂载成功，源={Source}（{Done}/{Total}）。",
            _source.GetType().Name, _triedCount, _availableFactories.Count);

        // CompositionCustomVisual 上屏路径在 Android 禁用：EGL/Vulkan 后端下
        // SendHandlerMessage 消息不达 handler、OnAnimationFrameUpdate 不回调、合成器不渲染该 visual，
        // 且该路径会顶掉 DrawOp 上屏路径导致黑屏。
        const bool EnableCompositionCustomVisual = false;
        if (EnableCompositionCustomVisual && OperatingSystem.IsAndroid())
            AttachCompositionCustomVisual(visual);
    }

    /// <inheritdoc/>
    public void Detach()
    {
        DetachCompositionCustomVisual();
        UnsubscribeControl();
    }

    // 合成 Custom Visual（Android EGL 后端上屏路径）
    private VideoCompositionCustomVisualHandler? _compositionHandler;
    private CompositionCustomVisual? _compositionVisual;
    private Control? _compositionSizeHost;

    private void OnCompositionHostSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyCompositionVisualSize();

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_gate)
        {
            _hasPending = false;
            _pending = default;
        }
    }

    /// <summary>通知目标尺寸变化（VideoView 经 IAvaloniaRenderAware 调用，DIP 尺寸）。</summary>
    /// <remarks>GPU 直绘以 DIP 计算目标矩形（与 CPU Skia 路径同约定），scale 不参与几何。</remarks>
    public void Resize(int width, int height, float scale)
    {
        _controlSize = new Vector(width, height);
        if (_compositionVisual is not null && _visual is not null)
            _compositionVisual.Size = new Vector(_visual.Bounds.Width, _visual.Bounds.Height);
    }

    /// <summary>将缓存的表面快照绘制到 Avalonia DrawingContext（Avalonia 渲染线程调用）。</summary>
    /// <remarks>
    /// 合成器每帧重放已记录的 DrawOp——<b>不要</b>在此快照描述符进 op（快照会被重放旧帧）；
    /// op 内部每帧经 <see cref="TryGetLatestPresentation"/> 取最新待呈现帧。
    /// </remarks>
    public void Render(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_disposed)
            return;

        // Android：上屏由 CompositionCustomVisual 的帧循环驱动（见 AttachCompositionCustomVisual），
        // scene 重建路径不再挂 DrawOp（避免双通道重复绘制）。
        if (_compositionVisual is not null)
            return;

        // 渲染回调心跳（Trace 级，每 300 次 ≈ 10s@30fps）：统计本方法（scene 重建路径）
        // 的调度频率。Trace 级理由同交付心跳——Information 日志经双 provider 写 logcat，
        // 单次可达数十 ms，不应周期性落在 UI/渲染线程。
        _renderCallbacks++;
        if ((_renderCallbacks % 300) == 1)
            _logger.LogTrace("[SKIA-GPU] 渲染回调 #{N}（渲染线程活跃）", _renderCallbacks);

        var op = new SkiaGpuVideoDrawOp(this)
        {
            Bounds = new Rect(0, 0, _controlSize.X, _controlSize.Y),
        };
        drawingContext.Custom(op);
    }

    /// <summary>
    /// 取当前待呈现快照（锁内）：描述符 + 能力匹配的宿主呈现适配器 + 控件尺寸。
    /// 由 DrawOp 在合成重放时调用（渲染线程）；无待呈现帧返回 <see langword="false"/>。
    /// </summary>
    internal bool TryGetLatestPresentation(
        out SharedGpuSurfaceDescriptor descriptor,
        [NotNullWhen(true)] out IHostSurfacePresenter? presenter,
        out double controlW,
        out double controlH,
        out Stretch stretch)
    {
        descriptor = default;
        presenter = null;
        controlW = _controlSize.X;
        controlH = _controlSize.Y;
        stretch = _stretch;

        lock (_gate)
        {
            if (!_hasPending)
                return false;
            descriptor = _pending;
        }

        // 能力自报选择宿主呈现适配器（DI 注册顺序第一个匹配者，骨架无句柄类型硬编码）。
        foreach (var candidate in _hostPresenters)
        {
            if (candidate.CanPresent(descriptor))
            {
                presenter = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>是否有待呈现帧（合成 Custom Visual 的帧循环查询）。</summary>
    public bool HasPendingFrame
    {
        get
        {
            lock (_gate)
                return _hasPending && !_disposed;
        }
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>同步快速释放：共享表面源 Dispose 内含 DeviceWaitIdle，无 I/O 可 await，非伪异步。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DetachCompositionCustomVisual();
        UnsubscribeControl();
        lock (_gate)
        {
            _hasPending = false;
            _pending = default;
        }
        _source?.Dispose();
        _source = null;
    }

    /// <summary>Stretch 变化观察者（UI 线程回调，仅更新快照）。</summary>
    private sealed class StretchObserver(SkiaGpuVideoRenderer owner) : IObserver<Stretch>
    {
        public void OnNext(Stretch value) => owner._stretch = value;
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

/// <summary>
/// <see cref="SkiaGpuVideoRenderer"/> 的工厂（同 device Skia GPU 直绘）。
/// </summary>
/// <remarks>
/// <para>类名含 <c>SkiaGpuVideoRenderer</c> 以支持 <c>VideoView.RendererType</c> 前置匹配。
/// 回退链位置：在 Composition 渲染器之后、CPU Skia 之前——Android 上 Composition 因
/// <see cref="SharedGpuHandleKind.VulkanNativeImage"/> 不被合成器支持而让位，本工厂接手直绘；
/// Windows/Linux/Apple 上无 VulkanNativeImage 源，Attach 抛 <see cref="NotSupportedException"/>
/// 自动跳过，既有 Composition 路径不受影响。</para>
/// <para><b>AOT 兼容</b>：sealed 类，构造函数自动解析，无反射。</para>
/// </remarks>
public sealed class SkiaGpuVideoRendererFactory : IVideoRendererFactory
{
    private readonly IEnumerable<ISharedGpuSurfaceSourceFactory> _surfaceFactories;
    private readonly IEnumerable<IHostSurfacePresenter> _hostPresenters;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="SkiaGpuVideoRendererFactory"/> 的新实例。
    /// </summary>
    /// <param name="surfaceFactories">共享表面源工厂集合（DI 注入）。</param>
    /// <param name="hostPresenters">宿主表面呈现适配器集合（DI 注入，能力自报选择）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public SkiaGpuVideoRendererFactory(
        IEnumerable<ISharedGpuSurfaceSourceFactory> surfaceFactories,
        IEnumerable<IHostSurfacePresenter> hostPresenters,
        ILoggerFactory loggerFactory)
    {
        _surfaceFactories = surfaceFactories ?? throw new ArgumentNullException(nameof(surfaceFactories));
        _hostPresenters = hostPresenters ?? throw new ArgumentNullException(nameof(hostPresenters));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IVideoRenderer Create()
        => new SkiaGpuVideoRenderer(_surfaceFactories, _hostPresenters, _loggerFactory.CreateLogger<SkiaGpuVideoRenderer>());
}

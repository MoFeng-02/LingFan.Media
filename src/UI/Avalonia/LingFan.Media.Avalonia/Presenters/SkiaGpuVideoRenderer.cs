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
internal sealed class SkiaGpuVideoRenderer : IVideoRenderer, IAvaloniaRenderAware, IRendererHealth
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

    // ── 源级运行时回退 ──
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

    // ── 渲染线程消费心跳 ──
    // Present（管线线程）持续写入共享表面；DrawOp.Render（Avalonia 渲染线程）消费并绘制。
    // 若写入持续推进而渲染线程长时间未绘制，说明画面可能定格（管线侧计数与真实上屏脱节）。
    // Present 侧据此告警（节流：重新积累写入帧数后才可能再次触发），渲染线程侧零日志开销。
    private long _drawSeq;                  // 渲染线程已成功绘制的帧序号
    private long _deliveredFrames;          // 已交付渲染线程的帧计数（心跳用，不受源 Version 语义影响）
    private long _lastDrawQpc;              // 最近一次成功绘制的时刻
    private int _framesSinceLastDraw;       // 自上次成功绘制以来管线写入的帧数

    /// <summary>渲染线程完成一帧采样绘制后回调（记录心跳，供写入侧判定渲染线程是否停滞）。</summary>
    internal void OnDrawn()
    {
        Interlocked.Increment(ref _drawSeq);
        Volatile.Write(ref _lastDrawQpc, System.Diagnostics.Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _framesSinceLastDraw, 0);
    }

    /// <summary>渲染线程绘制诊断（DrawOp 调用：[DRAW-OP] 心跳/wrap 遥测/几何对账，Trace 级）。</summary>
    internal void LogDrawGeometry(string message)
        => _logger.LogTrace("[SKIA-GPU-DRAW] {Message}", message);

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

        // ⚠️ CompositionCustomVisual 上屏路径【挂起】（2026-09-07）：
        // 实验结论——Android EGL/Vulkan 后端下 SendHandlerMessage 消息不达 handler、
        // OnAnimationFrameUpdate 从不回调、合成器不渲染该 visual（真机多场实证 + 帧循环看门狗），
        // 且该路径会顶掉已验证的 DrawOp 上屏路径导致黑屏。挂载调用保留（下方 if 置 false），
        // 待 Avalonia composition 层查明后再启用。
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

    // ── 合成 Custom Visual（Android EGL 后端上屏路径）──
    private VideoCompositionCustomVisualHandler? _compositionHandler;
    private CompositionCustomVisual? _compositionVisual;
    private Control? _compositionSizeHost;

    /// <summary>把 custom visual 尺寸同步为宿主当前尺寸（零尺寸会被合成器跳过，必须非 0）。</summary>
    private void ApplyCompositionVisualSize()
    {
        if (_compositionVisual is null || _visual is null)
            return;
        var b = _visual.Bounds;
        if (b.Width > 0 && b.Height > 0)
            _compositionVisual.Size = new Vector(b.Width, b.Height);
    }

    private void OnCompositionHostSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyCompositionVisualSize();

    /// <summary>把合成 Custom Visual 挂为控件子视觉并启动帧循环。</summary>
    private void AttachCompositionCustomVisual(Visual visual)
    {
        try
        {
            var compositor = ElementComposition.GetElementVisual(visual)?.Compositor;
            if (compositor is null)
            {
                _logger.LogWarning("无法取得 Compositor，CompositionCustomVisual 上屏路径不可用（回退 DrawOp 路径）。");
                return;
            }

            _compositionHandler = new VideoCompositionCustomVisualHandler();
            _compositionVisual = compositor.CreateCustomVisual(_compositionHandler);
            ElementComposition.SetElementChildVisual(visual, _compositionVisual);
            _compositionVisual.Offset = new Vector3(0, 0, 0);
            // 尺寸：Attach 时布局可能尚未完成（Bounds 仍为 0）——零尺寸子视觉不会被合成器渲染，
            // 故延后到下一 UI 循环读取 post-layout 尺寸，并持续订阅尺寸变化（同 Composition 渲染器做法）。
            if (visual is Control control)
            {
                control.SizeChanged += OnCompositionHostSizeChanged;
                _compositionSizeHost = control;
            }
            Dispatcher.UIThread.Post(ApplyCompositionVisualSize);
            // 消息顺序：先绑帧来源，再启动帧循环。
            _compositionVisual.SendHandlerMessage(_compositionHandler);
            _compositionVisual.SendHandlerMessage(VideoCompositionCustomVisualHandler.StartMessage);
            _logger.LogInformation("[SKIA-GPU] CompositionCustomVisual 已挂载（render loop 每帧驱动上屏）。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CompositionCustomVisual 挂载失败，回退 DrawOp 路径。");
            _compositionHandler = null;
            _compositionVisual = null;
        }
    }

    /// <summary>停止帧循环并解除子视觉挂载。</summary>
    private void DetachCompositionCustomVisual()
    {
        if (_compositionVisual is not null)
        {
            _compositionVisual.SendHandlerMessage(VideoCompositionCustomVisualHandler.StopMessage);
            if (_visual is not null)
                ElementComposition.SetElementChildVisual(_visual, null);
            _compositionVisual = null;
        }
        if (_compositionSizeHost is not null)
        {
            _compositionSizeHost.SizeChanged -= OnCompositionHostSizeChanged;
            _compositionSizeHost = null;
        }
        _compositionHandler = null;
    }

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        if (_disposed)
            return;

        // 源级回退：渲染线程失败达阈值后置位请求，由本（管线）线程串行执行切换；
        // 切换与旧源释放在渲染线程当帧结束后进行，无并发采样。
        if (Interlocked.Exchange(ref _sourceSwitchPending, 0) == 1)
            SwitchSourceCore();

        if (_source is null)
        {
            RegisterFailure();
            return;
        }

        try
        {
            if (!_source.TryWriteFrame(frame, out SharedGpuSurfaceDescriptor desc) || !desc.IsValid)
            {
                RegisterFailure();
                return;
            }

            lock (_gate)
            {
                _pending = desc;
                _hasPending = true;
            }

            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                _logger.LogInformation(
                    "[SKIA-GPU] 首帧已写入共享表面 {W}x{H} version={Version}，等待渲染线程直绘。",
                    desc.Width, desc.Height, desc.Version);
            }
            _consecutiveFailures = 0;

            // 交付到渲染线程的入口心跳（不依赖合成循环）：证明帧确实抵达渲染器输入端。
            // 若本条出现而下游无绘制 ⇒ 卡在 Avalonia 渲染循环/合成调度，而非解码或导入链。
            _deliveredFrames++;
            if ((_deliveredFrames % 60) == 1)
                _logger.LogInformation(
                    "[SKIA-GPU] 已交付渲染线程 #{N} v={Version} kind={Kind} {W}x{H}",
                    _deliveredFrames, desc.Version, desc.Kind, desc.Width, desc.Height);

            // 渲染线程消费心跳检查：管线持续写入而渲染线程长时间未绘制 ⇒ 画面可能定格。
            // 仅告警不干预——真实修复取决于渲染线程侧状态（包装失败已有 OnDrawFailure 通道）。
            var lastDraw = Volatile.Read(ref _lastDrawQpc);
            // 从未成功绘制（lastDraw==0）也算停滞：原判据会静默跳过，掩盖「渲染线程从未上屏」。
            if (Interlocked.Increment(ref _framesSinceLastDraw) > 90)
            {
                bool neverDrawn = lastDraw == 0;
                double drawIdleSec = neverDrawn ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(lastDraw).TotalSeconds;
                if (neverDrawn) drawIdleSec = 999;
                if (drawIdleSec > 3.0)
                {
                    _logger.LogWarning(
                        neverDrawn
                            ? "[SKIA-GPU-STALL] 管线已连续写入 {N} 帧，渲染线程【从未成功绘制】（最后绘制序号={Seq}）⇒ 上屏路径未工作（合成循环未驱动或全部包装失败）。"
                            : "[SKIA-GPU-STALL] 管线已连续写入 {N} 帧，渲染线程 {Sec:F1}s 未绘制（最后绘制序号={Seq}）⇒ 画面可能定格，管线侧计数与真实上屏脱节。",
                        _framesSinceLastDraw, drawIdleSec, Volatile.Read(ref _drawSeq));
                    Interlocked.Exchange(ref _framesSinceLastDraw, 0);

                    // 合成帧循环看门狗：已挂 CompositionCustomVisual 却从未收到帧回调
                    // （render loop 未驱动）⇒ 卸载并回退 DrawOp 路径，避免新上屏路径拖死既有路线。
                    if (_compositionVisual is not null && (_compositionHandler?.FrameLoopCount ?? 0) == 0)
                    {
                        _logger.LogWarning(
                            "[SKIA-GPU] 合成帧循环零回调（render loop 未驱动），卸载 CompositionCustomVisual 回退 DrawOp 路径。");
                        DetachCompositionCustomVisual();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SkiaGpuVideoRenderer Present 失败。");
            RegisterFailure();
        }
    }

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

        // 渲染回调心跳（Information 级，每 60 次）：统计本方法（scene 重建路径）的调度频率。
        _renderCallbacks++;
        if ((_renderCallbacks % 60) == 1)
            _logger.LogInformation("[SKIA-GPU] 渲染回调 #{N}（渲染线程活跃）", _renderCallbacks);

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

    /// <summary>合成 Custom Visual 的诊断通道（渲染线程 handler → ILogger）。</summary>
    internal void LogCompositionDiagnostic(string message)
        => _logger.LogInformation("[SKIA-GPU] {Message}", message);

    /// <summary>是否有待呈现帧（合成 Custom Visual 的帧循环查询）。</summary>
    public bool HasPendingFrame
    {
        get
        {
            lock (_gate)
                return _hasPending && !_disposed;
        }
    }

    /// <summary>
    /// 合成 Custom Visual 的渲染回调（渲染线程，Avalonia render loop 每帧驱动）：
    /// 经宿主呈现适配器采样最新共享表面并绘制。Android EGL 后端的主上屏路径——
    /// 该后端的合成循环不响应跨线程 InvalidateVisual 驱动的 Custom op 重渲染。
    /// </summary>
    public void RenderCompositionFrame(ImmediateDrawingContext drawingContext, Vector effectiveSize)
    {
        if (_disposed)
            return;

        if (!TryGetLatestPresentation(out var descriptor, out var presenter, out var controlW, out var controlH, out var stretch))
            return;

        if (presenter is null)
        {
            OnDrawFailure("无可呈现该描述符的宿主呈现适配器（须注册 IHostSurfacePresenter 实现）");
            return;
        }

        if (drawingContext.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
        {
            OnDrawFailure("无 ISkiaSharpApiLeaseFeature（非 Skia 渲染后端）");
            return;
        }

        using ISkiaSharpApiLease lease = leaseFeature.Lease();
        GRContext? gr = lease.GrContext;
        SKCanvas? canvas = lease.SkCanvas;
        if (gr is null || canvas is null)
        {
            OnDrawFailure("Lease 缺 GrContext/SkCanvas");
            return;
        }

        SkiaWrappedSurface? wrapped = null;
        try
        {
            if (!presenter.TryWrap(gr, descriptor, out wrapped, out string? wrapFail))
            {
                OnDrawFailure(wrapFail ?? "共享表面包装失败");
                return;
            }

            var image = wrapped.Image;
            var dest = ComputeDestRectCore(descriptor, effectiveSize.X, effectiveSize.Y, stretch);
            int rot = ((int)descriptor.RotationDegrees % 360 + 360) % 360;
            if (rot == 90 || rot == 180 || rot == 270)
            {
                int save = canvas.Save();
                canvas.Translate(dest.MidX, dest.MidY);
                canvas.RotateDegrees(rot);
                float rw = (rot == 90 || rot == 270) ? dest.Height : dest.Width;
                float rh = (rot == 90 || rot == 270) ? dest.Width : dest.Height;
                canvas.DrawImage(image, new SKRect(-rw / 2, -rh / 2, rw / 2, rh / 2),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
                canvas.RestoreToCount(save);
            }
            else
            {
                canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
            }

            OnDrawn();
        }
        catch (Exception ex)
        {
            OnDrawFailure($"包装/绘制异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            wrapped?.Dispose();
        }
    }

    /// <summary>按拉伸模式计算目标矩形（DIP），画面恒不越控件边界。
    /// 90/270° 旋转时以「旋转后的显示宽高」（互换）参与适配——dest 矩形是画面最终呈现区域。</summary>
    internal static SKRect ComputeDestRectCore(in SharedGpuSurfaceDescriptor descriptor, double controlW, double controlH, Stretch stretch)
    {
        double fw = descriptor.Width;
        double fh = descriptor.Height;
        int rot = ((int)descriptor.RotationDegrees % 360 + 360) % 360;
        if (rot == 90 || rot == 270)
            (fw, fh) = (fh, fw);
        if (fw <= 0 || fh <= 0 || controlW <= 0 || controlH <= 0)
            return SKRect.Empty;

        double scaleX = controlW / fw;
        double scaleY = controlH / fh;
        double scale, dx, dy;
        switch (stretch)
        {
            case Stretch.Fill:
                return new SKRect(0, 0, (float)controlW, (float)controlH);
            case Stretch.UniformToFill:
                scale = Math.Max(scaleX, scaleY);
                dx = (controlW - fw * scale) / 2;
                dy = (controlH - fh * scale) / 2;
                break;
            default: // Uniform（默认）
                scale = Math.Min(scaleX, scaleY);
                dx = (controlW - fw * scale) / 2;
                dy = (controlH - fh * scale) / 2;
                break;
        }

        double w = fw * scale;
        double h = fh * scale;
        // 裁剪保险：UniformToFill 可能越界，夹回控件内。
        double left = Math.Max(0, Math.Min(dx, controlW));
        double top = Math.Max(0, Math.Min(dy, controlH));
        double right = Math.Max(left, Math.Min(dx + w, controlW));
        double bottom = Math.Max(top, Math.Min(dy + h, controlH));
        return new SKRect((float)left, (float)top, (float)right, (float)bottom);
    }

    /// <summary>渲染线程回调：绘制失败计数（达阈值请求源级切换；全部源穷尽后触发 Unhealthy）。</summary>
    internal void OnDrawFailure(string reason)
    {
        // 失败原因按内容去重（不同原因各打一次）——全局一次性会吞掉换源后新形态的失败细节。
        if (reason != _lastFailureReason)
        {
            _lastFailureReason = reason;
            _logger.LogWarning("[SKIA-GPU] 渲染线程直绘失败：{Reason}（连续失败将切换共享表面源）。", reason);
        }
        RegisterFailure();

        // 保底驱动：失败帧没有任何像素变化，若宿主合成循环因此不再调度渲染回调，
        // 失败计数与源级切换都无从推进——主动请求一次重绘以维持失败处理循环。
        var control = _control;
        if (control is not null)
            Dispatcher.UIThread.Post(() => control.InvalidateVisual());
    }

    private void RegisterFailure()
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        if (_unhealthyFired != 0)
            return;
        if (failures < SourceFailureThreshold)
            return;

        // 源级回退：还有未尝试的源 → 请求切换（Present 在管线线程执行）；穷尽 → Unhealthy。
        if (_triedCount < _availableFactories.Count)
        {
            if (Interlocked.Exchange(ref _sourceSwitchPending, 1) == 0)
            {
                _logger.LogWarning(
                    "[SKIA-GPU] 连续 {N} 帧呈现失败，请求切换共享表面源（{Done}/{Total}）。",
                    failures, _triedCount, _availableFactories.Count);
                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }
            return;
        }

        if (Interlocked.CompareExchange(ref _unhealthyFired, 1, 0) == 0)
        {
            _logger.LogWarning(
                "[SKIA-GPU] 全部 {Total} 个共享表面源呈现失败，触发渲染器回退（软渲染）。",
                _availableFactories.Count);
            Unhealthy?.Invoke();
        }
    }

    /// <summary>
    /// 切换到下一个共享表面源（管线线程串行调用）：释放当前源并创建下一候选；
    /// 全部候选穷尽时触发 Unhealthy 交由宿主整体回退。
    /// </summary>
    private void SwitchSourceCore()
    {
        while (_triedCount < _availableFactories.Count)
        {
            var factory = _availableFactories[_triedCount];
            _triedCount++;
            try
            {
                var candidate = factory.Create();
                _source?.Dispose();
                _source = candidate;
                _firstFrameLogged = false;
                lock (_gate)
                {
                    _hasPending = false;
                    _pending = default;
                }
                _logger.LogInformation(
                    "[SKIA-GPU] 已切换共享表面源={Source}（{Done}/{Total}）。",
                    _source.GetType().Name, _triedCount, _availableFactories.Count);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "共享表面源工厂（{Factory}）Create 失败，尝试下一个。",
                    factory.GetType().Name);
            }
        }

        // 全部候选穷尽：交由宿主整体回退（CPU Skia 兜底）。
        if (Interlocked.CompareExchange(ref _unhealthyFired, 1, 0) == 0)
        {
            _logger.LogWarning(
                "[SKIA-GPU] 全部 {Total} 个共享表面源尝试完毕，触发渲染器回退（软渲染）。",
                _availableFactories.Count);
            Unhealthy?.Invoke();
        }
    }

    private void UnsubscribeControl()
    {
        _control = null;
        _stretchSubscription?.Dispose();
        _stretchSubscription = null;
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
/// Skia GPU 直绘的帧绘制操作骨架：把共享表面描述符经宿主呈现适配器包装为可采样图像并绘制
/// （Avalonia 渲染线程，经 <see cref="DrawingContext.Custom"/> 挂入合成树）。
/// </summary>
/// <remarks>
/// <para>骨架职责：lease 获取、适配器选择结果执行、几何/旋转/拉伸、释放与遥测——
/// 「如何把描述符包装为可采样图像」由 <see cref="IHostSurfacePresenter"/> 实现承担，
/// 本骨架不含任何句柄类型/GPU API 分支。</para>
/// <para>包装生命周期：绘制内现场创建、绘制后立即释放——Ganesh 持有已记录命令的代理引用，
/// 释放包装不影响本帧已记录的采样；不跨帧缓存，规避上下文重建悬挂与线程亲和问题。</para>
/// <para>采样同步契约（生产者保证，见共享表面源实现）：交付布局已对采样可见、生产/消费
/// 共用同一队列（同队列提交按序串行）。本侧不做任何布局转换或同步原语。</para>
/// </remarks>
internal sealed class SkiaGpuVideoDrawOp : ICustomDrawOperation
{
    private readonly SkiaGpuVideoRenderer _owner;
    private static bool _drawGeomLogged; // 一次性几何对账打点标志（static：DrawOp 每帧新建实例，实例字段会每帧打点）
    private static uint _dbgFrameSeq; // 绘制帧序号（跨 DrawOp 实例单调；[DRAW-OP] 心跳与 wrap 遥测共用，Trace 级）

    /// <summary>初始化帧绘制操作。描述符与适配器在每次重放时经 owner 实时拉取（不快照）。</summary>
    /// <param name="owner">宿主渲染器（最新待呈现帧、失败计数回调用）。</param>
    public SkiaGpuVideoDrawOp(SkiaGpuVideoRenderer owner)
    {
        _owner = owner;
    }

    /// <summary>脏区边界（控件可视区）。</summary>
    public Rect Bounds { get; set; }

    /// <inheritdoc/>
    public void Render(ImmediateDrawingContext context)
    {
        // 绘制阶段心跳（Trace 级）：DrawOp.Render 才是真正往画布画东西的回调（渲染线程）。
        // 冻结窗口内心跳持续 ⇒ 绘制在跑但未上屏（提交/交换链停摆）；
        // 心跳停而管线侧心跳仍在 ⇒ 渲染线程被楔死（GPU 等待永不返回）；
        // wrap=FAIL ⇒ SKImage.FromTexture 拒收（声明与创建 usage/layout 不一致的经典症状）。
        // 实时拉取最新待呈现帧与适配器（合成器每帧重放本 op——绝不快照旧帧）。
        if (!_owner.TryGetLatestPresentation(out var descriptor, out var presenter, out var controlW, out var controlH, out var stretch))
            return;

        uint n = Interlocked.Increment(ref _dbgFrameSeq);
        if ((n % 60) == 1)
            _owner.LogDrawGeometry(
                $"[DRAW-OP] #{n} desc={descriptor.Width}x{descriptor.Height} layout={descriptor.NativeImageLayout}");
        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
        {
            _owner.OnDrawFailure("无 ISkiaSharpApiLeaseFeature（非 Skia 渲染后端）");
            return;
        }

        using ISkiaSharpApiLease lease = leaseFeature.Lease();
        GRContext? gr = lease.GrContext;
        SKCanvas? canvas = lease.SkCanvas;
        if (gr is null || canvas is null)
        {
            _owner.OnDrawFailure("Lease 缺 GrContext/SkCanvas");
            return;
        }

        SkiaWrappedSurface? wrapped = null;
        try
        {
            // 共享表面 → 可采样图像：由宿主呈现适配器完成（骨架不含任何句柄类型/GPU API 分支）。
            if (!presenter.TryWrap(gr, descriptor, out wrapped, out string? wrapFail))
            {
                _owner.OnDrawFailure(wrapFail ?? "共享表面包装失败");
                return;
            }
            // 【包装遥测】每 60 帧报一次（与 DRAW-OP 心跳同 n 对齐），让包装成败在任何窗口都可见。
            if ((n % 60) == 1)
                _owner.LogDrawGeometry(
                    $"[DRAW-OP] #{n} wrap=OK layout={descriptor.NativeImageLayout} usage=0x{descriptor.NativeImageUsage:X}");

            var image = wrapped.Image;

            // 旋转呈现：容器声明的显示旋转（90/180/270）。源纹理像素本身不旋转，
            // 以 canvas 变换把已算好的 dest 矩形（按旋转后的显示宽高适配）转回源方向绘制。
            int rot = ((int)descriptor.RotationDegrees % 360 + 360) % 360;
            SKRect dest = ComputeDestRect(descriptor, controlW, controlH, stretch);
            // 【诊断】一次性对账坐标单位（放大溢出排查）：desc/控件(DIP)/dest/画布本地与设备裁剪盒。
            // LocalClipBounds=canvas 当前变换下的本地单位；DeviceClipBounds=物理像素。
            // 两者比值 = 画布有效缩放。dest 落在 Local 内 = 单位一致（DIP）；溢出 Device = 单位错位。
            if (!_drawGeomLogged)
            {
                _drawGeomLogged = true;
                _owner.LogDrawGeometry(
                    $"desc={descriptor.Width}x{descriptor.Height} control={controlW:F0}x{controlH:F0} " +
                    $"dest={dest} localClip={canvas.LocalClipBounds} deviceClip={canvas.DeviceClipBounds}");
            }
            if (rot == 90 || rot == 180 || rot == 270)
            {
                int save = canvas.Save();
                canvas.Translate(dest.MidX, dest.MidY);
                canvas.RotateDegrees(rot);
                // 旋转后源图占据的矩形：宽高随角度互换。
                float rw = (rot == 90 || rot == 270) ? dest.Height : dest.Width;
                float rh = (rot == 90 || rot == 270) ? dest.Width : dest.Height;
                canvas.DrawImage(image, new SKRect(-rw / 2, -rh / 2, rw / 2, rh / 2),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
                canvas.RestoreToCount(save);
            }
            else
            {
                // 线性过滤（视频缩放平滑）；无 Mipmap（单级纹理）。
                canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
            }

            // 本帧采样命令已成功记录：向宿主回报渲染线程心跳（管线侧据此判定画面是否定格）。
            _owner.OnDrawn();
        }
        catch (Exception ex)
        {
            _owner.OnDrawFailure($"包装/绘制异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 释放包装（先图像后纹理包装；借用的 VkImage/VkDeviceMemory 等共享表面不受影响——
            // Ganesh 不拥有外来纹理，已记录的本帧采样命令由 Ganesh 代理引用保活）。
            wrapped?.Dispose();
        }
    }

    private SKRect ComputeDestRect(in SharedGpuSurfaceDescriptor descriptor, double controlW, double controlH, Stretch stretch)
        => SkiaGpuVideoRenderer.ComputeDestRectCore(descriptor, controlW, controlH, stretch);

    /// <inheritdoc/>
    public bool HitTest(Point p) => false;

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    bool IEquatable<ICustomDrawOperation>.Equals(ICustomDrawOperation? other) => false;
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

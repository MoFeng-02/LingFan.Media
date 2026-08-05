using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using LingFan.Media.Presenters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 视频显示控件。继承 <see cref="Control"/>（非 OpenGlControlBase），实现 <see cref="IRenderTarget"/>。
/// </summary>
/// <remarks>
/// <para><b>继承选择</b>：不继承 OpenGlControlBase——那会绑死 OpenGL 上下文管理。
/// 继承 Control + 实现 IRenderTarget 使 VideoView 可运行时切换任意 GPU 后端（Skia/D3D11/Vulkan/Metal/OpenGL）。</para>
/// <para><b>无空域设计</b>：两种渲染模式均不创建独立原生窗口：</para>
/// <list type="bullet">
/// <item>UI 模式：重写 Render(DrawingContext)，视频帧作为 SKImage 绘制到 Avalonia 合成树</item>
/// <item>原生 GPU 模式：通过平台合成器子层将 SwapChain 合成进窗口
/// （Windows: DirectComposition / macOS: CAMetalLayer / Linux: Wayland subsurface / Android: TextureView / iOS: CAMetalLayer）</item>
/// </list>
/// <para>两种模式下 UI 控件均可自由覆盖视频上方，无 z-order / 裁剪 / DPI 问题。</para>
/// <para><b>异步策略</b>（遵守异步同步分类表）：</para>
/// <list type="bullet">
/// <item>OnAttachedToVisualTree：sync——创建 Presenter + Initialize，纯内存</item>
/// <item>OnDetachedFromVisualTree：V1 sync（SkiaVideoPresenter.Dispose 同步，无 I/O 可 await）。
/// V2 原生 GPU 模式新增 <see cref="DisposePlayerAsync"/> 供消费方显式调用（**async void 绝对禁止**），
/// void 覆写内调同步清理兜底。</item>
/// <item>Render(DrawingContext)：native——Avalonia 框架 void 签名硬限制</item>
/// <item>OnSizeChanged：sync——通知 Presenter.Resize，纯内存</item>
/// <item>OnPlayerChanged：sync 设置——检查 Session 是否已就绪（V1 不 await OpenAsync，消费方应先调用 OpenAsync 再绑定 Player）</item>
/// </list>
/// <para><b>数据绑定</b>：使用 Avalonia 原生 StyledProperty&lt;T&gt;，不依赖 MVVM 框架。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// <example>
/// 推荐用法——消费方应先 <c>await player.OpenAsync()</c> 完成（Session 就绪后），
/// 再将 player 绑定到 VideoView.Player 属性：
/// <code>
/// var player = serviceProvider.GetRequiredService&lt;IMediaPlayer&gt;();
/// await player.OpenAsync(source); // 先 Open
/// videoView.Player = player;      // 再绑定
/// </code>
/// 释放时推荐异步路径：
/// <code>
/// await videoView.DisposePlayerAsync(); // V2 推荐路径
/// </code>
/// </example>
/// </remarks>
public sealed class VideoView : Control, IRenderTarget
{
    private IGpuPresenter? _presenter;
    private IMediaPlayer? _player;
    private SubtitleFrame? _currentSubtitle;
    private IServiceProvider? _services;
    private ILogger? _logger;
    private bool _videoSinkSubscribed;

    #region StyledProperties

    /// <summary>绑定播放器的 StyledProperty。</summary>
    public static readonly StyledProperty<IMediaPlayer?> PlayerProperty =
        AvaloniaProperty.Register<VideoView, IMediaPlayer?>(nameof(Player));

    /// <summary>渲染器类型的 StyledProperty。</summary>
    public static readonly StyledProperty<Type?> RendererTypeProperty =
        AvaloniaProperty.Register<VideoView, Type?>(nameof(RendererType), defaultValue: typeof(SkiaVideoPresenter));

    /// <summary>宽高比模式的 StyledProperty。</summary>
    public static readonly StyledProperty<AspectRatioMode> AspectRatioModeProperty =
        AvaloniaProperty.Register<VideoView, AspectRatioMode>(nameof(AspectRatioMode), defaultValue: AspectRatioMode.Uniform);

    /// <summary>拉伸模式的 StyledProperty。</summary>
    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<VideoView, Stretch>(nameof(Stretch), defaultValue: Stretch.Uniform);

    /// <summary>是否正在加载的 StyledProperty（只读）。</summary>
    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<VideoView, bool>(nameof(IsLoading));

    #endregion

    #region Events

    /// <summary>绑定的播放器变化时触发。</summary>
    public event EventHandler? PlayerChanged;

    /// <summary>帧渲染完成时触发。</summary>
    public event EventHandler? FrameRendered;

    // SizeChanged 事件继承自 Layoutable（使用 SizeChangedEventArgs，含 NewSize/PreviousSize）。

    #endregion

    #region StyledProperty Accessors

    /// <summary>绑定播放器。</summary>
    public IMediaPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <summary>渲染器类型（默认 SkiaVideoPresenter，可切换 D3D11/Vulkan/Metal/OpenGL）。</summary>
    public Type? RendererType
    {
        get => GetValue(RendererTypeProperty);
        set => SetValue(RendererTypeProperty, value);
    }

    /// <summary>
    /// 宿主注入的 DI 服务容器。供 VideoView 按 <see cref="RendererType"/> 解析已注册的
    /// <see cref="IVideoPresenterFactory"/>（如 D3D11 GPU Presenter 桥接项目）。未注入时回退到内置
    /// <see cref="SkiaVideoPresenter"/>。VideoView 不引用桥接项目，仅用 Type 对象比较，符合依赖倒置（D1 方案 B）。
    /// </summary>
    public IServiceProvider? Services
    {
        get => _services;
        set => _services = value;
    }

    /// <summary>宽高比模式。</summary>
    public AspectRatioMode AspectRatioMode
    {
        get => GetValue(AspectRatioModeProperty);
        set => SetValue(AspectRatioModeProperty, value);
    }

    /// <summary>拉伸模式。</summary>
    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <summary>是否正在加载（只读）。</summary>
    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        private set => SetValue(IsLoadingProperty, value);
    }

    #endregion

    #region IRenderTarget Implementation

    /// <inheritdoc/>
    RenderTargetType IRenderTarget.Type => RenderTargetType.Window;

    /// <inheritdoc/>
    RenderHandleType IRenderTarget.HandleType => RenderHandleType.None;

    /// <inheritdoc/>
    object IRenderTarget.NativeHandle => this;

    /// <inheritdoc/>
    int IRenderTarget.Width => (int)Bounds.Width;

    /// <inheritdoc/>
    int IRenderTarget.Height => (int)Bounds.Height;

    /// <inheritdoc/>
    float IRenderTarget.Scale => (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);

    #endregion

    /// <summary>
    /// 呈现一帧到 VideoView。由管线或桥接组件调用。
    /// 同步方法——委托给 SkiaVideoPresenter.Present（纯内存 + GPU操作）。
    /// </summary>
    /// <param name="frame">要呈现的视频帧（所有权转移给 Presenter）。</param>
    public void PresentFrame(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // 捕获引用防止 TOCTOU 竞态：_presenter 可能被 OnDetachedFromVisualTree（UI 线程）置 null
        var presenter = _presenter;
        if (presenter == null)
        {
            // Presenter 尚未初始化，Dispose 帧防止泄漏
            frame.Dispose();
            return;
        }

        presenter.Present(frame);
        Dispatcher.UIThread.Post(() => InvalidateVisual());
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EnsurePresenter();

        // 如果 Player 已绑定但 _player 字段为 null（重新挂载到视觉树后），重新绑定事件
        if (Player != null && _player == null)
        {
            AttachPlayer(Player);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>V1 同步兜底</b>：SkiaVideoPresenter.Dispose 是同步的，无 I/O 可 await。
    /// <b>V2 推荐路径</b>：消费方先 <c>await DisposePlayerAsync()</c> 释放 GPU 资源（GPU flush + SwapChain 释放是真实异步 I/O），
    /// 再让控件 Detach。void 覆写不可改签名为 Task/ValueTask（<b>async void 绝对禁止</b>），
    /// 内调同步清理兜底——解绑播放器事件 + 同步释放 Presenter。
    /// 当前 V1 无伪异步——方法体内无 await 故不加 async 关键字。
    /// </remarks>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // 解绑播放器事件
        DetachPlayer();

        // 释放 Presenter（SkiaVideoPresenter.Dispose 是同步的）
        _presenter?.Dispose();
        _presenter = null;
    }

    /// <summary>
    /// 异步释放播放器和渲染器资源。供消费方在控件 Detach 前显式调用。
    /// </summary>
    /// <remarks>
    /// <para><b>V2 推荐路径</b>：原生 GPU 模式下，<c>await _player.DisposeAsync()</c> 释放 GPU 资源
    /// （GPU flush + SwapChain 释放是真实异步 I/O，不能同步阻塞 UI 线程）。</para>
    /// <para><b>ValueTask</b> 与 <see cref="IMediaPlayer.DisposeAsync()"/> 返回类型一致。</para>
    /// <para><b>async void 绝对禁止</b>——此方法不是 void 覆写，是新增独立 async 方法。</para>
    /// <para>void 覆写 <see cref="OnDetachedFromVisualTree"/> 内调同步清理兜底（解绑事件 + 同步释放 Presenter），
    /// 禁止调 <c>DisposeAsync().GetResult()</c> 伪异步。</para>
    /// </remarks>
    public async ValueTask DisposePlayerAsync()
    {
        // 1. 捕获 Player 引用（DetachPlayer 会将 _player 置 null）
        var player = _player;

        // 2. 解绑播放器事件（内部会将 _player 置 null）
        DetachPlayer();

        // 3. 如果绑定了 Player，异步释放（GPU flush + SwapChain + 线程 join）
        if (player != null)
        {
            await player.DisposeAsync();
        }

        // 4. 释放 Presenter（SkiaVideoPresenter.Dispose 是同步的）
        _presenter?.Dispose();
        _presenter = null;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext drawingContext)
    {
        base.Render(drawingContext);

        // 委托给 Presenter 绘制视频帧（GPU Presenter 实现 IGpuPresenter 不含 Render，自然跳过无空域合成）
        if (_presenter is IVideoPresenter presenter)
        {
            presenter.Render(drawingContext);
        }

        // 绘制字幕叠加层
        RenderSubtitle(drawingContext);

        FrameRendered?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 属性变化时处理 Player/RendererType 变化。
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PlayerProperty)
        {
            OnPlayerChanged(change.OldValue as IMediaPlayer, change.NewValue as IMediaPlayer);
        }
        else if (change.Property == RendererTypeProperty)
        {
            OnRendererTypeChanged();
        }
        else if (change.Property == AspectRatioModeProperty && _presenter is SkiaVideoPresenter skia)
        {
            skia.AspectRatioMode = (AspectRatioMode)change.NewValue!;
        }
    }

    /// <inheritdoc/>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _presenter?.Resize((int)e.NewSize.Width, (int)e.NewSize.Height, (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0));
    }

    #region Private Methods

    private void EnsurePresenter()
    {
        if (_presenter != null)
            return;

        var rendererType = RendererType ?? typeof(SkiaVideoPresenter);

        IGpuPresenter? created = null;

        // 优先通过 DI 注册的 IGpuPresenterFactory 按 RendererType 匹配（D1 方案 B 演进）。
        // VideoView 不引用具体 GPU 项目（如 GpuPresenter.D3D11），仅用 Type 对象比较，符合依赖倒置。
        // 工厂集合同时包含 SkiaPresenterFactory（继承 IGpuPresenterFactory）与各后端 GPU 工厂。
        if (_services is not null)
        {
            foreach (var factory in _services.GetServices<IGpuPresenterFactory>())
            {
                if (factory.PresenterType == rendererType)
                {
                    created = factory.Create();
                    break;
                }
            }
        }

        created ??= (rendererType == typeof(SkiaVideoPresenter))
            ? new SkiaVideoPresenter()
            : throw new NotSupportedException(
                $"渲染器类型 {rendererType.Name} 未注册对应的 IGpuPresenterFactory，且非 SkiaVideoPresenter。" +
                "请调用 AddD3D11Presenter()（或对应后端注册方法）以注册匹配的 IGpuPresenterFactory。");

        _presenter = created;

        // 统一初始化：尝试解析窗口 HWND 并构造 Pointer 渲染目标。
        // - GPU 路径（D3D11GpuPresenter 等）：需要 HWND 建 SwapChain。
        // - Skia 路径：只读 Width/Height/Scale（不碰 NativeHandle），GpuRenderTarget 同样满足。
        // HWND 解析依赖 Avalonia Visual/TopLevel，故放在 UI 层；IGpuPresenter 本身保持与 UI 无关。
        var hwnd = TryResolveHwnd();
        IRenderTarget initTarget = hwnd is not null
            ? new GpuRenderTarget(hwnd.Value, (int)Bounds.Width, (int)Bounds.Height,
                (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0))
            : this;

        // GPU 后端（D3D11 等）初始化可能因环境/驱动失败（非 Windows、无 GPU、Attach 异常等）。
        // 捕获异常并安全降级到内置 SkiaVideoPresenter，保证 UI 不崩、视频仍可软渲染呈现（无空域渲染）。
        try
        {
            _presenter.Initialize(initTarget);
        }
        catch (Exception ex)
        {
            _logger ??= _services?.GetService<ILogger<VideoView>>();
            _logger?.LogWarning(ex,
                "GPU Presenter（{RendererType}）初始化失败，降级到 SkiaVideoPresenter 软渲染。",
                rendererType.Name);
            try { _presenter.Dispose(); } catch { } // 释放失败的 GPU Presenter（其 Initialize 已清理部分资源）

            IGpuPresenter fallback = new SkiaVideoPresenter();
            try { fallback.Initialize(this); }       // Skia 用 VideoView 自身作渲染目标（只读尺寸/缩放）
            catch { /* 最后兜底：保留 fallback，后续 Present/Clear/Resize 均为安全空操作 */ }
            _presenter = fallback;
        }

        if (_presenter is SkiaVideoPresenter skia)
        {
            skia.AspectRatioMode = AspectRatioMode;
        }

        // V2-12: Presenter 创建/重建后重新评估视频帧 sink 订阅（Skia 订阅、D3D11/GPU 退订）。
        // GPU 初始化失败降级 Skia 时，此处会自动订阅 sink，使管线切到软渲染喂帧路径。
        UpdateVideoSinkSubscription();
    }

    /// <summary>
    /// 从 Avalonia 视觉树解析窗口平台句柄（HWND）。GPU Presenter 需要它创建 SwapChain。
    /// HWND 解析属于 UI 框架职责，推回 Avalonia 层；中立的 IGpuPresenter 只消费 Pointer 渲染目标。
    /// </summary>
    private IntPtr? TryResolveHwnd()
    {
        var handle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle;
        return handle is { } h && h != IntPtr.Zero ? h : null;
    }

    private void OnRendererTypeChanged()
    {
        if (_presenter != null)
        {
            _presenter.Dispose();
            _presenter = null;
        }
        EnsurePresenter();
    }

    /// <summary>
    /// 绑定的播放器变化时触发。void 回调不可改签名（<b>async void 绝对禁止</b>）。
    /// </summary>
    /// <remarks>
    /// <b>调用方契约</b>：消费方应先 <c>await player.OpenAsync()</c> 完成（Session 就绪后），
    /// 再将 player 绑定到 <see cref="Player"/> 属性。此方法是 void 回调不可改签名为 Task/ValueTask，
    /// 因此不能在此方法内 await OpenAsync。
    /// </remarks>
    private void OnPlayerChanged(IMediaPlayer? oldPlayer, IMediaPlayer? newPlayer)
    {
        if (oldPlayer != null)
        {
            DetachPlayer();
        }

        if (newPlayer != null)
        {
            AttachPlayer(newPlayer);
        }

        PlayerChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 绑定播放器：订阅事件 + 检查 Session 是否已就绪。
    /// </summary>
    /// <remarks>
    /// <b>调用方契约</b>：消费方应先 <c>await player.OpenAsync()</c> 完成（Session 就绪后），
    /// 再将 player 绑定到 <see cref="Player"/> 属性。
    /// 此方法由 void 回调 <see cref="OnPlayerChanged"/> 调用，不可改签名为 Task/ValueTask（<b>async void 绝对禁止</b>）。
    /// </remarks>
    private void AttachPlayer(IMediaPlayer player)
    {
        _player = player;
        player.StateChanged += OnPlayerStateChanged;
        player.SubtitleReceived += OnSubtitleReceived;

        // V2-12: 若当前为 Skia 软渲染 Presenter，订阅视频帧 sink（管线线程同步投递帧）。
        // D3D11 原生 GPU 模式不订阅——管线直接 Present 到已 Attach 的共享 SwapChain。
        UpdateVideoSinkSubscription();

        // 如果 Session 已就绪（OpenAsync 已完成），直接附加渲染器
        if (player.Session != null)
        {
            IsLoading = false;
        }
        else
        {
            IsLoading = true;
        }
    }

    /// <summary>
    /// 解绑播放器：取消订阅事件。
    /// </summary>
    private void DetachPlayer()
    {
        if (_player == null)
            return;

        // V2-12: 退订视频帧 sink（避免管线线程继续向已解绑 Player 投递帧）
        if (_videoSinkSubscribed)
        {
            _player.VideoFrameAvailable -= PresentFrame;
            _videoSinkSubscribed = false;
        }

        _player.StateChanged -= OnPlayerStateChanged;
        _player.SubtitleReceived -= OnSubtitleReceived;
        _player = null;
        _currentSubtitle = null;
    }

    /// <summary>
    /// V2-12 收敛：所有 <see cref="IGpuPresenter"/>（Skia 软渲染 / D3D11 零拷贝 GPU 等）均订阅
    /// <see cref="IMediaPlayer.VideoFrameAvailable"/>，经统一 FrameChannel 接收帧——有头与无头同饮一条通道，
    /// 不再区分"订阅 vs 直接 Present"两条路径（原 T19 硬编码分支已移除）。<see cref="PresentFrame"/> 内部
    /// 仅调 <c>_presenter.Present(frame)</c>：Skia 写位图并调度重绘，D3D11 经共享 SwapChain 零拷贝上屏。
    /// 幂等（由 <c>_videoSinkSubscribed</c> 标记防重复订阅）。
    /// </summary>
    private void UpdateVideoSinkSubscription()
    {
        var shouldSubscribe = _player != null && _presenter is IGpuPresenter;
        if (shouldSubscribe && !_videoSinkSubscribed)
        {
            _player!.VideoFrameAvailable += PresentFrame;
            _videoSinkSubscribed = true;
        }
        else if (!shouldSubscribe && _videoSinkSubscribed)
        {
            _player?.VideoFrameAvailable -= PresentFrame;
            _videoSinkSubscribed = false;
        }
    }

    private void OnPlayerStateChanged(object? sender, MediaStateChangedEventArgs e)
    {
        // StateChanged 事件可能从非 UI 线程触发，必须切换到 UI 线程设置 StyledProperty
        Dispatcher.UIThread.Post(() =>
        {
            if (e.NewState == MediaState.Playing || e.NewState == MediaState.Paused)
            {
                IsLoading = false;
            }
            else if (e.NewState == MediaState.Opening || e.NewState == MediaState.Buffering)
            {
                IsLoading = true;
            }
        });
    }

    private void OnSubtitleReceived(object? sender, SubtitleFrame? subtitle)
    {
        _currentSubtitle = subtitle;
        Dispatcher.UIThread.Post(() => InvalidateVisual());
    }

    /// <summary>
    /// 渲染字幕叠加层。在视频帧之上、UI 控件之下（z-order 居中）。
    /// </summary>
    private void RenderSubtitle(DrawingContext drawingContext)
    {
        // 捕获引用一次，防止 TOCTOU 竞态（OnSubtitleReceived 可能从非 UI 线程置 null）
        var subtitle = _currentSubtitle;
        if (subtitle == null || string.IsNullOrEmpty(subtitle.Text))
            return;

        var style = subtitle.Style ?? new SubtitleStyle();
        var fontSize = style.FontSize ?? 18.0f * (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);
        var fontFamily = style.FontFamily ?? "Segoe UI";
        var colorStr = style.Color ?? "#FFFFFFFF";

        var foreground = ParseColorBrush(colorStr, Colors.White);

        var typeface = new Typeface(fontFamily, FontStyle.Normal, FontWeight.Normal);

        var formattedText = new FormattedText(
            subtitle.Text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            foreground);

        var textWidth = formattedText.Width;
        var textHeight = formattedText.Height;
        var controlWidth = Bounds.Width;
        var controlHeight = Bounds.Height;
        var margin = 10.0;

        // 水平对齐
        double x = style.Alignment switch
        {
            SubtitleAlignment.Left => margin,
            SubtitleAlignment.Right => controlWidth - textWidth - margin,
            _ => (controlWidth - textWidth) / 2.0
        };

        // 垂直定位
        double y = style.Position switch
        {
            SubtitlePosition.Top => margin,
            SubtitlePosition.Center => (controlHeight - textHeight) / 2.0,
            _ => controlHeight - textHeight - margin
        };

        // 背景半透明矩形（增强可读性）
        var bgRect = new Rect(x - 4, y - 2, textWidth + 8, textHeight + 4);
        var bgBrush = ParseColorBrush(style.BackgroundColor ?? "#80000000", Colors.Transparent);
        drawingContext.DrawRectangle(bgBrush, null, bgRect, 3, 3);

        drawingContext.DrawText(formattedText, new Point(x, y));
    }

    private static IBrush ParseColorBrush(string hex, Color fallback)
    {
        try
        {
            return new SolidColorBrush(Color.Parse(hex));
        }
        catch
        {
            return new SolidColorBrush(fallback);
        }
    }

    #endregion
}

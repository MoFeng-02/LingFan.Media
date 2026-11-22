using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
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
/// <para><b>异步策略</b>（有真实 I/O 走 async、纯内存保持 sync）：</para>
/// <list type="bullet">
/// <item>OnAttachedToVisualTree：sync——创建 Presenter + Initialize，纯内存</item>
/// <item>OnDetachedFromVisualTree：sync（SkiaVideoRenderer.Dispose 同步，无 I/O 可 await）。
/// 原生 GPU 模式新增 <see cref="DisposePlayerAsync"/> 供消费方显式调用（**async void 绝对禁止**），
/// void 覆写内调同步清理兜底。</item>
/// <item>Render(DrawingContext)：native——Avalonia 框架 void 签名硬限制</item>
/// <item>OnSizeChanged：sync——通知 Presenter.Resize，纯内存</item>
/// <item>OnPlayerChanged：sync 设置——检查 Session 是否已就绪（不 await OpenAsync，消费方应先调用 OpenAsync 再绑定 Player）</item>
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
/// await videoView.DisposePlayerAsync(); // 推荐路径
/// </code>
/// </example>
/// </remarks>
public sealed partial class VideoView : Control, IRenderTarget
{
    private IVideoRenderer? _renderer;
    private IMediaPlayer? _player;
    private SubtitleFrame? _currentSubtitle;
    private IServiceProvider? _services;
    private ILogger? _logger;
    private bool _videoSinkSubscribed;

    // 运行期被判定不健康的渲染器工厂（如 Composition 合成器导入失败）→ 拉黑，重建回退链时跳过，避免无限重试。
    private readonly HashSet<Type> _failedFactories = new();
    private Type? _activeFactoryType;
    private long _presentedFrames; // 诊断节流计数

    // 当前渲染器健康事件的订阅委托实例。必须保存实例本身：IRendererHealth.Unhealthy 是 Action 类型事件，
    // 退订要求与订阅是同一个委托；每次新建 lambda 会让 -= 匹配不上而静默失效，造成重复订阅。
    private Action? _healthHandler;

    #region StyledProperties

    /// <summary>绑定播放器的 StyledProperty。</summary>
    public static readonly StyledProperty<IMediaPlayer?> PlayerProperty =
        AvaloniaProperty.Register<VideoView, IMediaPlayer?>(nameof(Player));

    /// <summary>偏好渲染器类型的 StyledProperty（可选；null=按注册顺序自动回退，Skia 末级兜底）。</summary>
    public static readonly StyledProperty<Type?> RendererTypeProperty =
        AvaloniaProperty.Register<VideoView, Type?>(nameof(RendererType), defaultValue: null);

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

    /// <summary>
    /// 偏好渲染器类型（可选）。设为某个 <see cref="IVideoRenderer"/> 实现类型（如
    /// <c>typeof(D3D11Renderer)</c>）可令对应工厂在回退链中优先尝试；若其在该环境无法合成
    /// （Avalonia 控件内仅提供 RenderHandleType.None，GPU 原生 SwapChain 渲染器需 Pointer/HWND
    /// 而抛 NotSupportedException），VideoView 自动回退到下一个已注册渲染器，最终落到内置
    /// <see cref="SkiaVideoRenderer"/> 软渲染——与后端回退（BackendFallbackMediaPlayerFactory）同构。
    /// 不设置（null）则按注册顺序尝试，Skia 永远作为末级兜底。
    /// </summary>
    public Type? RendererType
    {
        get => GetValue(RendererTypeProperty);
        set => SetValue(RendererTypeProperty, value);
    }

    /// <summary>
    /// 宿主注入的 DI 服务容器。供 VideoView 解析已注册的 <see cref="IVideoRendererFactory"/>
    /// （如 D3D11RendererFactory 等 GPU 渲染器工厂）。未注入时直接兜底到内置
    /// <see cref="SkiaVideoRenderer"/>。VideoView 不引用具体 GPU 项目，仅通过 IVideoRendererFactory
    /// 抽象消费，符合依赖倒置（抽象层消费模式）。
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
    /// 同步方法——委托给 SkiaVideoRenderer.Present（纯内存 + GPU操作）。
    /// </summary>
    /// <param name="frame">要呈现的视频帧（只读借用：所有权归管线，回调返回后由管线 ReturnFrame 释放）。</param>
    public void PresentFrame(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // 诊断节流：帧到达 VideoView（区分「帧未到达」vs「到达但渲染器未就绪」），Trace 级。
        if ((_presentedFrames % 64) == 0)
            _logger?.LogTrace("[VIDEOVIEW] 收到帧 #{Count} {W}x{H} pts={Pts:g} renderer={Renderer}",
                _presentedFrames, frame.Width, frame.Height, frame.Timestamp,
                _renderer?.GetType().Name ?? "null");
        _presentedFrames++;

        // 捕获引用防止 TOCTOU 竞态：_renderer 可能被 OnDetachedFromVisualTree（UI 线程）置 null
        var renderer = _renderer;
        if (renderer == null)
        {
            // 渲染器尚未初始化：帧归还由管线 ReturnFrame 统一负责（只读借用契约）。
            // 此处不得 Dispose——统一 FrameChannel 多播下，后续订阅方会读到已释放帧（use-after-free）。
            _logger?.LogWarning("[VIDEOVIEW] 收到帧但渲染器未就绪，跳过呈现（帧由管线归池）。");
            return;
        }

        renderer.Present(frame);
        // 重绘调度：自带重绘的渲染器（如 Skia GPU 直绘）已在 Present 内经合成器动画时钟
        // 预约下一帧重绘（跟随显示实际刷新率自适应），此处不再逐帧投递，避免两套节拍叠加；
        // 其余渲染器（CPU Skia 软渲染等）仍由逐帧 InvalidateVisual 驱动。
        if (renderer is not IAvaloniaRenderAware { DrivesOwnRenderLoop: true })
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
    /// <b>同步兜底</b>：SkiaVideoRenderer.Dispose 是同步的，无 I/O 可 await。
    /// <b>推荐路径</b>：消费方先 <c>await DisposePlayerAsync()</c> 释放 GPU 资源（GPU flush + SwapChain 释放是真实异步 I/O），
    /// 再让控件 Detach。void 覆写不可改签名为 Task/ValueTask（<b>async void 绝对禁止</b>），
    /// 内调同步清理兜底——解绑播放器事件 + 同步释放 Presenter。
    /// 当前无伪异步——方法体内无 await 故不加 async 关键字。
    /// </remarks>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // 解绑播放器事件
        DetachPlayer();

        // 释放渲染器（Detach 解绑渲染目标 + Dispose 同步释放；Skia 为同步 Dispose，无 I/O 可 await）
        if (_renderer is not null)
        {
            // 必须先退订再拆除：渲染器内部的长生命周期对象可能在拆除后仍触发 Unhealthy。
            UnsubscribeRendererHealth(_renderer);
            _renderer.Detach();
            _renderer.Dispose();
            _renderer = null;
        }
    }

    /// <summary>
    /// 异步释放播放器和渲染器资源。供消费方在控件 Detach 前显式调用。
    /// </summary>
    /// <remarks>
    /// <para><b>推荐路径</b>：原生 GPU 模式下，<c>await _player.DisposeAsync()</c> 释放 GPU 资源
    /// （GPU flush + SwapChain 释放是真实异步 I/O，不能同步阻塞 UI 线程）。</para>
    /// <para><b>ValueTask</b> 与 <c>IMediaPlayer.DisposeAsync()</c> 返回类型一致。</para>
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

        // 4. 释放渲染器（Detach 解绑渲染目标 + Dispose 同步释放；Skia 为同步 Dispose，无 I/O 可 await）
        if (_renderer is not null)
        {
            // 必须先退订再拆除：渲染器内部的长生命周期对象可能在拆除后仍触发 Unhealthy。
            UnsubscribeRendererHealth(_renderer);
            _renderer.Detach();
            _renderer.Dispose();
            _renderer = null;
        }
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext drawingContext)
    {
        base.Render(drawingContext);

        // 仅 Avalonia 合成型渲染器（如 SkiaVideoRenderer）经本回调绘制缓存位图；
        // 原生 SwapChain 渲染器（D3D11 等）不经此路径（其 Attach 在 Avalonia 控件内必然失败并回退）。
        if (_renderer is IAvaloniaRenderAware renderAware)
        {
            renderAware.Render(drawingContext);
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
        else if (change.Property == AspectRatioModeProperty && _renderer is SkiaVideoRenderer skia)
        {
            skia.AspectRatioMode = (AspectRatioMode)change.NewValue!;
        }
    }

    /// <inheritdoc/>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_renderer is IAvaloniaRenderAware renderAware)
        {
            renderAware.Resize((int)e.NewSize.Width, (int)e.NewSize.Height, (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0));
        }
    }

    #region Private Methods

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

        // 若当前为 Skia 软渲染 Presenter，订阅视频帧 sink（管线线程同步投递帧）。
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

        // 退订视频帧 sink（避免管线线程继续向已解绑 Player 投递帧）
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
    /// 视频帧 sink 订阅收敛：所有渲染器均经统一 <see cref="IMediaPlayer.VideoFrameAvailable"/>
    /// 接收帧（有头与无头同饮一条 FrameChannel）。只要已绑定 Player 且渲染器就绪即订阅，
    /// <see cref="PresentFrame"/> 内部调用 <c>_renderer.Present(frame)</c>。
    /// 幂等（由 <c>_videoSinkSubscribed</c> 标记防重复订阅）。
    /// </summary>
    private void UpdateVideoSinkSubscription()
    {
        var shouldSubscribe = _player != null && _renderer != null;
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

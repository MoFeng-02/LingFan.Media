using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

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
    /// V2 原生 GPU 模式新增 async ValueTask DisposePlayerAsync() 供消费方显式调用（**async void 绝对禁止**），
    /// void 覆写内调同步 Dispose() 兜底。</item>
/// <item>Render(DrawingContext)：native——Avalonia 框架 void 签名硬限制</item>
/// <item>OnSizeChanged：sync——通知 Presenter.Resize，纯内存</item>
/// <item>OnPlayerChanged：sync 设置——检查 Session 是否已就绪（V1 不 await OpenAsync，消费方应先调用 OpenAsync 再绑定 Player）</item>
/// </list>
/// <para><b>数据绑定</b>：使用 Avalonia 原生 StyledProperty&lt;T&gt;，不依赖 MVVM 框架。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class VideoView : Control, IRenderTarget
{
    private IVideoPresenter? _presenter;
    private IMediaPlayer? _player;
    private SubtitleFrame? _currentSubtitle;

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
    /// <b>V1 同步实现</b>：SkiaVideoPresenter.Dispose 是同步的，无 I/O 可 await。
    /// <b>V2 原生 GPU 模式</b>：新增 async ValueTask DisposePlayerAsync() 供消费方显式调用，
    /// await renderer.DisposeAsync() 释放 GPU 资源（GPU flush + SwapChain 释放是真实异步 I/O，不能同步阻塞 UI 线程）。
    /// void 覆写不可改签名为 Task/ValueTask（**async void 绝对禁止**），内调同步 Dispose() 兜底。
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

    /// <inheritdoc/>
    public override void Render(DrawingContext drawingContext)
    {
        base.Render(drawingContext);

        // 委托给 Presenter 绘制视频帧
        _presenter?.Render(drawingContext);

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
        _presenter = CreatePresenter(rendererType);
        _presenter.Initialize(this);

        if (_presenter is SkiaVideoPresenter skia)
        {
            skia.AspectRatioMode = AspectRatioMode;
        }
    }

    private static IVideoPresenter CreatePresenter(Type rendererType)
    {
        if (rendererType == typeof(SkiaVideoPresenter))
            return new SkiaVideoPresenter();

        // 未来：D3D11/Vulkan/Metal/OpenGL 的 Presenter 适配器
        throw new NotSupportedException(
            $"渲染器类型 {rendererType.Name} 的 Presenter 尚未实现。V1 仅支持 SkiaVideoPresenter。");
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
    private void AttachPlayer(IMediaPlayer player)
    {
        _player = player;
        player.StateChanged += OnPlayerStateChanged;
        player.SubtitleReceived += OnSubtitleReceived;

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

        _player.StateChanged -= OnPlayerStateChanged;
        _player.SubtitleReceived -= OnSubtitleReceived;
        _player = null;
        _currentSubtitle = null;
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 播放控制条控件。封装常用播放控件（播放/暂停、进度条、音量、时间、全屏）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：config/sync 分类——UI 绑定/命令路由，纯内存操作。
/// 播放控制方法返回 <see cref="Task"/>（<c>TogglePlayPauseAsync</c>/<c>SeekToAsync</c>），
/// 供消费方 <c>await</c>。void 事件处理器内 fire-and-forget 调用（<c>_ = SomeAsync()</c>），
/// **async void 绝对禁止**。</para>
/// <para><b>数据绑定</b>：使用 Avalonia 原生 StyledProperty，不依赖 MVVM 框架。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class MediaControl : TemplatedControl
{
    private IMediaPlayer? _player;
    private bool _suppressPositionUpdate;
    private Button? _playButton;
    private Button? _fullscreenButton;
    private ProgressBar? _progressBar;

    #region Events

    /// <summary>全屏按钮点击时触发。由消费方处理实际的全屏切换逻辑（窗口级操作）。</summary>
    public event EventHandler? FullscreenRequested;

    #endregion

    #region StyledProperties

    /// <summary>绑定播放器的 StyledProperty。</summary>
    public static readonly StyledProperty<IMediaPlayer?> PlayerProperty =
        AvaloniaProperty.Register<MediaControl, IMediaPlayer?>(nameof(Player));

    /// <summary>是否显示进度条。</summary>
    public static readonly StyledProperty<bool> ShowProgressBarProperty =
        AvaloniaProperty.Register<MediaControl, bool>(nameof(ShowProgressBar), defaultValue: true);

    /// <summary>是否显示音量控制。</summary>
    public static readonly StyledProperty<bool> ShowVolumeProperty =
        AvaloniaProperty.Register<MediaControl, bool>(nameof(ShowVolume), defaultValue: true);

    /// <summary>是否显示时间。</summary>
    public static readonly StyledProperty<bool> ShowTimeProperty =
        AvaloniaProperty.Register<MediaControl, bool>(nameof(ShowTime), defaultValue: true);

    /// <summary>是否显示全屏按钮。</summary>
    public static readonly StyledProperty<bool> ShowFullscreenProperty =
        AvaloniaProperty.Register<MediaControl, bool>(nameof(ShowFullscreen), defaultValue: true);

    /// <summary>是否允许拖拽进度条 Seek 的 StyledProperty。</summary>
    public static readonly StyledProperty<bool> IsSeekableProperty =
        AvaloniaProperty.Register<MediaControl, bool>(nameof(IsSeekable), defaultValue: true);

    /// <summary>是否自动隐藏。</summary>
    public static readonly StyledProperty<bool> AutoHideProperty =
        AvaloniaProperty.Register<MediaControl, bool>(nameof(AutoHide), defaultValue: false);

    /// <summary>自动隐藏延迟。</summary>
    public static readonly StyledProperty<TimeSpan> AutoHideDelayProperty =
        AvaloniaProperty.Register<MediaControl, TimeSpan>(nameof(AutoHideDelay), defaultValue: TimeSpan.FromSeconds(3));

    /// <summary>播放进度 (0.0~1.0) 的 StyledProperty。</summary>
    public static readonly StyledProperty<float> ProgressProperty =
        AvaloniaProperty.Register<MediaControl, float>(nameof(Progress));

    /// <summary>当前位置的 StyledProperty。</summary>
    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<MediaControl, TimeSpan>(nameof(Position));

    /// <summary>总时长的 StyledProperty。</summary>
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<MediaControl, TimeSpan>(nameof(Duration));

    /// <summary>音量 (0.0~1.0) 的 StyledProperty。</summary>
    public static readonly StyledProperty<float> VolumeProperty =
        AvaloniaProperty.Register<MediaControl, float>(nameof(Volume), defaultValue: 1.0f);

    /// <summary>是否静音的 StyledProperty。</summary>
    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<MediaControl, bool>(nameof(IsMuted));

    #endregion

    #region StyledProperty Accessors

    /// <summary>绑定播放器。</summary>
    public IMediaPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <summary>是否显示进度条。</summary>
    public bool ShowProgressBar
    {
        get => GetValue(ShowProgressBarProperty);
        set => SetValue(ShowProgressBarProperty, value);
    }

    /// <summary>是否显示音量控制。</summary>
    public bool ShowVolume
    {
        get => GetValue(ShowVolumeProperty);
        set => SetValue(ShowVolumeProperty, value);
    }

    /// <summary>是否显示时间。</summary>
    public bool ShowTime
    {
        get => GetValue(ShowTimeProperty);
        set => SetValue(ShowTimeProperty, value);
    }

    /// <summary>是否显示全屏按钮。</summary>
    public bool ShowFullscreen
    {
        get => GetValue(ShowFullscreenProperty);
        set => SetValue(ShowFullscreenProperty, value);
    }

    /// <summary>是否允许拖拽进度条 Seek。</summary>
    public bool IsSeekable
    {
        get => GetValue(IsSeekableProperty);
        set => SetValue(IsSeekableProperty, value);
    }

    /// <summary>是否自动隐藏。</summary>
    public bool AutoHide
    {
        get => GetValue(AutoHideProperty);
        set => SetValue(AutoHideProperty, value);
    }

    /// <summary>自动隐藏延迟。</summary>
    public TimeSpan AutoHideDelay
    {
        get => GetValue(AutoHideDelayProperty);
        set => SetValue(AutoHideDelayProperty, value);
    }

    /// <summary>播放进度 (0.0~1.0)。</summary>
    public float Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>当前位置。</summary>
    public TimeSpan Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>总时长。</summary>
    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <summary>音量 (0.0~1.0)。</summary>
    public float Volume
    {
        get => GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>是否静音。</summary>
    public bool IsMuted
    {
        get => GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    #endregion

    /// <inheritdoc/>
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // 解绑旧控件（模板可能被重新应用）
        if (_playButton != null)
            _playButton.Click -= OnPlayButtonClick;
        if (_fullscreenButton != null)
            _fullscreenButton.Click -= OnFullscreenButtonClick;
        if (_progressBar != null)
            _progressBar.Seek -= OnProgressBarSeek;

        // 查找模板中的控件并绑定事件
        _playButton = e.NameScope.Find<Button>("PART_PlayButton");
        _fullscreenButton = e.NameScope.Find<Button>("PART_FullscreenButton");
        _progressBar = e.NameScope.Find<ProgressBar>("PART_ProgressBar");

        if (_playButton != null)
            _playButton.Click += OnPlayButtonClick;
        if (_fullscreenButton != null)
            _fullscreenButton.Click += OnFullscreenButtonClick;
        if (_progressBar != null)
            _progressBar.Seek += OnProgressBarSeek;

        // 同步当前播放状态到按钮内容
        UpdatePlayButtonContent();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 重新挂载到视觉树后，如果 Player 已绑定但事件未订阅，重新订阅
        if (Player != null && _player == null)
        {
            _player = Player;
            _player.StateChanged += OnStateChanged;
            _player.PositionChanged += OnPositionChanged;
            UpdatePlayButtonContent();
        }
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // 从视觉树分离时取消订阅事件，防止内存泄漏（Player 可能是长生命周期对象）
        if (_player != null)
        {
            _player.StateChanged -= OnStateChanged;
            _player.PositionChanged -= OnPositionChanged;
            _player = null;
        }
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PlayerProperty)
        {
            OnPlayerChanged(change.OldValue as IMediaPlayer, change.NewValue as IMediaPlayer);
        }
        else if (change.Property == VolumeProperty && _player != null)
        {
            _player.Volume = Volume;
        }
        else if (change.Property == IsMutedProperty && _player != null)
        {
            _player.IsMuted = IsMuted;
        }
    }

    private void OnPlayerChanged(IMediaPlayer? oldPlayer, IMediaPlayer? newPlayer)
    {
        if (oldPlayer != null)
        {
            oldPlayer.StateChanged -= OnStateChanged;
            oldPlayer.PositionChanged -= OnPositionChanged;
        }

        _player = newPlayer;

        if (newPlayer != null)
        {
            newPlayer.StateChanged += OnStateChanged;
            newPlayer.PositionChanged += OnPositionChanged;

            // 同步初始值
            Position = newPlayer.Position;
            Duration = newPlayer.Duration;
            Volume = newPlayer.Volume;
            IsMuted = newPlayer.IsMuted;
            UpdatePlayButtonContent();
        }
    }

    private void OnStateChanged(object? sender, MediaStateChangedEventArgs e)
    {
        // UI 线程更新（事件可能从非 UI 线程触发）
        Dispatcher.UIThread.Post(() =>
        {
            if (_player != null)
            {
                Duration = _player.Duration;
                Position = _player.Position;
                UpdatePlayButtonContent();
            }
        });
    }

    private void OnPositionChanged(object? sender, TimeSpan e)
    {
        // 位置变化时更新进度条和时间显示
        Dispatcher.UIThread.Post(() =>
        {
            if (_suppressPositionUpdate)
                return;

            Position = e;

            if (_player != null && _player.Duration > TimeSpan.Zero)
            {
                Progress = (float)(e.TotalSeconds / _player.Duration.TotalSeconds);
            }
        });
    }

    /// <summary>
    /// 播放/暂停切换。返回 <see cref="Task"/> 供消费方 await。
    /// PlayAsync/PauseAsync 是接口契约（返回 Task.CompletedTask），此处 await 是真异步契约。
    /// </summary>
    public async Task TogglePlayPauseAsync()
    {
        if (_player == null)
            return;

        if (_player.State == MediaState.Playing)
        {
            await _player.PauseAsync();
        }
        else
        {
            await _player.PlayAsync();
        }
    }

    /// <summary>
    /// 定位到指定位置。返回 <see cref="Task"/> 供消费方 await。
    /// SeekAsync 包含真实 I/O（demuxer.SeekAsync）。
    /// </summary>
    /// <param name="position">目标位置。</param>
    public async Task SeekToAsync(TimeSpan position)
    {
        if (_player == null)
            return;

        _suppressPositionUpdate = true;
        try
        {
            await _player.SeekAsync(position);
        }
        finally
        {
            _suppressPositionUpdate = false;
        }
    }

    /// <summary>
    /// 设置音量。设置 Volume StyledProperty，由 OnPropertyChanged 传播到 Player。
    /// </summary>
    /// <param name="volume">音量 (0.0~1.0)。</param>
    public void SetVolume(float volume)
    {
        Volume = Math.Clamp(volume, 0f, 1f);
    }

    /// <summary>
    /// 切换静音。设置 IsMuted StyledProperty，由 OnPropertyChanged 传播到 Player。
    /// </summary>
    public void ToggleMute()
    {
        IsMuted = !IsMuted;
    }

    /// <summary>
    /// 播放按钮点击事件处理器。void 事件处理器（EventHandler 委托签名要求），
    /// 内部 fire-and-forget 调用 async Task 方法（**async void 绝对禁止**）。
    /// </summary>
    private void OnPlayButtonClick(object? sender, RoutedEventArgs e)
        => _ = TogglePlayPauseAsync();

    /// <summary>
    /// 进度条拖拽 Seek 事件处理器。void 事件处理器（EventHandler&lt;SeekEventArgs&gt; 委托签名要求），
    /// 内部 fire-and-forget 调用 async Task 方法（**async void 绝对禁止**）。
    /// </summary>
    private void OnProgressBarSeek(object? sender, SeekEventArgs e)
        => _ = SeekToAsync(e.Position);

    /// <summary>
    /// 全屏按钮点击事件处理器。触发 FullscreenRequested 事件供消费方处理。
    /// </summary>
    private void OnFullscreenButtonClick(object? sender, RoutedEventArgs e)
        => FullscreenRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 根据当前播放器状态更新播放按钮内容（▶/⏸）。
    /// </summary>
    private void UpdatePlayButtonContent()
    {
        if (_playButton == null)
            return;

        _playButton.Content = _player != null && _player.State == MediaState.Playing
            ? "⏸"
            : "▶";
    }
}

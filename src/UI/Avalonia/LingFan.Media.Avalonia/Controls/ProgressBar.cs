using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 缓冲进度条控件。显示播放进度和缓冲进度两条，支持拖拽 Seek。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：config/native 分类——纯属性数据 + UI 线程指针事件，无 I/O。
/// Seek 事件由 <see cref="MediaControl"/> 接收后调用 <c>await IMediaPlayer.SeekAsync()</c>，
/// ProgressBar 自身不做异步操作。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class ProgressBar : TemplatedControl
{
    private bool _isDragging;

    /// <summary>播放进度 (0.0~1.0) 的 StyledProperty。</summary>
    public static readonly StyledProperty<float> ProgressProperty =
        AvaloniaProperty.Register<ProgressBar, float>(nameof(Progress));

    /// <summary>缓冲进度 (0.0~1.0) 的 StyledProperty。</summary>
    public static readonly StyledProperty<float> BufferedProgressProperty =
        AvaloniaProperty.Register<ProgressBar, float>(nameof(BufferedProgress));

    /// <summary>是否允许拖拽 Seek 的 StyledProperty。</summary>
    public static readonly StyledProperty<bool> IsSeekableProperty =
        AvaloniaProperty.Register<ProgressBar, bool>(nameof(IsSeekable));

    /// <summary>总时长的 StyledProperty（用于计算拖拽对应的时间位置）。</summary>
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<ProgressBar, TimeSpan>(nameof(Duration));

    /// <summary>拖拽 Seek 完成时触发。消费方在此事件中调用 <c>await player.SeekAsync(position)</c>。</summary>
    public event EventHandler<SeekEventArgs>? Seek;

    /// <summary>播放进度 (0.0~1.0)。</summary>
    public float Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>缓冲进度 (0.0~1.0)。</summary>
    public float BufferedProgress
    {
        get => GetValue(BufferedProgressProperty);
        set => SetValue(BufferedProgressProperty, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>是否允许拖拽 Seek。</summary>
    public bool IsSeekable
    {
        get => GetValue(IsSeekableProperty);
        set => SetValue(IsSeekableProperty, value);
    }

    /// <summary>总时长（用于计算拖拽对应的时间位置）。</summary>
    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!IsSeekable)
            return;

        // 捕获指针，确保拖拽过程中即使移出控件边界也能收到 PointerMoved
        e.Pointer.Capture(this);
        _isDragging = true;

        UpdateProgressFromPointer(e);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_isDragging)
            return;

        UpdateProgressFromPointer(e);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_isDragging)
            return;

        _isDragging = false;
        e.Pointer.Capture(null);

        // 最终位置再精确更新一次
        UpdateProgressFromPointer(e);

        // 触发 Seek 事件，传递最终进度值和时间位置
        var progress = (double)Progress;
        var position = CalculatePosition(progress);
        Seek?.Invoke(this, new SeekEventArgs(progress, position));

        e.Handled = true;
    }

    /// <summary>
    /// 根据指针位置更新 Progress 属性。
    /// </summary>
    private void UpdateProgressFromPointer(PointerEventArgs e)
    {
        var width = Bounds.Width;
        if (width <= 0)
            return;

        var x = e.GetPosition(this).X;
        var ratio = Math.Clamp(x / width, 0.0, 1.0);
        Progress = (float)ratio;
    }

    /// <summary>
    /// 根据进度值计算时间位置。
    /// </summary>
    private TimeSpan CalculatePosition(double progress)
    {
        var duration = Duration;
        if (duration <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return TimeSpan.FromSeconds(progress * duration.TotalSeconds);
    }
}

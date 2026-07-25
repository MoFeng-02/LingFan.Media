using Avalonia;
using Avalonia.Controls.Primitives;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 时间显示控件。显示 "mm:ss / mm:ss" 格式。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：全部 config 分类——纯属性数据，无 I/O。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class TimeDisplay : TemplatedControl
{
    /// <summary>当前位置的 StyledProperty。</summary>
    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<TimeDisplay, TimeSpan>(nameof(Position));

    /// <summary>总时长的 StyledProperty。</summary>
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<TimeDisplay, TimeSpan>(nameof(Duration));

    /// <summary>格式化显示文本的 DirectProperty。</summary>
    public static readonly DirectProperty<TimeDisplay, string> DisplayTextProperty =
        AvaloniaProperty.RegisterDirect<TimeDisplay, string>(
            nameof(DisplayText),
            o => o.DisplayText);

    private string _displayText = "00:00 / 00:00";

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

    /// <summary>格式化显示文本（如 "01:23 / 05:30"）。</summary>
    public string DisplayText => _displayText;

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return ts.ToString(@"hh\:mm\:ss");
        return ts.ToString(@"mm\:ss");
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PositionProperty || change.Property == DurationProperty)
        {
            var newText = $"{FormatTimeSpan(Position)} / {FormatTimeSpan(Duration)}";
            SetAndRaise(DisplayTextProperty, ref _displayText, newText);
        }
    }
}

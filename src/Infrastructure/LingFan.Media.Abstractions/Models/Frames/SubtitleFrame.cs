namespace LingFan.Media.Abstractions;

/// <summary>
/// 字幕帧。不实现 <see cref="IDisposableFrame"/>（仅 string + TimeSpan，无原生资源）。
/// </summary>
public sealed class SubtitleFrame
{
    /// <summary>字幕文本内容。</summary>
    public string Text { get; init; }

    /// <summary>显示开始时间。</summary>
    public TimeSpan Start { get; init; }

    /// <summary>显示结束时间。</summary>
    public TimeSpan End { get; init; }

    /// <summary>字幕样式（可能为 null，使用默认）。</summary>
    public SubtitleStyle? Style { get; init; }

    /// <summary>
    /// 初始化 <see cref="SubtitleFrame"/> 的新实例。
    /// </summary>
    public SubtitleFrame(string text, TimeSpan start, TimeSpan end, SubtitleStyle? style = null)
    {
        Text = text;
        Start = start;
        End = end;
        Style = style;
    }
}

namespace LingFan.Media.Abstractions;

/// <summary>
/// 字幕样式配置。
/// </summary>
public sealed class SubtitleStyle
{
    /// <summary>字体族（可能为 null，使用默认）。</summary>
    public string? FontFamily { get; init; }

    /// <summary>字号（可能为 null，使用默认）。</summary>
    public float? FontSize { get; init; }

    /// <summary>前景色（如 "#FFFFFFFF"）。</summary>
    public string? Color { get; init; }

    /// <summary>背景色（可能为 null）。</summary>
    public string? BackgroundColor { get; init; }

    /// <summary>字幕位置。</summary>
    public SubtitlePosition Position { get; init; } = SubtitlePosition.Bottom;

    /// <summary>字幕对齐方式。</summary>
    public SubtitleAlignment Alignment { get; init; } = SubtitleAlignment.Center;
}

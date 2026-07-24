namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频解码与渲染设置。
/// </summary>
public sealed class VideoSettings
{
    /// <summary>是否启用硬件加速解码。</summary>
    public bool EnableHardwareAcceleration { get; init; } = true;

    /// <summary>首选编解码器（null 表示自动选择）。</summary>
    public VideoCodec? PreferredCodec { get; init; }

    /// <summary>最大解码宽度（null 表示不限制）。</summary>
    public int? MaxWidth { get; init; }

    /// <summary>最大解码高度（null 表示不限制）。</summary>
    public int? MaxHeight { get; init; }

    /// <summary>输出像素格式（null 表示使用源格式）。</summary>
    public PixelFormat? OutputPixelFormat { get; init; }
}

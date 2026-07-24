namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频轨道详情信息。
/// </summary>
public sealed class VideoTrackInfo
{
    /// <summary>视频宽度（像素）。</summary>
    public int Width { get; init; }

    /// <summary>视频高度（像素）。</summary>
    public int Height { get; init; }

    /// <summary>帧率（FPS）。</summary>
    public float FrameRate { get; init; }

    /// <summary>像素格式。</summary>
    public PixelFormat PixelFormat { get; init; }

    /// <summary>色彩空间（可能为 null）。</summary>
    public string? ColorSpace { get; init; }

    /// <summary>采样宽高比（可能为 null）。</summary>
    public Rational? Sar { get; init; }

    /// <summary>轨道时长。</summary>
    public TimeSpan Duration { get; init; }
}

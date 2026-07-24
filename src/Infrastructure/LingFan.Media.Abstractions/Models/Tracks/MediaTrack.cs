namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体轨道信息。
/// </summary>
/// <remarks>
/// Codec 字段拆为 <see cref="VideoCodec"/> / <see cref="AudioCodec"/> / <see cref="SubtitleCodec"/>
/// 三个独立可空属性，不用泛型。
/// </remarks>
public sealed class MediaTrack
{
    /// <summary>轨道索引（在容器中的流索引）。</summary>
    public int Index { get; init; }

    /// <summary>轨道类型。</summary>
    public TrackType Type { get; init; }

    /// <summary>视频编解码器（仅 Type=Video 时有效，否则 null）。</summary>
    public VideoCodec? VideoCodec { get; init; }

    /// <summary>音频编解码器（仅 Type=Audio 时有效，否则 null）。</summary>
    public AudioCodec? AudioCodec { get; init; }

    /// <summary>字幕编解码器（仅 Type=Subtitle 时有效，否则 null）。</summary>
    public SubtitleCodec? SubtitleCodec { get; init; }

    /// <summary>语言标签（如 "en"、"zh"，可能为 null）。</summary>
    public string? Language { get; init; }

    /// <summary>轨道标题（可能为 null）。</summary>
    public string? Title { get; init; }

    /// <summary>是否默认轨道。</summary>
    public bool IsDefault { get; init; }

    /// <summary>比特率（bps）。</summary>
    public long BitRate { get; init; }

    /// <summary>视频详情（仅视频轨道有效，否则 null）。</summary>
    public VideoTrackInfo? VideoInfo { get; init; }

    /// <summary>音频详情（仅音频轨道有效，否则 null）。</summary>
    public AudioTrackInfo? AudioInfo { get; init; }
}

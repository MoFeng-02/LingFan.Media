namespace LingFan.Media.Abstractions;

/// <summary>
/// 字幕编解码器类型。
/// </summary>
public enum SubtitleCodec : int
{
    /// <summary>SubRip（.srt）。</summary>
    SRT,
    /// <summary>Advanced SubStation Alpha（.ass）。</summary>
    ASS,
    /// <summary>PGS 图形字幕（位图，V1 不实现）。</summary>
    PGS,
    /// <summary>DVD VobSub 图形字幕（位图，V1 不实现）。</summary>
    VobSub,
    /// <summary>WebVTT（.vtt）。</summary>
    WebVTT,
    /// <summary>未知字幕编解码器。</summary>
    Unknown
}

namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频编解码器类型。
/// </summary>
public enum VideoCodec : int
{
    /// <summary>H.264 / AVC。</summary>
    H264,
    /// <summary>H.265 / HEVC。</summary>
    H265,
    /// <summary>AV1。</summary>
    AV1,
    /// <summary>VP9。</summary>
    VP9,
    /// <summary>MPEG-2。</summary>
    MPEG2,
    /// <summary>MPEG-4。</summary>
    MPEG4,
    /// <summary>未知编解码器。</summary>
    Unknown
}

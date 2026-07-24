namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频编解码器类型。
/// </summary>
public enum AudioCodec : int
{
    /// <summary>AAC。</summary>
    AAC,
    /// <summary>MP3。</summary>
    MP3,
    /// <summary>Opus。</summary>
    Opus,
    /// <summary>FLAC。</summary>
    FLAC,
    /// <summary>Vorbis。</summary>
    Vorbis,
    /// <summary>PCM（无压缩原始音频）。</summary>
    PCM,
    /// <summary>AC-3 / Dolby Digital。</summary>
    AC3,
    /// <summary>未知编解码器。</summary>
    Unknown
}

namespace LingFan.Media.Backends.VLCNative;

/// <summary>
/// VLC 编解码器 / FourCC 映射工具（纯函数，零 LibVLCSharp 依赖）。
/// </summary>
/// <remarks>
/// VLCNative 自写 P/Invoke 后端专用（替代已退役的 LibVLCSharp 旧后端）。
/// 仅依赖 <see cref="LingFan.Media.Abstractions"/> 中的 Codec 枚举，AOT / trim 完全安全。
/// </remarks>
public static class VlcCodecMapping
{
    /// <summary>将 4 字符字符串编码为 FourCC (uint)，小端。</summary>
    public static uint FourCC(string s)
        => ((uint)s[0]) | ((uint)s[1] << 8) | ((uint)s[2] << 16) | ((uint)s[3] << 24);

    /// <summary>将 VLC 的 FourCC (uint) 解码为 4 字符字符串。</summary>
    public static string FourCCToString(uint fourcc)
        => new string(new char[]
        {
            (char)(fourcc & 0xFF),
            (char)((fourcc >> 8) & 0xFF),
            (char)((fourcc >> 16) & 0xFF),
            (char)((fourcc >> 24) & 0xFF)
        });

    /// <summary>VLC 视频 codec 字符串 → <see cref="VideoCodec"/>。</summary>
    public static VideoCodec MapVideoCodec(string? codec) => codec?.ToUpperInvariant() switch
    {
        "H264" or "AVC" or "AVC1" => VideoCodec.H264,
        "H265" or "HEVC" or "HVC1" => VideoCodec.H265,
        "AV01" or "AV1" => VideoCodec.AV1,
        "VP09" or "VP9" => VideoCodec.VP9,
        "MP2V" or "MPEG2" => VideoCodec.MPEG2,
        "MP4V" or "MPEG4" => VideoCodec.MPEG4,
        _ => VideoCodec.Unknown
    };

    /// <summary>VLC 音频 codec 字符串 → <see cref="AudioCodec"/>。</summary>
    public static AudioCodec MapAudioCodec(string? codec) => codec?.ToUpperInvariant() switch
    {
        "MP4A" or "AAC" => AudioCodec.AAC,
        "MP3 " or "MP3" or "MPEG" => AudioCodec.MP3,
        "OPUS" or "OPU" => AudioCodec.Opus,
        "FLAC" or "FLA" => AudioCodec.FLAC,
        "VORB" or "VORBIS" => AudioCodec.Vorbis,
        "S16N" or "S16L" or "PCM " or "PCM" => AudioCodec.PCM,
        "AC3 " or "AC3" or "A52" => AudioCodec.AC3,
        _ => AudioCodec.Unknown
    };

    /// <summary>VLC 字幕 codec 字符串 → <see cref="SubtitleCodec"/>。</summary>
    public static SubtitleCodec MapSubtitleCodec(string? codec) => codec?.ToUpperInvariant() switch
    {
        "SUBT" or "SRT" or "SUBRIP" => SubtitleCodec.SRT,
        "SSA " or "ASS " or "SSA" or "ASS" => SubtitleCodec.ASS,
        "WEBVTT" or "VTT" => SubtitleCodec.WebVTT,
        "PGS " or "PGS" or "HDMV" => SubtitleCodec.PGS,
        "VOBSUB" or "SPU " or "DVD" => SubtitleCodec.VobSub,
        _ => SubtitleCodec.Unknown
    };
}

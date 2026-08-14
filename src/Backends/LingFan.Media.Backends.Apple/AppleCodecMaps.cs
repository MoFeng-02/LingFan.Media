using System.Runtime.Versioning;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Backends.Apple;

/// <summary>
/// Apple 后端专用编解码器 / 容器映射（AVFoundation / VideoToolbox FourCharCode ↔ 契约枚举）。
/// </summary>
/// <remarks>
/// <para>仅做静态映射，无原生依赖；与 <see cref="Backends.MediaCodec.AndroidCodecMaps"/> 对称。</para>
/// <para>FourCharCode 取值来自 CoreMedia <c>CMVideoCodecType</c> / <c>kCMAudioFormatType_*</c>，
/// 跨 Apple 平台恒定（小端读取的 uint）。</para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2050",
    Justification = "无 [ComImport]，使用原始 [LibraryImport] P/Invoke，不会被裁剪器移除。仅 Apple 运行时使用。")]
internal static class AppleCodecMaps
{
    // ── 视频 FourCharCode ──
    private const uint kCMVideoCodecType_H264 = 0x61766331u; // 'avc1'
    private const uint kCMVideoCodecType_H264Alt = 0x61766333u; // 'avc3'
    private const uint kCMVideoCodecType_HEVC = 0x68766331u; // 'hvc1'
    private const uint kCMVideoCodecType_HEVCAlt = 0x68766333u; // 'hvc3'
    private const uint kCMVideoCodecType_AV1 = 0x61763031u; // 'av01'
    private const uint kCMVideoCodecType_VP9 = 0x76703039u; // 'vp09'
    private const uint kCMVideoCodecType_MPEG4Video = 0x6D703476u; // 'mp4v'
    private const uint kCMVideoCodecType_MPEG4VideoAlt = 0x6D347620u; // 'm4v '

    /// <summary>FourCharCode → <see cref="VideoCodec"/>（未知返回 <see cref="VideoCodec.Unknown"/>）。</summary>
    public static VideoCodec FourCharToVideoCodec(uint fourChar)
        => fourChar switch
        {
            kCMVideoCodecType_H264 or kCMVideoCodecType_H264Alt => VideoCodec.H264,
            kCMVideoCodecType_HEVC or kCMVideoCodecType_HEVCAlt => VideoCodec.H265,
            kCMVideoCodecType_AV1 => VideoCodec.AV1,
            kCMVideoCodecType_VP9 => VideoCodec.VP9,
            kCMVideoCodecType_MPEG4Video or kCMVideoCodecType_MPEG4VideoAlt => VideoCodec.MPEG4,
            _ => VideoCodec.Unknown,
        };

    // ── 音频 FourCharCode ──
    private const uint kCMAudioFormatType_AAC = 0x61616320u; // 'aac '
    private const uint kCMAudioFormatType_MP3 = 0x2E6D7033u; // '.mp3'
    private const uint kCMAudioFormatType_OPUS = 0x6F707573u; // 'opus'
    private const uint kCMAudioFormatType_FLAC = 0x666C6163u; // 'flac'
    private const uint kCMAudioFormatType_AC3 = 0x61632D33u; // 'ac-3'

    /// <summary>FourCharCode → <see cref="AudioCodec"/>（未知返回 <see cref="AudioCodec.Unknown"/>）。</summary>
    public static AudioCodec FourCharToAudioCodec(uint fourChar)
        => fourChar switch
        {
            kCMAudioFormatType_AAC => AudioCodec.AAC,
            kCMAudioFormatType_MP3 => AudioCodec.MP3,
            kCMAudioFormatType_OPUS => AudioCodec.Opus,
            kCMAudioFormatType_FLAC => AudioCodec.FLAC,
            kCMAudioFormatType_AC3 => AudioCodec.AC3,
            _ => AudioCodec.Unknown,
        };

    /// <summary>由文件/URL 地址推断容器格式（扩展名映射；无法识别返回 <see cref="ContainerFormat.Unknown"/>）。</summary>
    public static ContainerFormat ContainerFromLocation(string? location)
    {
        if (string.IsNullOrEmpty(location)) return ContainerFormat.Unknown;
        string ext = Path.GetExtension(location!).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".m4v" or ".mov" => ContainerFormat.MP4,
            ".mkv" => ContainerFormat.MKV,
            ".avi" => ContainerFormat.AVI,
            ".ts" or ".m2ts" => ContainerFormat.TS,
            ".webm" => ContainerFormat.WebM,
            ".flv" => ContainerFormat.FLV,
            _ => ContainerFormat.Unknown,
        };
    }
}

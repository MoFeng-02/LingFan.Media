namespace LingFan.Media.Backends.MediaCodec;

/// <summary>
/// Android MediaCodec / MediaExtractor 的 MIME 字符串、编解码器枚举的互映射（可移植，零外部引用）。
/// </summary>
/// <remarks>
/// <para>MIME 字符串严格取自 AOSP <c>MediaDefs</c>（<c>frameworks/base/media/java/android/media/MediaFormat.java</c>
/// 的 <c>MIMETYPE_*</c> 常量），不可臆改：</para>
/// <list type="bullet">
/// <item>video/avc、video/hevc、video/av01（AV1 基础类型，profile 串如 video/av01.0.00M.08 以此前缀匹配）、
/// video/x-vnd.on2.vp9（VP9 标准常量，非 video/vp9）、video/mpeg2、video/mp4v-es。</item>
/// <item>audio/mp4a-latm（AAC）、audio/mpeg（MP3）、audio/opus、audio/flac、audio/vorbis、
/// audio/raw（PCM）、audio/ac3。</item>
/// </list>
/// <para>本文件为可移植部分（net10.0 与 net10.0-android 共有）；依赖 <c>Android.Media.MediaCodecCapabilities</c>/
/// <c>Encoding</c> 的颜色格式与采样格式映射在 <c>AndroidCodecMaps.Android.cs</c>（仅 Android 目标编译）。
/// 均为纯值映射，零外部引用，AOT 安全（无反射、无字典反射）。</para>
/// </remarks>
internal static partial class AndroidCodecMaps
{
    // ── MIME → 枚举 ──

    public static VideoCodec MimeToVideoCodec(string mime)
    {
        if (mime.StartsWith("video/avc", StringComparison.Ordinal)) return VideoCodec.H264;
        if (mime.StartsWith("video/hevc", StringComparison.Ordinal)) return VideoCodec.H265;
        if (mime.StartsWith("video/av01", StringComparison.Ordinal)) return VideoCodec.AV1;
        if (mime.StartsWith("video/x-vnd.on2.vp9", StringComparison.Ordinal)
            || mime.StartsWith("video/vp9", StringComparison.Ordinal)) return VideoCodec.VP9;
        if (mime.StartsWith("video/mpeg2", StringComparison.Ordinal)) return VideoCodec.MPEG2;
        if (mime.StartsWith("video/mp4v-es", StringComparison.Ordinal)) return VideoCodec.MPEG4;
        return mime.StartsWith("video/", StringComparison.Ordinal) ? VideoCodec.Unknown : VideoCodec.Unknown;
    }

    public static AudioCodec MimeToAudioCodec(string mime)
    {
        return mime switch
        {
            _ when mime.StartsWith("audio/mp4a-latm", StringComparison.Ordinal) => AudioCodec.AAC,
            _ when mime.StartsWith("audio/mpeg", StringComparison.Ordinal) => AudioCodec.MP3,
            _ when mime.StartsWith("audio/opus", StringComparison.Ordinal) => AudioCodec.Opus,
            _ when mime.StartsWith("audio/flac", StringComparison.Ordinal) => AudioCodec.FLAC,
            _ when mime.StartsWith("audio/vorbis", StringComparison.Ordinal) => AudioCodec.Vorbis,
            _ when mime.StartsWith("audio/raw", StringComparison.Ordinal) => AudioCodec.PCM,
            _ when mime.StartsWith("audio/ac3", StringComparison.Ordinal) => AudioCodec.AC3,
            _ => AudioCodec.Unknown
        };
    }

    public static TrackType MimeToTrackType(string mime)
    {
        if (mime.StartsWith("video/", StringComparison.Ordinal)) return TrackType.Video;
        if (mime.StartsWith("audio/", StringComparison.Ordinal)) return TrackType.Audio;
        if (mime.StartsWith("text/", StringComparison.Ordinal)) return TrackType.Subtitle;
        return TrackType.Subtitle; // 未知按字幕（不安全，但至少可被列举）
    }

    // ── 枚举 → MIME ──

    public static string? VideoCodecToMime(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "video/avc",
        VideoCodec.H265 => "video/hevc",
        VideoCodec.AV1 => "video/av01",
        VideoCodec.VP9 => "video/x-vnd.on2.vp9",
        VideoCodec.MPEG2 => "video/mpeg2",
        VideoCodec.MPEG4 => "video/mp4v-es",
        _ => null
    };

    public static string? AudioCodecToMime(AudioCodec codec) => codec switch
    {
        AudioCodec.AAC => "audio/mp4a-latm",
        AudioCodec.MP3 => "audio/mpeg",
        AudioCodec.Opus => "audio/opus",
        AudioCodec.FLAC => "audio/flac",
        AudioCodec.Vorbis => "audio/vorbis",
        AudioCodec.PCM => "audio/raw",
        AudioCodec.AC3 => "audio/ac3",
        _ => null
    };

    // ── 容器格式（从 file format mime 推断）──

    public static ContainerFormat MimeToContainerFormat(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return ContainerFormat.Unknown;
        if (mime.Contains("mp4") || mime.Contains("mpeg4") || mime.Contains("quicktime")) return ContainerFormat.MP4;
        if (mime.Contains("matroska") || mime.Contains("webm")) return ContainerFormat.MKV;
        if (mime.Contains("mpegts") || mime.Contains("mp2t")) return ContainerFormat.TS;
        if (mime.Contains("avi")) return ContainerFormat.AVI;
        if (mime.Contains("flv")) return ContainerFormat.FLV;
        if (mime.Contains("webm")) return ContainerFormat.WebM;
        return ContainerFormat.Unknown;
    }

    // ── 色彩空间 NDK 值 → LingFan 枚举（YUV→RGB 矩阵选择）──

    /// <summary>将 AOSP key-color-standard 的 int 值映射到 <see cref="ColorStandard"/>。</summary>
    public static ColorStandard ColorStandardFromNdk(int value) => value switch
    {
        1 => ColorStandard.Bt709,      // COLOR_STANDARD_BT709
        2 => ColorStandard.Bt601,      // COLOR_STANDARD_BT601_PAL
        4 => ColorStandard.Bt601,      // COLOR_STANDARD_BT601_NTSC
        5 => ColorStandard.Bt2020,     // COLOR_STANDARD_BT2020
        _ => ColorStandard.Unspecified,
    };

    /// <summary>将 AOSP key-color-range 的 int 值映射到 <see cref="ColorRange"/>。</summary>
    public static ColorRange ColorRangeFromNdk(int value) => value switch
    {
        1 => ColorRange.Full,          // COLOR_RANGE_FULL
        2 => ColorRange.Limited,       // COLOR_RANGE_LIMITED
        _ => ColorRange.Unspecified,
    };

    /// <summary>将 AOSP key-color-transfer 的 int 值映射到 <see cref="ColorTransfer"/>。</summary>
    public static ColorTransfer ColorTransferFromNdk(int value) => value switch
    {
        1 => ColorTransfer.Linear,     // COLOR_TRANSFER_LINEAR
        3 => ColorTransfer.SdrVideo,   // COLOR_TRANSFER_SDR_VIDEO
        6 => ColorTransfer.St2084,     // COLOR_TRANSFER_ST2084
        7 => ColorTransfer.Hlg,        // COLOR_TRANSFER_HLG
        _ => ColorTransfer.Unspecified,
    };

    /// <summary>采样格式每样本字节数。</summary>
    public static int BytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.S16 => 2,
        SampleFormat.S32 => 4,
        SampleFormat.F32 => 4,
        _ => 0
    };
}

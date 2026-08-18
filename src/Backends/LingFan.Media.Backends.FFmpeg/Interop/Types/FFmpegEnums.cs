namespace LingFan.Media.Backends.FFmpeg.Interop;

/// <summary>
/// FFmpeg 像素格式枚举（值严格对齐 libavutil/pixfmt.h，跨主版本稳定）。
/// 仅列出本后端实际使用及校验所需的成员；其余格式运行时以整数透传。
/// </summary>
internal enum AVPixelFormat
{
    AV_PIX_FMT_NONE = -1,
    AV_PIX_FMT_YUV420P = 0,
    AV_PIX_FMT_YUYV422 = 1,
    AV_PIX_FMT_RGB24 = 2,
    AV_PIX_FMT_BGR24 = 3,
    AV_PIX_FMT_YUV422P = 4,
    AV_PIX_FMT_YUV444P = 5,
    AV_PIX_FMT_YUV410P = 6,
    AV_PIX_FMT_YUV411P = 7,
    AV_PIX_FMT_GRAY8 = 8,
    AV_PIX_FMT_MONOWHITE = 9,
    AV_PIX_FMT_MONOBLACK = 10,
    AV_PIX_FMT_PAL8 = 11,
    AV_PIX_FMT_NV12 = 23,
    AV_PIX_FMT_NV21 = 24,
    AV_PIX_FMT_ARGB = 25,
    AV_PIX_FMT_RGBA = 26,
    AV_PIX_FMT_ABGR = 27,
    AV_PIX_FMT_BGRA = 28,
    AV_PIX_FMT_GRAY16BE = 29,
    AV_PIX_FMT_GRAY16LE = 30,
    AV_PIX_FMT_YUV440P = 31,
    // 数值严格对齐 libavutil/pixfmt.h（FFmpeg 8.1 头文件权威解析；另经运行时 format=171 实证 D3D11）。
    // 跨 FFmpeg 4.x–9.0（avutil 56–61）ABI 稳定，既有枚举值不随版本变更。
    AV_PIX_FMT_VAAPI = 44,
    AV_PIX_FMT_YUV420P10LE = 62,
    AV_PIX_FMT_YUV420P10BE = 61,
    AV_PIX_FMT_YUV422P10LE = 64,
    AV_PIX_FMT_YUV444P10LE = 68,
    AV_PIX_FMT_P010LE = 158,
    AV_PIX_FMT_P010BE = 159,
    AV_PIX_FMT_VIDEOTOOLBOX = 157,
    AV_PIX_FMT_CUDA = 117,
    AV_PIX_FMT_D3D11VA_VLD = 116,
    AV_PIX_FMT_D3D11 = 171,
    AV_PIX_FMT_MEDIACODEC = 164,
}

/// <summary>
/// FFmpeg 采样格式枚举（值严格对齐 libavutil/samplefmt.h）。
/// </summary>
internal enum AVSampleFormat
{
    AV_SAMPLE_FMT_NONE = -1,
    AV_SAMPLE_FMT_U8 = 0,
    AV_SAMPLE_FMT_S16 = 1,
    AV_SAMPLE_FMT_S32 = 2,
    AV_SAMPLE_FMT_FLT = 3,
    AV_SAMPLE_FMT_DBL = 4,
    AV_SAMPLE_FMT_U8P = 5,
    AV_SAMPLE_FMT_S16P = 6,
    AV_SAMPLE_FMT_S32P = 7,
    AV_SAMPLE_FMT_FLTP = 8,
    AV_SAMPLE_FMT_DBLP = 9,
    AV_SAMPLE_FMT_S64 = 10,
    AV_SAMPLE_FMT_S64P = 11,
}

/// <summary>
/// FFmpeg 编解码器 ID 枚举（值严格对齐 libavcodec/codec_id.h，跨主版本稳定）。
/// </summary>
internal enum AVCodecID
{
    AV_CODEC_ID_NONE = 0,
    AV_CODEC_ID_MPEG2VIDEO = 2,
    AV_CODEC_ID_MPEG4 = 12,
    AV_CODEC_ID_H264 = 27,
    AV_CODEC_ID_HEVC = 173,
    AV_CODEC_ID_AV1 = 225,
    AV_CODEC_ID_VP9 = 167,
    AV_CODEC_ID_PCM_S16LE = 65536,
    AV_CODEC_ID_PCM_S24LE = 65548,
    AV_CODEC_ID_PCM_S32LE = 65544,
    AV_CODEC_ID_MP2 = 86016,
    AV_CODEC_ID_MP3 = 86017,
    AV_CODEC_ID_AAC = 86018,
    AV_CODEC_ID_AC3 = 86019,
    AV_CODEC_ID_VORBIS = 86021,
    AV_CODEC_ID_FLAC = 86028,
    AV_CODEC_ID_OPUS = 86076,
    AV_CODEC_ID_DVD_SUBTITLE = 94208,
    AV_CODEC_ID_DVB_SUBTITLE = 94209,
    AV_CODEC_ID_SSA = 94212,
    AV_CODEC_ID_HDMV_PGS_SUBTITLE = 94214,
    AV_CODEC_ID_SUBRIP = 94225,
    AV_CODEC_ID_WEBVTT = 94226,
    AV_CODEC_ID_ASS = 94230,
}

/// <summary>
/// FFmpeg 媒体类型枚举（值严格对齐 libavutil/avutil.h）。
/// </summary>
internal enum AVMediaType
{
    AVMEDIA_TYPE_UNKNOWN = -1,
    AVMEDIA_TYPE_VIDEO = 0,
    AVMEDIA_TYPE_AUDIO = 1,
    AVMEDIA_TYPE_SUBTITLE = 2,
    AVMEDIA_TYPE_DATA = 3,
    AVMEDIA_TYPE_ATTACHMENT = 4,
    AVMEDIA_TYPE_NB = 5,
}

/// <summary>
/// FFmpeg 硬件设备类型枚举（值严格对齐 libavutil/hwcontext.h）。
/// </summary>
internal enum AVHWDeviceType
{
    AV_HWDEVICE_TYPE_NONE = 0,
    AV_HWDEVICE_TYPE_VAAPI = 3,
    AV_HWDEVICE_TYPE_CUDA = 2,
    AV_HWDEVICE_TYPE_VIDEOTOOLBOX = 6,
    AV_HWDEVICE_TYPE_D3D11VA = 7,
    AV_HWDEVICE_TYPE_MEDIACODEC = 10,
}

/// <summary>
/// FFmpeg 通道顺序枚举（值严格对齐 libavutil/channel_layout.h）。
/// </summary>
internal enum AVChannelOrder
{
    AV_CHANNEL_ORDER_UNSPEC = 0,
    AV_CHANNEL_ORDER_NATIVE = 1,
    AV_CHANNEL_ORDER_CUSTOM = 2,
    AV_CHANNEL_ORDER_AMBISONIC = 3,
}

/// <summary>
/// FFmpeg 字幕类型枚举（值严格对齐 libavcodec/avcodec.h）。
/// </summary>
internal enum AVSubtitleType
{
    SUBTITLE_NONE = 0,
    SUBTITLE_BITMAP = 1,
    SUBTITLE_TEXT = 2,
    SUBTITLE_ASS = 3,
}

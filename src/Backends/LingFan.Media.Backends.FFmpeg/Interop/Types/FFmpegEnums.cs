namespace LingFan.Media.Backends.FFmpeg.Interop;

/// <summary>
/// FFmpeg 像素格式枚举（值严格对齐 libavutil/pixfmt.h，跨主版本稳定）。
/// 仅列出本后端实际使用及校验所需的成员；其余格式运行时以整数透传。
/// </summary>
internal enum AVPixelFormat
{
    /// <summary>未知/未指定像素格式。</summary>
    AV_PIX_FMT_NONE = -1,
    /// <summary>平面 YUV 4:2:0，8bit。</summary>
    AV_PIX_FMT_YUV420P = 0,
    /// <summary>打包 YUV 4:2:2（YUYV 字节序）。</summary>
    AV_PIX_FMT_YUYV422 = 1,
    /// <summary>打包 RGB 8:8:8。</summary>
    AV_PIX_FMT_RGB24 = 2,
    /// <summary>打包 BGR 8:8:8。</summary>
    AV_PIX_FMT_BGR24 = 3,
    /// <summary>平面 YUV 4:2:2，8bit。</summary>
    AV_PIX_FMT_YUV422P = 4,
    /// <summary>平面 YUV 4:4:4，8bit。</summary>
    AV_PIX_FMT_YUV444P = 5,
    /// <summary>平面 YUV 4:1:0，8bit。</summary>
    AV_PIX_FMT_YUV410P = 6,
    /// <summary>平面 YUV 4:1:1，8bit。</summary>
    AV_PIX_FMT_YUV411P = 7,
    /// <summary>8bit 灰度。</summary>
    AV_PIX_FMT_GRAY8 = 8,
    /// <summary>1bit 单色（0=白）。</summary>
    AV_PIX_FMT_MONOWHITE = 9,
    /// <summary>1bit 单色（0=黑）。</summary>
    AV_PIX_FMT_MONOBLACK = 10,
    /// <summary>8bit 调色板索引（附带 AV_PIX_DATA 调色板）。</summary>
    AV_PIX_FMT_PAL8 = 11,
    /// <summary>半平面 YUV 4:2:0：Y 平面 + UV 交织平面，8bit。</summary>
    AV_PIX_FMT_NV12 = 23,
    /// <summary>半平面 YUV 4:2:0：Y 平面 + VU 交织平面，8bit。</summary>
    AV_PIX_FMT_NV21 = 24,
    /// <summary>打包 ARGB 8:8:8:8。</summary>
    AV_PIX_FMT_ARGB = 25,
    /// <summary>打包 RGBA 8:8:8:8。</summary>
    AV_PIX_FMT_RGBA = 26,
    /// <summary>打包 ABGR 8:8:8:8。</summary>
    AV_PIX_FMT_ABGR = 27,
    /// <summary>打包 BGRA 8:8:8:8。</summary>
    AV_PIX_FMT_BGRA = 28,
    /// <summary>16bit 灰度，大端。</summary>
    AV_PIX_FMT_GRAY16BE = 29,
    /// <summary>16bit 灰度，小端。</summary>
    AV_PIX_FMT_GRAY16LE = 30,
    /// <summary>平面 YUV 4:4:0，8bit。</summary>
    AV_PIX_FMT_YUV440P = 31,
    // 数值严格对齐 libavutil/pixfmt.h（FFmpeg 8.1 头文件权威解析）。
    // 跨 FFmpeg 4.x–9.0（avutil 56–61）ABI 稳定，既有枚举值不随版本变更。
    /// <summary>VAAPI 硬件表面（data[3] 为 VA surface id）。</summary>
    AV_PIX_FMT_VAAPI = 44,
    /// <summary>平面 YUV 4:2:0，10bit 右对齐存于 16-bit 字，小端。</summary>
    AV_PIX_FMT_YUV420P10LE = 62,
    /// <summary>平面 YUV 4:2:0，10bit 右对齐存于 16-bit 字，大端。</summary>
    AV_PIX_FMT_YUV420P10BE = 61,
    /// <summary>平面 YUV 4:2:2，10bit 右对齐存于 16-bit 字，小端。</summary>
    AV_PIX_FMT_YUV422P10LE = 64,
    /// <summary>平面 YUV 4:4:4，10bit 右对齐存于 16-bit 字，小端。</summary>
    AV_PIX_FMT_YUV444P10LE = 68,
    /// <summary>半平面 YUV 4:2:0，10bit 左对齐存于 16-bit 字高位，小端。</summary>
    AV_PIX_FMT_P010LE = 158,
    /// <summary>半平面 YUV 4:2:0，10bit 左对齐存于 16-bit 字高位，大端。</summary>
    AV_PIX_FMT_P010BE = 159,
    /// <summary>VideoToolbox 硬件表面（data[3] 为 CVPixelBufferRef）。</summary>
    AV_PIX_FMT_VIDEOTOOLBOX = 157,
    /// <summary>CUDA 设备内存（data[0]/[1] 为 CUdeviceptr）。</summary>
    AV_PIX_FMT_CUDA = 117,
    /// <summary>D3D11VA 旧式表面（data[3] 为 ID3D11VideoDecoderOutputView）。</summary>
    AV_PIX_FMT_D3D11VA_VLD = 116,
    /// <summary>D3D11 纹理数组表面（data[0] 为 ID3D11Texture2D，data[1] 为数组索引）。</summary>
    AV_PIX_FMT_D3D11 = 171,
    /// <summary>Android MediaCodec 硬件表面（data[3] 为 AVMediaCodecBuffer）。</summary>
    AV_PIX_FMT_MEDIACODEC = 164,
}

/// <summary>
/// FFmpeg 采样格式枚举（值严格对齐 libavutil/samplefmt.h）。
/// </summary>
internal enum AVSampleFormat
{
    /// <summary>未知/未指定采样格式。</summary>
    AV_SAMPLE_FMT_NONE = -1,
    /// <summary>无符号 8bit，交织。</summary>
    AV_SAMPLE_FMT_U8 = 0,
    /// <summary>有符号 16bit，交织。</summary>
    AV_SAMPLE_FMT_S16 = 1,
    /// <summary>有符号 32bit，交织。</summary>
    AV_SAMPLE_FMT_S32 = 2,
    /// <summary>32bit 浮点，交织。</summary>
    AV_SAMPLE_FMT_FLT = 3,
    /// <summary>64bit 浮点，交织。</summary>
    AV_SAMPLE_FMT_DBL = 4,
    /// <summary>无符号 8bit，每声道独立平面。</summary>
    AV_SAMPLE_FMT_U8P = 5,
    /// <summary>有符号 16bit，每声道独立平面。</summary>
    AV_SAMPLE_FMT_S16P = 6,
    /// <summary>有符号 32bit，每声道独立平面。</summary>
    AV_SAMPLE_FMT_S32P = 7,
    /// <summary>32bit 浮点，每声道独立平面。</summary>
    AV_SAMPLE_FMT_FLTP = 8,
    /// <summary>64bit 浮点，每声道独立平面。</summary>
    AV_SAMPLE_FMT_DBLP = 9,
    /// <summary>有符号 64bit，交织。</summary>
    AV_SAMPLE_FMT_S64 = 10,
    /// <summary>有符号 64bit，每声道独立平面。</summary>
    AV_SAMPLE_FMT_S64P = 11,
}

/// <summary>
/// FFmpeg 编解码器 ID 枚举（值严格对齐 libavcodec/codec_id.h，跨主版本稳定）。
/// </summary>
internal enum AVCodecID
{
    /// <summary>未知/未指定编解码器。</summary>
    AV_CODEC_ID_NONE = 0,
    /// <summary>MPEG-2 视频。</summary>
    AV_CODEC_ID_MPEG2VIDEO = 2,
    /// <summary>MPEG-4 Part 2 视频。</summary>
    AV_CODEC_ID_MPEG4 = 12,
    /// <summary>H.264 / AVC 视频。</summary>
    AV_CODEC_ID_H264 = 27,
    /// <summary>H.265 / HEVC 视频。</summary>
    AV_CODEC_ID_HEVC = 173,
    /// <summary>AV1 视频。</summary>
    AV_CODEC_ID_AV1 = 225,
    /// <summary>VP9 视频。</summary>
    AV_CODEC_ID_VP9 = 167,
    /// <summary>PCM 有符号 16bit 小端音频。</summary>
    AV_CODEC_ID_PCM_S16LE = 65536,
    /// <summary>PCM 有符号 24bit 小端音频。</summary>
    AV_CODEC_ID_PCM_S24LE = 65548,
    /// <summary>PCM 有符号 32bit 小端音频。</summary>
    AV_CODEC_ID_PCM_S32LE = 65544,
    /// <summary>MPEG Audio Layer II 音频。</summary>
    AV_CODEC_ID_MP2 = 86016,
    /// <summary>MPEG Audio Layer III（MP3）音频。</summary>
    AV_CODEC_ID_MP3 = 86017,
    /// <summary>AAC 音频。</summary>
    AV_CODEC_ID_AAC = 86018,
    /// <summary>AC-3（Dolby Digital）音频。</summary>
    AV_CODEC_ID_AC3 = 86019,
    /// <summary>Vorbis 音频。</summary>
    AV_CODEC_ID_VORBIS = 86021,
    /// <summary>FLAC 无损音频。</summary>
    AV_CODEC_ID_FLAC = 86028,
    /// <summary>Opus 音频。</summary>
    AV_CODEC_ID_OPUS = 86076,
    /// <summary>DVD 图形字幕。</summary>
    AV_CODEC_ID_DVD_SUBTITLE = 94208,
    /// <summary>DVB 图形字幕。</summary>
    AV_CODEC_ID_DVB_SUBTITLE = 94209,
    /// <summary>SSA 文本字幕。</summary>
    AV_CODEC_ID_SSA = 94212,
    /// <summary>蓝光 HDMV PGS 图形字幕。</summary>
    AV_CODEC_ID_HDMV_PGS_SUBTITLE = 94214,
    /// <summary>SubRip（SRT）文本字幕。</summary>
    AV_CODEC_ID_SUBRIP = 94225,
    /// <summary>WebVTT 文本字幕。</summary>
    AV_CODEC_ID_WEBVTT = 94226,
    /// <summary>ASS 文本字幕。</summary>
    AV_CODEC_ID_ASS = 94230,
}

/// <summary>
/// FFmpeg 媒体类型枚举（值严格对齐 libavutil/avutil.h）。
/// </summary>
internal enum AVMediaType
{
    /// <summary>未知媒体类型。</summary>
    AVMEDIA_TYPE_UNKNOWN = -1,
    /// <summary>视频流。</summary>
    AVMEDIA_TYPE_VIDEO = 0,
    /// <summary>音频流。</summary>
    AVMEDIA_TYPE_AUDIO = 1,
    /// <summary>数据流（不透明载荷）。</summary>
    AVMEDIA_TYPE_DATA = 2,
    /// <summary>字幕流。</summary>
    AVMEDIA_TYPE_SUBTITLE = 3,
    /// <summary>附件（如字体）。</summary>
    AVMEDIA_TYPE_ATTACHMENT = 4,
    /// <summary>类型计数哨兵（非实际类型）。</summary>
    AVMEDIA_TYPE_NB = 5,
}

/// <summary>
/// FFmpeg 硬件设备类型枚举（值严格对齐 libavutil/hwcontext.h）。
/// </summary>
internal enum AVHWDeviceType
{
    /// <summary>无硬件设备。</summary>
    AV_HWDEVICE_TYPE_NONE = 0,
    /// <summary>VA-API（Linux 视频加速）。</summary>
    AV_HWDEVICE_TYPE_VAAPI = 3,
    /// <summary>NVIDIA CUDA。</summary>
    AV_HWDEVICE_TYPE_CUDA = 2,
    /// <summary>Apple VideoToolbox。</summary>
    AV_HWDEVICE_TYPE_VIDEOTOOLBOX = 6,
    /// <summary>Direct3D 11 视频加速（Windows）。</summary>
    AV_HWDEVICE_TYPE_D3D11VA = 7,
    /// <summary>Android MediaCodec。</summary>
    AV_HWDEVICE_TYPE_MEDIACODEC = 10,
}

/// <summary>
/// FFmpeg 通道顺序枚举（值严格对齐 libavutil/channel_layout.h）。
/// </summary>
internal enum AVChannelOrder
{
    /// <summary>未指定（仅知声道数）。</summary>
    AV_CHANNEL_ORDER_UNSPEC = 0,
    /// <summary>原生位掩码布局（AVChannel 组合）。</summary>
    AV_CHANNEL_ORDER_NATIVE = 1,
    /// <summary>自定义逐声道描述。</summary>
    AV_CHANNEL_ORDER_CUSTOM = 2,
    /// <summary>Ambisonic 高阶环绕。</summary>
    AV_CHANNEL_ORDER_AMBISONIC = 3,
}

/// <summary>
/// FFmpeg 字幕类型枚举（值严格对齐 libavcodec/avcodec.h）。
/// </summary>
internal enum AVSubtitleType
{
    /// <summary>无字幕内容。</summary>
    SUBTITLE_NONE = 0,
    /// <summary>位图字幕（rects 为像素矩形）。</summary>
    SUBTITLE_BITMAP = 1,
    /// <summary>纯文本字幕（text 字段）。</summary>
    SUBTITLE_TEXT = 2,
    /// <summary>ASS 行字幕（ass 字段，含样式/时间）。</summary>
    SUBTITLE_ASS = 3,
}

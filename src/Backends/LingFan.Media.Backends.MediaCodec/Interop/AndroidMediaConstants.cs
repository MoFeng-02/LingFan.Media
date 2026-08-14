namespace LingFan.Media.Backends.MediaCodec;

/// <summary>
/// NDK Media 常量与枚举（纯值定义，零外部引用）。
/// </summary>
/// <remarks>
/// <para>值严格取自 Android NDK 头（<c>media/NdkMedia*.h</c>）与 AOSP 实现，不可臆改——
/// 常量值变更会破坏与 <c>libmediandk</c> 的 ABI 契约。</para>
/// <para><b>错误码不是 -1 起算</b>：<c>media_status_t</c> 以 <c>AMEDIA_ERROR_BASE = -10000</c>
/// 为基值向下递减（<c>AMEDIA_ERROR_MALFORMED = BASE - 1</c> 即 -10001，依此类推）。
/// 按「负小整数」猜测会导致所有失败分支误判为未知错误，故此处以基值表达而非硬写字面量。</para>
/// </remarks>
internal static class AndroidMediaConstants
{
    // ============================================================
    // media_status_t（NdkMediaError.h，typedef int32_t）
    // ============================================================

    /// <summary>成功。</summary>
    public const int AMEDIA_OK = 0;

    /// <summary>错误码基值；所有 AMEDIA_ERROR_* 由此向下递减。</summary>
    public const int AMEDIA_ERROR_BASE = -10000;

    public const int AMEDIA_ERROR_UNKNOWN = AMEDIA_ERROR_BASE;               // -10000
    public const int AMEDIA_ERROR_MALFORMED = AMEDIA_ERROR_BASE - 1;         // -10001
    public const int AMEDIA_ERROR_UNSUPPORTED = AMEDIA_ERROR_BASE - 2;       // -10002
    public const int AMEDIA_ERROR_INVALID_OBJECT = AMEDIA_ERROR_BASE - 3;    // -10003
    public const int AMEDIA_ERROR_INVALID_PARAMETER = AMEDIA_ERROR_BASE - 4; // -10004
    public const int AMEDIA_ERROR_INVALID_OPERATION = AMEDIA_ERROR_BASE - 5; // -10005
    public const int AMEDIA_ERROR_END_OF_STREAM = AMEDIA_ERROR_BASE - 6;     // -10006
    public const int AMEDIA_ERROR_IO = AMEDIA_ERROR_BASE - 7;                // -10007
    public const int AMEDIA_ERROR_WOULD_BLOCK = AMEDIA_ERROR_BASE - 8;       // -10008

    // ============================================================
    // AMediaExtractor
    // ============================================================

    /// <summary>当前采样为同步（关键）帧。</summary>
    public const uint AMEDIAEXTRACTOR_SAMPLE_FLAG_SYNC = 1;

    /// <summary>当前采样已加密（本后端不支持 DRM，命中即拒绝）。</summary>
    public const uint AMEDIAEXTRACTOR_SAMPLE_FLAG_ENCRYPTED = 2;

    /// <summary>SeekMode：定位到目标之前最近的同步帧。</summary>
    public const int AMEDIAEXTRACTOR_SEEK_PREVIOUS_SYNC = 0;

    /// <summary>SeekMode：定位到目标之后最近的同步帧。</summary>
    public const int AMEDIAEXTRACTOR_SEEK_NEXT_SYNC = 1;

    /// <summary>SeekMode：定位到距目标最近的同步帧。</summary>
    public const int AMEDIAEXTRACTOR_SEEK_CLOSEST_SYNC = 2;

    // ============================================================
    // AMediaCodec
    // ============================================================

    /// <summary>输出 buffer 为关键帧。</summary>
    public const uint AMEDIACODEC_BUFFER_FLAG_KEY_FRAME = 1;

    /// <summary>该 buffer 承载编解码器配置数据（csd），非可呈现帧。</summary>
    public const uint AMEDIACODEC_BUFFER_FLAG_CODEC_CONFIG = 2;

    /// <summary>流结束标记。</summary>
    public const uint AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM = 4;

    /// <summary>部分帧（同一 PTS 的后续分片仍将到来）。</summary>
    public const uint AMEDIACODEC_BUFFER_FLAG_PARTIAL_FRAME = 8;

    /// <summary><c>AMediaCodec_configure</c> flags：编码模式。解码器恒传 0。</summary>
    public const uint AMEDIACODEC_CONFIGURE_FLAG_ENCODE = 1;

    /// <summary>dequeueOutputBuffer/dequeueInputBuffer：暂无可用 buffer，稍后重试。</summary>
    public const int AMEDIACODEC_INFO_TRY_AGAIN_LATER = -1;

    /// <summary>dequeueOutputBuffer：输出格式已变更，须重新读取 <c>AMediaCodec_getOutputFormat</c>。</summary>
    public const int AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED = -2;

    /// <summary>dequeueOutputBuffer：输出 buffer 集合已变更（NDK 下无需处理，保留以完整匹配返回域）。</summary>
    public const int AMEDIACODEC_INFO_OUTPUT_BUFFERS_CHANGED = -3;

    // ============================================================
    // MediaCodec 颜色格式（android.media.MediaCodecInfo.CodecCapabilities）
    // ============================================================

    /// <summary>I420 三平面（Y + U + V）。</summary>
    public const int COLOR_FormatYUV420Planar = 19;

    /// <summary>I420 紧凑三平面。</summary>
    public const int COLOR_FormatYUV420PackedPlanar = 20;

    /// <summary>NV12 半平面（Y + 交织 UV）。</summary>
    public const int COLOR_FormatYUV420SemiPlanar = 21;

    /// <summary>NV12 紧凑半平面。</summary>
    public const int COLOR_FormatYUV420PackedSemiPlanar = 39;

    /// <summary>柔性 YUV420（0x7F420888）：具体布局须经 MediaImage 查询，ByteBuffer 模式下不可直接假定。</summary>
    public const int COLOR_FormatYUV420Flexible = 0x7F420888; // 2135033992

    /// <summary>Surface 输出（0x7F000789）：仅 Surface 模式出现，ByteBuffer 模式命中即为配置错误。</summary>
    public const int COLOR_FormatSurface = 0x7F000789;

    /// <summary>高通厂商私有 NV12 变体（0x7FA30C00），按 NV12 处理。</summary>
    public const int COLOR_QCOM_FormatYUV420SemiPlanar = 0x7FA30C00;

    /// <summary>德州仪器厂商私有 NV12 变体（0x7F000100），按 NV12 处理。</summary>
    public const int COLOR_TI_FormatYUV420PackedSemiPlanar = 0x7F000100;

    // ============================================================
    // 音频 PCM 编码（AudioFormat.ENCODING_*，对应 "pcm-encoding" 键）
    // ============================================================

    public const int ENCODING_PCM_16BIT = 2;
    public const int ENCODING_PCM_8BIT = 3;
    public const int ENCODING_PCM_FLOAT = 4;
    public const int ENCODING_PCM_24BIT_PACKED = 21;
    public const int ENCODING_PCM_32BIT = 22;

    // ============================================================
    // AMediaFormat 键名（与 NDK AMEDIAFORMAT_KEY_* 的字符串字面量逐字一致）
    // ============================================================

    public const string KEY_MIME = "mime";
    public const string KEY_WIDTH = "width";
    public const string KEY_HEIGHT = "height";
    public const string KEY_COLOR_FORMAT = "color-format";
    public const string KEY_STRIDE = "stride";
    public const string KEY_SLICE_HEIGHT = "slice-height";

    /// <summary>编解码器配置数据（H.264 的 SPS+PPS / H.265 的 VPS+SPS+PPS）。</summary>
    public const string KEY_CSD_0 = "csd-0";
    public const string KEY_CSD_1 = "csd-1";
    public const string KEY_CSD_2 = "csd-2";

    public const string KEY_MAX_INPUT_SIZE = "max-input-size";
    public const string KEY_FRAME_RATE = "frame-rate";

    /// <summary>时长键名为 <c>durationUs</c>（驼峰，非 kebab-case），单位微秒。</summary>
    public const string KEY_DURATION_US = "durationUs";

    public const string KEY_DISPLAY_WIDTH = "display-width";
    public const string KEY_DISPLAY_HEIGHT = "display-height";
    public const string KEY_SAMPLE_RATE = "sample-rate";
    public const string KEY_CHANNEL_COUNT = "channel-count";
    public const string KEY_PCM_ENCODING = "pcm-encoding";
    public const string KEY_BIT_RATE = "bitrate";
    public const string KEY_LANGUAGE = "language";
    public const string KEY_ROTATION = "rotation-degrees";
    public const string KEY_AAC_PROFILE = "aac-profile";
    public const string KEY_IS_ADTS = "is-adts";

    /// <summary>裁剪矩形（含端点）。解码输出的对齐尺寸常大于显示尺寸，须以此裁剪。</summary>
    public const string KEY_CROP_LEFT = "crop-left";
    public const string KEY_CROP_RIGHT = "crop-right";
    public const string KEY_CROP_TOP = "crop-top";
    public const string KEY_CROP_BOTTOM = "crop-bottom";
}

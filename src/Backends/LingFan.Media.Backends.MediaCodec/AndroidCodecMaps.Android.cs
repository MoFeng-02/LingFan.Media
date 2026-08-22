namespace LingFan.Media.Backends.MediaCodec;

/// <summary>
/// <see cref="AndroidCodecMaps"/> 的 Android-only 分片：依赖 <c>Android.Media.MediaCodecCapabilities</c> / <c>Encoding</c>
/// 的颜色格式 → 像素格式、采样编码 → 采样格式映射。仅 net10.0-android 目标编译（见 csproj），可移植 net10.0 不包含。
/// </summary>
internal static partial class AndroidCodecMaps
{
    // ── 颜色格式 → 像素格式（ByteBuffer 软件输出路径）──

    /// <summary>将 NDK 颜色格式映射到 LingFan <see cref="PixelFormat"/>；不支持返回 null 由调用方拒绝。</summary>
    /// <remarks>
    /// 值取自 net-android 非废弃的 <c>Android.Media.MediaCodecCapabilities</c> 枚举（对应 AOSP
    /// <c>COLOR_Format*</c> 常量；旧常量字段在 .NET 绑定已标 <c>[Obsolete(..., true)]</c>，不可引用）。
    /// N/V12 半平面与 I420 三平面两类 YUV420 均映射到标准像素格式；其余（含私有变体）返回 null。
    /// </remarks>
    public static PixelFormat? ColorFormatToPixelFormat(int colorFormat)
    {
        return colorFormat switch
        {
            // NV12 半平面（Y + 交织 UV）
            (int)Android.Media.MediaCodecCapabilities.Formatyuv420flexible => PixelFormat.NV12,
            (int)Android.Media.MediaCodecCapabilities.Formatyuv420semiplanar => PixelFormat.NV12,
            (int)Android.Media.MediaCodecCapabilities.Formatyuv420packedsemiplanar => PixelFormat.NV12,
            (int)Android.Media.MediaCodecCapabilities.QcomFormatyuv420semiplanar => PixelFormat.NV12,
            (int)Android.Media.MediaCodecCapabilities.TiFormatyuv420packedsemiplanar => PixelFormat.NV12,
            // I420 三平面：Y + U + V
            (int)Android.Media.MediaCodecCapabilities.Formatyuv420planar => PixelFormat.YUV420P,
            (int)Android.Media.MediaCodecCapabilities.Formatyuv420packedplanar => PixelFormat.YUV420P,
            _ => null
        };
    }

    // ── pcm-encoding → 采样格式（音频解码输出）──

    /// <summary>将 AOSP pcm-encoding 值映射到 LingFan <see cref="SampleFormat"/>；不支持返回 null。</summary>
    /// <remarks>
    /// 值取自 net-android 非废弃的 <c>Android.Media.Encoding</c> 枚举（对应 AOSP <c>ENCODING_PCM_*</c>；
    /// 旧常量字段在 .NET 绑定已标 <c>[Obsolete(..., true)]</c>，不可引用）。
    /// <c>Pcm8bit</c> 与 <c>Pcm24bitPacked</c> 无对应 LingFan 枚举
    /// （8-bit 无 S8、24-bit 打包为 3 字节/样本），按「绝不假绿」原则返回 null 供调用方诚实失败。
    /// </remarks>
    public static SampleFormat? PcmEncodingToSampleFormat(int encoding)
    {
        return encoding switch
        {
            (int)Android.Media.Encoding.Pcm16bit => SampleFormat.S16,
            (int)Android.Media.Encoding.PcmFloat => SampleFormat.F32,
            // ENCODING_PCM_32BIT = 22（API 31）；枚举成员引用会触发 CA1416（Android 21 调用点），故用字面量。
            22 => SampleFormat.S32,
            _ => null
        };
    }
}
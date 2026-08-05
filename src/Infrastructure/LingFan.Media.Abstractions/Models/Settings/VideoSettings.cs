namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频解码与渲染设置。
/// </summary>
public sealed class VideoSettings
{
    /// <summary>是否启用硬件加速解码。</summary>
    public bool EnableHardwareAcceleration { get; init; } = true;

    /// <summary>首选编解码器（null 表示自动选择）。</summary>
    public VideoCodec? PreferredCodec { get; init; }

    /// <summary>最大解码宽度（null 表示不限制）。</summary>
    public int? MaxWidth { get; init; }

    /// <summary>最大解码高度（null 表示不限制）。</summary>
    public int? MaxHeight { get; init; }

    /// <summary>输出像素格式（null 表示使用源格式）。</summary>
    public PixelFormat? OutputPixelFormat { get; init; }

    /// <summary>
    /// 编解码器私有配置（如 H264/H265 的 SPS+PPS / avcC / hvcC），由解封装器在轨道信息中提供，
    /// 透传给解码器设置输入媒体类型（MF 后端经 <c>MF_MT_MPEG_SEQUENCE_HEADER</c>，FFmpeg 等后端通常忽略）。
    /// 零外部引用（BCL <see cref="ReadOnlyMemory{T}"/>），符合契约层“只增不改”演进准则。
    /// </summary>
    public ReadOnlyMemory<byte> CodecConfiguration { get; init; }

    /// <summary>
    /// 流时间基（<c>AVStream.time_base</c>）。透传自轨道信息，供 FFmpeg 等后端写入
    /// <c>AVCodecContext.pkt_timebase</c>，使解码帧 pts/dts 换算为正确秒数。默认 <c>default</c>（无效）。
    /// </summary>
    public Rational TimeBase { get; init; }
}

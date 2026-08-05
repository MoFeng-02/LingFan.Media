namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频轨道详情信息。
/// </summary>
public sealed class VideoTrackInfo
{
    /// <summary>视频宽度（像素）。</summary>
    public int Width { get; init; }

    /// <summary>视频高度（像素）。</summary>
    public int Height { get; init; }

    /// <summary>帧率（FPS）。</summary>
    public float FrameRate { get; init; }

    /// <summary>像素格式。</summary>
    public PixelFormat PixelFormat { get; init; }

    /// <summary>色彩空间（可能为 null）。</summary>
    public string? ColorSpace { get; init; }

    /// <summary>采样宽高比（可能为 null）。</summary>
    public Rational? Sar { get; init; }

    /// <summary>轨道时长。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// 编解码器私有配置（H264/H265 的 SPS+PPS / avcC / hvcC）。由解封装器从轨道媒体类型提取，
    /// 透传给解码器（MF 后端经 <c>MF_MT_MPEG_SEQUENCE_HEADER</c>）。契约层“只增不改”演进，零外部引用。
    /// 用可读写属性（非 init）：解封装器在轨道构建后于 ParseTracks 内回填。
    /// </summary>
    public ReadOnlyMemory<byte> CodecConfiguration { get; set; }

    /// <summary>
    /// 流时间基（<c>AVStream.time_base</c>）。ffmpeg 解码帧的 pts/dts 以此为单位，须透传给解码器
    /// 写入 <c>AVCodecContext.pkt_timebase</c> 才能正确换算时间戳。解码后 <c>avFrame->time_base</c> /
    /// <c>ctx->time_base</c> 常为 0，不能直接用于换算（否则帧时间戳全 0）。
    /// </summary>
    public Rational TimeBase { get; set; }
}

namespace LingFan.Media.Abstractions;

/// <summary>
/// 解码偏好策略：以"解码成本分"替代纯分辨率阈值，判断内容是否超出软件解码的实时能力，
/// 从而决定应"优先硬件加速"。成本分 = 宽×高×帧率 × 编码复杂度权重 × 位深权重。
/// </summary>
/// <remarks>
/// <para>设计动机：纯分辨率阈值会漏掉高帧率（如 1440p@120）与高复杂度编码（AV1/HEVC）场景；
/// 10-bit 比 8-bit 软件解码更贵约 30%。成本分综合三者，给出更稳的判据。经验值：
/// 1080p@60 H265≈199M、1440p@60 H265≈354M、4K@60 H264≈498M、4K@60 H265≈796M（单位：加权像素/秒）。</para>
/// <para>语义为"优先硬件加速"（PreferHardware），而非"强制"：本策略只决定硬解是否应被尝试；
/// 是否真能走硬解取决于运行时是否有可用 GPU 设备上下文，且硬解失败时必须优雅回落软解（既有保证）。
/// 用户显式关闭硬件加速的意图始终优先——策略不会覆盖 <see cref="MediaPlayerOptions.EnableHardwareAcceleration"/> 的 false。</para>
/// <para>纯静态、无反射、无分配，符合 AOT 零警告铁律。</para>
/// </remarks>
public static class DecodePreferencePolicy
{
    /// <summary>
    /// 默认软件解码能力阈值（成本分）。≈ 2K (2560×1440)@60 H265 8-bit 的加权值，
    /// 作为"建议优先硬解"的默认起点。超过即视为软件解码在典型 CPU 上难以实时。
    /// </summary>
    public const long DefaultSoftwareDecodeThreshold = 300_000_000L;

    /// <summary>
    /// 计算视频轨道的解码成本分（加权像素/秒）。返回 0 表示信息不足（如轨道信息为空）。
    /// </summary>
    /// <param name="info">视频轨道详情（宽/高/帧率/像素格式）。</param>
    /// <param name="codec">视频编码；未知时按基准权重计。</param>
    public static long ComputeScore(VideoTrackInfo? info, VideoCodec? codec)
    {
        if (info is null || info.Width <= 0 || info.Height <= 0)
            return 0;

        long pixelsPerSecond = (long)info.Width * info.Height * Math.Max(1L, (long)Math.Round(info.FrameRate));
        double weighted = pixelsPerSecond * CodecWeight(codec) * BitDepthWeight(info.PixelFormat);
        return (long)weighted;
    }

    /// <summary>
    /// 内容是否超过软件解码实时能力阈值（应优先硬件加速）。
    /// </summary>
    /// <param name="info">视频轨道详情。</param>
    /// <param name="codec">视频编码。</param>
    /// <param name="threshold">阈值；null 使用 <see cref="DefaultSoftwareDecodeThreshold"/>。</param>
    public static bool ExceedsSoftwareDecodeCapability(VideoTrackInfo? info, VideoCodec? codec, long? threshold = null)
    {
        long t = threshold ?? DefaultSoftwareDecodeThreshold;
        return ComputeScore(info, codec) >= t;
    }

    /// <summary>像素格式的位深（视频域：10-bit 格式记 10，其余记 8）。</summary>
    public static int BitDepthOf(PixelFormat format) => format switch
    {
        PixelFormat.P010 => 10,
        PixelFormat.YUV420P10 => 10,
        _ => 8,
    };

    /// <summary>编码复杂度权重（软件解码相对成本）。值越大越依赖硬件加速。</summary>
    private static double CodecWeight(VideoCodec? codec) => codec switch
    {
        VideoCodec.H264 => 1.0,
        VideoCodec.H265 => 1.6,
        VideoCodec.VP9 => 1.4,
        VideoCodec.AV1 => 2.2,
        VideoCodec.MPEG2 => 1.2,
        VideoCodec.MPEG4 => 1.2,
        _ => 1.0,
    };

    /// <summary>位深权重：10-bit 比 8-bit 软件解码更贵约 30%。</summary>
    private static double BitDepthWeight(PixelFormat format) => format switch
    {
        PixelFormat.P010 => 1.3,
        PixelFormat.YUV420P10 => 1.3,
        _ => 1.0,
    };
}

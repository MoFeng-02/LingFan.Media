namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频解码与输出设置。
/// </summary>
public sealed class AudioSettings
{
    /// <summary>首选编解码器（null 表示自动选择）。</summary>
    public AudioCodec? PreferredCodec { get; init; }

    /// <summary>
    /// 源采样率（Hz，来自轨道信息；null 表示未知）。
    /// 供需要显式参数的解码后端使用（如 Android MediaCodec 的 <c>configure</c> 部分解码器
    /// 要求显式 <c>sample-rate</c>/<c>channel-count</c>，仅 csd-0 推导不足会返回 EINVAL）。
    /// </summary>
    public int? SourceSampleRate { get; init; }

    /// <summary>
    /// 源声道数（来自轨道信息；null 表示未知）。见 <see cref="SourceSampleRate"/>。
    /// </summary>
    public int? SourceChannels { get; init; }

    /// <summary>输出采样率（null 表示使用源采样率）。</summary>
    public int? OutputSampleRate { get; init; }

    /// <summary>输出声道数（null 表示使用源声道数）。</summary>
    public int? OutputChannels { get; init; }

    /// <summary>输出采样格式（null 表示使用源格式）。</summary>
    public SampleFormat? OutputSampleFormat { get; init; }

    /// <summary>编解码器私有配置（如 AAC 的 AudioSpecificConfig）。透传自轨道 extradata，
    /// 解码器据此设置 ctx->extradata。无则为默认空。</summary>
    public ReadOnlyMemory<byte> CodecConfiguration { get; init; }

    /// <summary>流时间基（<c>AVStream.time_base</c>）。透传自轨道信息，供 FFmpeg 后端写入
    /// <c>pkt_timebase</c> 以正确换算时间戳。默认 <c>default</c>（无效）。</summary>
    public Rational TimeBase { get; init; }
}

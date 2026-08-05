namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频轨道详情信息。
/// </summary>
public sealed class AudioTrackInfo
{
    /// <summary>采样率（Hz，如 44100）。</summary>
    public int SampleRate { get; init; }

    /// <summary>声道数。</summary>
    public int Channels { get; init; }

    /// <summary>声道布局（如 "mono"、"stereo"、"5.1"，可能为 null）。</summary>
    public string? ChannelLayout { get; init; }

    /// <summary>每采样位数。</summary>
    public int BitsPerSample { get; init; }

    /// <summary>编解码器私有配置（如 AAC 的 AudioSpecificConfig）。解码器需据此设置 extradata。
    /// 无则为默认空。</summary>
    public ReadOnlyMemory<byte> CodecConfiguration { get; init; }

    /// <summary>流时间基（<c>AVStream.time_base</c>）。透传给解码器写入 <c>pkt_timebase</c> 以正确换算时间戳。</summary>
    public Rational TimeBase { get; init; }

    /// <summary>轨道时长。</summary>
    public TimeSpan Duration { get; init; }
}

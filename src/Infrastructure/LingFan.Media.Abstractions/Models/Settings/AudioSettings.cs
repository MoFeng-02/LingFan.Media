namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频解码与输出设置。
/// </summary>
public sealed class AudioSettings
{
    /// <summary>首选编解码器（null 表示自动选择）。</summary>
    public AudioCodec? PreferredCodec { get; init; }

    /// <summary>输出采样率（null 表示使用源采样率）。</summary>
    public int? OutputSampleRate { get; init; }

    /// <summary>输出声道数（null 表示使用源声道数）。</summary>
    public int? OutputChannels { get; init; }

    /// <summary>输出采样格式（null 表示使用源格式）。</summary>
    public SampleFormat? OutputSampleFormat { get; init; }
}

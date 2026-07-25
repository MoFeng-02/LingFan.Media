namespace LingFan.Media.Audio;

/// <summary>
/// 混音器输出设置。
/// </summary>
/// <remarks>
/// 定义混音输出的音频格式参数。所有通道的音频数据在混音时
/// 会被转换到此设置指定的格式。
/// </remarks>
public sealed class MixerSettings
{
    /// <summary>输出采样率（Hz，如 44100）。</summary>
    public int SampleRate { get; init; } = 44100;

    /// <summary>输出声道数（如 2 = 立体声）。</summary>
    public int Channels { get; init; } = 2;

    /// <summary>输出采样格式。</summary>
    public SampleFormat SampleFormat { get; init; } = SampleFormat.F32;
}

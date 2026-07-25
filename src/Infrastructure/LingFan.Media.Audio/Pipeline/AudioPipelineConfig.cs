namespace LingFan.Media.Audio;

/// <summary>
/// 音频管线配置快照。由 <see cref="AudioPipeline.BuildConfig"/> 生成，
/// 供 MediaPlayer（Task-02-09）在创建 Core <c>AudioPipeline</c> 执行器时消费。
/// </summary>
/// <remarks>
/// <para>不可变快照，创建后不可修改。包含执行相关配置项：</para>
/// <list type="bullet">
/// <item>输出采样率（<see cref="OutputSampleRate"/>）</item>
/// <item>输出声道数（<see cref="OutputChannels"/>）</item>
/// <item>输出采样格式（<see cref="OutputSampleFormat"/>）</item>
/// <item>效果链（<see cref="Effects"/>）</item>
/// <item>混音器设置（<see cref="MixerSettings"/>，null 表示无混音）</item>
/// </list>
/// <para>
/// MediaPlayer 将配置中的执行相关项映射为 <c>Core.AudioPipeline</c> 的运行时参数。
/// Core.AudioPipeline 不直接依赖 <c>LingFan.Media.Audio</c>，避免分层倒置。
/// </para>
/// </remarks>
public sealed class AudioPipelineConfig
{
    /// <summary>输出采样率（null 表示使用源采样率）。</summary>
    public int? OutputSampleRate { get; init; }

    /// <summary>输出声道数（null 表示使用源声道数）。</summary>
    public int? OutputChannels { get; init; }

    /// <summary>输出采样格式（null 表示使用源格式）。</summary>
    public SampleFormat? OutputSampleFormat { get; init; }

    /// <summary>效果链（可能为空列表，不会为 null）。</summary>
    public IReadOnlyList<IAudioEffect> Effects { get; init; } = Array.Empty<IAudioEffect>();

    /// <summary>混音器设置（null 表示不使用混音器）。</summary>
    public MixerSettings? MixerSettings { get; init; }
}

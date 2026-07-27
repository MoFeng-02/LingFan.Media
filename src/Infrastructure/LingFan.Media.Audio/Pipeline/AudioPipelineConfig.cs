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

    /// <summary>
    /// 将效果链转换为 Core 管线可用的中立委托序列（V2-06 C6）。
    /// </summary>
    /// <remarks>
    /// <para>每个 <see cref="IAudioEffect"/> 包装为一个 <c>Func&lt;AudioFrame, AudioFrame&gt;</c> 闭包。</para>
    /// <para>音量控制与混音器不在本配置内（配置仅含设置快照 <see cref="MixerSettings"/>，不含活动实例），
    /// 须由调用方通过 <c>AudioPipelineTransforms.FromVolume</c> / <c>FromMixer</c> 另行构造并合并。</para>
    /// <para>所有权转移：<see cref="IAudioEffect.Process"/> 内部 Dispose 输入帧并返回新帧；
    /// 禁用的效果透传（闭包直接返回输入帧）。</para>
    /// </remarks>
    public IReadOnlyList<Func<AudioFrame, AudioFrame>> ToTransforms()
    {
        if (Effects.Count == 0)
            return Array.Empty<Func<AudioFrame, AudioFrame>>();

        var list = new List<Func<AudioFrame, AudioFrame>>(Effects.Count);
        foreach (var effect in Effects)
        {
            if (effect is null)
                continue;
            list.Add(frame => effect.Process(frame));
        }
        return list;
    }

    /// <summary>
    /// 生成效果器状态重置委托（V2-08.1），供 Core 音频管线在 Seek/Flush 解码锁内调用。
    /// </summary>
    /// <remarks>
    /// <para>遍历 <see cref="Effects"/> 逐个调用 <see cref="IAudioEffect.Reset"/>，
    /// 清除有状态效果（均衡器 biquad / 混响延迟线 / 压缩器包络）的跨位置残留，避免定位后音频瞬态或拖尾。</para>
    /// <para>当 <see cref="Effects"/> 为空时返回 <c>null</c>，宿主据此可不注入 reset 钩子（与 <see cref="ToTransforms"/> 对称）。</para>
    /// <para>返回纯 BCL 委托（<see cref="Action"/>）：宿主将其包装为 <c>audioTransformsReset</c> 经
    /// <c>MediaPlayerFactory</c> 透传至 Core <c>AudioPipeline</c>；Core 不依赖 <c>LingFan.Media.Audio</c>
    /// 具体效果类型，严守依赖倒置。配置快照不可变，闭包捕获的 <see cref="Effects"/> 引用稳定、无生命周期风险。</para>
    /// </remarks>
    public Action? ResetEffects()
    {
        if (Effects.Count == 0)
            return null;

        return () =>
        {
            foreach (var effect in Effects)
                effect?.Reset();
        };
    }
}

namespace LingFan.Media.Audio;

/// <summary>
/// 音频效果链。按顺序依次执行所有已注册的 <see cref="IAudioEffect"/>。
/// </summary>
/// <remarks>
/// <para><b>所有权转移语义</b>：对每个效果依次调用 <see cref="IAudioEffect.Process"/>，
/// 前一个效果的输出帧作为下一个效果的输入帧。
/// 每个效果负责 Dispose 输入帧并返回新帧。</para>
/// <para>当效果 <see cref="IAudioEffect.IsEnabled"/> 为 false 时，该效果透传（跳过处理）。</para>
/// <para>非线程安全（效果链在播放启动前配置，运行时不可修改）。</para>
/// </remarks>
public sealed class EffectChain
{
    private readonly List<IAudioEffect> _effects = [];

    /// <summary>效果列表（只读视图）。</summary>
    public IReadOnlyList<IAudioEffect> Effects => _effects;

    /// <summary>
    /// 添加效果到链末尾。
    /// </summary>
    /// <param name="effect">音频效果。</param>
    /// <exception cref="ArgumentNullException">effect 为 null。</exception>
    public void AddEffect(IAudioEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        _effects.Add(effect);
    }

    /// <summary>
    /// 从链中移除效果。
    /// </summary>
    /// <param name="effect">要移除的效果。</param>
    public void RemoveEffect(IAudioEffect effect)
    {
        _effects.Remove(effect);
    }

    /// <summary>
    /// 依次执行所有效果。
    /// </summary>
    /// <param name="frame">输入帧。</param>
    /// <returns>处理后的帧。</returns>
    /// <remarks>
    /// 对每个效果依次调用 <see cref="IAudioEffect.Process"/>，
    /// 输入帧被 Dispose，返回新帧传入下一个效果。
    /// 禁用的效果透传输入帧。
    /// </remarks>
    public AudioFrame Process(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        foreach (var effect in _effects)
        {
            frame = effect.Process(frame);
        }
        return frame;
    }
}

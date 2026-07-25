namespace LingFan.Media.Audio.Effects;

/// <summary>
/// 均衡器效果。N 段 biquad（IIR）滤波器。
/// </summary>
/// <remarks>
/// <para>参数：</para>
/// <list type="bullet">
/// <item>Frequency: 中心频率（20Hz~20000Hz）</item>
/// <item>Gain: 增益（-12dB~+12dB）</item>
/// <item>Q: 品质因数（0.1~10.0）</item>
/// </list>
/// <para>V1 简化实现：透传效果器（不做实际处理，直接返回输入帧）。</para>
/// <para>V2 路径：实现 biquad filter 链，使用 <c>Span&lt;float&gt;</c> 直接操作 PCM 采样。</para>
/// <para><b>所有权转移</b>：<see cref="IsEnabled"/> 为 true 且执行实际处理时，
/// 输入帧被 Dispose，返回新帧。V1 透传模式下不 Dispose、不创建新帧。</para>
/// </remarks>
public sealed class EqualizerEffect : IAudioEffect
{
    /// <inheritdoc/>
    public string Name => "Equalizer";

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>中心频率（Hz）。</summary>
    public float Frequency
    {
        get => Parameters[0].Value;
        set => Parameters[0].Value = Math.Clamp(value, 20f, 20000f);
    }

    /// <summary>增益（dB）。</summary>
    public float Gain
    {
        get => Parameters[1].Value;
        set => Parameters[1].Value = Math.Clamp(value, -12f, 12f);
    }

    /// <summary>品质因数 Q。</summary>
    public float Q
    {
        get => Parameters[2].Value;
        set => Parameters[2].Value = Math.Clamp(value, 0.1f, 10f);
    }

    /// <inheritdoc/>
    public IReadOnlyList<AudioEffectParameter> Parameters { get; }

    /// <summary>
    /// 初始化 <see cref="EqualizerEffect"/> 的新实例。
    /// </summary>
    /// <param name="frequency">中心频率（默认 1000Hz）。</param>
    /// <param name="gain">增益（默认 0dB）。</param>
    /// <param name="q">品质因数（默认 0.707）。</param>
    public EqualizerEffect(float frequency = 1000f, float gain = 0f, float q = 0.707f)
    {
        Parameters =
        [
            new AudioEffectParameter("Frequency", Math.Clamp(frequency, 20f, 20000f), 20f, 20000f),
            new AudioEffectParameter("Gain", Math.Clamp(gain, -12f, 12f), -12f, 12f),
            new AudioEffectParameter("Q", Math.Clamp(q, 0.1f, 10f), 0.1f, 10f),
        ];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// V1 透传：直接返回输入帧，不做实际均衡处理。
    /// V2 将实现 biquad filter DSP → 创建新 AudioFrame → Dispose 输入 frame → 返回新帧。
    /// </remarks>
    public AudioFrame Process(AudioFrame frame)
    {
        if (!IsEnabled)
            return frame;

        // V1: 透传——不做实际均衡处理，直接返回输入帧
        // V2: 实现 biquad filter 链 → Span<float> DSP → 创建新 AudioFrame → Dispose 输入 frame → 返回新帧
        return frame;
    }
}

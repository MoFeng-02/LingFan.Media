namespace LingFan.Media.Audio.Effects;

/// <summary>
/// 混响效果。使用 Schroeder 或 Freeverb 算法。
/// </summary>
/// <remarks>
/// <para>参数：</para>
/// <list type="bullet">
/// <item>ReverbTime: 混响时间（0.1s~10s）</item>
/// <item>Wet: 湿度（0.0~1.0，干湿混合比例）</item>
/// <item>PreDelay: 预延迟（0ms~200ms）</item>
/// </list>
/// <para>V1 简化实现：透传效果器（不做实际处理，直接返回输入帧）。</para>
/// <para>V2 路径：实现 Schroeder 混响（并联 comb filter + 串联 allpass filter），
/// 使用 <c>Span&lt;float&gt;</c> 直接操作 PCM 采样。</para>
/// <para><b>所有权转移</b>：<see cref="IsEnabled"/> 为 true 且执行实际处理时，
/// 输入帧被 Dispose，返回新帧。V1 透传模式下不 Dispose、不创建新帧。</para>
/// </remarks>
public sealed class ReverbEffect : IAudioEffect
{
    /// <inheritdoc/>
    public string Name => "Reverb";

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>混响时间（秒）。</summary>
    public float ReverbTime
    {
        get => Parameters[0].Value;
        set => Parameters[0].Value = Math.Clamp(value, 0.1f, 10f);
    }

    /// <summary>湿度（干湿混合比例，0.0=全干, 1.0=全湿）。</summary>
    public float Wet
    {
        get => Parameters[1].Value;
        set => Parameters[1].Value = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>预延迟（毫秒）。</summary>
    public float PreDelay
    {
        get => Parameters[2].Value;
        set => Parameters[2].Value = Math.Clamp(value, 0f, 200f);
    }

    /// <inheritdoc/>
    public IReadOnlyList<AudioEffectParameter> Parameters { get; }

    /// <summary>
    /// 初始化 <see cref="ReverbEffect"/> 的新实例。
    /// </summary>
    /// <param name="reverbTime">混响时间（默认 2.0s）。</param>
    /// <param name="wet">湿度（默认 0.3）。</param>
    /// <param name="preDelay">预延迟（默认 20ms）。</param>
    public ReverbEffect(float reverbTime = 2.0f, float wet = 0.3f, float preDelay = 20f)
    {
        Parameters =
        [
            new AudioEffectParameter("ReverbTime", Math.Clamp(reverbTime, 0.1f, 10f), 0.1f, 10f),
            new AudioEffectParameter("Wet", Math.Clamp(wet, 0f, 1f), 0f, 1f),
            new AudioEffectParameter("PreDelay", Math.Clamp(preDelay, 0f, 200f), 0f, 200f),
        ];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// V1 透传：直接返回输入帧，不做实际混响处理。
    /// V2 将实现 Schroeder 混响算法 → Span<float> DSP → 创建新 AudioFrame → Dispose 输入 frame → 返回新帧。
    /// </remarks>
    public AudioFrame Process(AudioFrame frame)
    {
        if (!IsEnabled)
            return frame;

        // V1: 透传——不做实际混响处理，直接返回输入帧
        // V2: 实现 Schroeder 混响 → Span<float> DSP → 创建新 AudioFrame → Dispose 输入 frame → 返回新帧
        return frame;
    }
}

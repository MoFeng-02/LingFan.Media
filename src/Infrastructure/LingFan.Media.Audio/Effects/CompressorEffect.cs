namespace LingFan.Media.Audio.Effects;

/// <summary>
/// 动态范围压缩器效果。
/// </summary>
/// <remarks>
/// <para>参数：</para>
/// <list type="bullet">
/// <item>Threshold: 阈值（-60dB~0dB，超过此电平开始压缩）</item>
/// <item>Ratio: 压缩比（1:1~20:1）</item>
/// <item>Attack: 启动时间（0.1ms~100ms，信号超过阈值后达到完全压缩的时间）</item>
/// <item>Release: 释放时间（10ms~1000ms，信号低于阈值后恢复到不压缩的时间）</item>
/// </list>
/// <para>V1 简化实现：透传效果器（不做实际处理，直接返回输入帧）。</para>
/// <para>V2 路径：实现动态范围压缩算法（RMS/peak detection + gain reduction），
/// 使用 <c>Span&lt;float&gt;</c> 直接操作 PCM 采样。</para>
/// <para><b>所有权转移</b>：<see cref="IsEnabled"/> 为 true 且执行实际处理时，
/// 输入帧被 Dispose，返回新帧。V1 透传模式下不 Dispose、不创建新帧。</para>
/// </remarks>
public sealed class CompressorEffect : IAudioEffect
{
    /// <inheritdoc/>
    public string Name => "Compressor";

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>阈值（dB）。</summary>
    public float Threshold
    {
        get => Parameters[0].Value;
        set => Parameters[0].Value = Math.Clamp(value, -60f, 0f);
    }

    /// <summary>压缩比（1:1~20:1，值越大压缩越强）。</summary>
    public float Ratio
    {
        get => Parameters[1].Value;
        set => Parameters[1].Value = Math.Clamp(value, 1f, 20f);
    }

    /// <summary>启动时间（毫秒）。</summary>
    public float Attack
    {
        get => Parameters[2].Value;
        set => Parameters[2].Value = Math.Clamp(value, 0.1f, 100f);
    }

    /// <summary>释放时间（毫秒）。</summary>
    public float Release
    {
        get => Parameters[3].Value;
        set => Parameters[3].Value = Math.Clamp(value, 10f, 1000f);
    }

    /// <inheritdoc/>
    public IReadOnlyList<AudioEffectParameter> Parameters { get; }

    /// <summary>
    /// 初始化 <see cref="CompressorEffect"/> 的新实例。
    /// </summary>
    /// <param name="threshold">阈值（默认 -20dB）。</param>
    /// <param name="ratio">压缩比（默认 4:1）。</param>
    /// <param name="attack">启动时间（默认 10ms）。</param>
    /// <param name="release">释放时间（默认 100ms）。</param>
    public CompressorEffect(float threshold = -20f, float ratio = 4f,
        float attack = 10f, float release = 100f)
    {
        Parameters =
        [
            new AudioEffectParameter("Threshold", Math.Clamp(threshold, -60f, 0f), -60f, 0f),
            new AudioEffectParameter("Ratio", Math.Clamp(ratio, 1f, 20f), 1f, 20f),
            new AudioEffectParameter("Attack", Math.Clamp(attack, 0.1f, 100f), 0.1f, 100f),
            new AudioEffectParameter("Release", Math.Clamp(release, 10f, 1000f), 10f, 1000f),
        ];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// V1 透传：直接返回输入帧，不做实际压缩处理。
    /// V2 将实现动态范围压缩算法 → Span<float> DSP → 创建新 AudioFrame → Dispose 输入 frame → 返回新帧。
    /// </remarks>
    public AudioFrame Process(AudioFrame frame)
    {
        if (!IsEnabled)
            return frame;

        // V1: 透传——不做实际压缩处理，直接返回输入帧
        // V2: 实现压缩算法 → Span<float> DSP → 创建新 AudioFrame → Dispose 输入 frame → 返回新帧
        return frame;
    }
}

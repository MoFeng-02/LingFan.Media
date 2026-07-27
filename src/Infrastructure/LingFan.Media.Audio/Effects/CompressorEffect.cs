namespace LingFan.Media.Audio.Effects;

using System;
using System.Buffers;
using System.Collections.Generic;
using LingFan.Media.Abstractions;

/// <summary>
/// 动态范围压缩器。前馈式：峰值包络跟随（attack/release 时间常数）→ 软膝增益计算 → 增益平滑。
/// </summary>
/// <remarks>
/// <para>参数：Threshold（阈值 dB）、Ratio（压缩比 1:1~20:1）、Attack（启动 ms）、Release（释放 ms）、KneeWidth（软膝宽 dB）。</para>
/// <para><b>所有权转移</b>：<see cref="IAudioEffect.IsEnabled"/> 为 true 时输入帧被 Dispose，返回新帧；禁用时透传。</para>
/// <para>单包络/单增益状态（跨声道共享，避免立体声泵浦），热路径同步、无分配。</para>
/// <para>Ratio=1 时为恒等变换（无增益衰减）。</para>
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

    /// <summary>软膝宽度（dB，0 表示硬膝）。</summary>
    public float KneeWidth
    {
        get => Parameters[4].Value;
        set => Parameters[4].Value = Math.Clamp(value, 0f, 40f);
    }

    /// <inheritdoc/>
    public IReadOnlyList<AudioEffectParameter> Parameters { get; }

    private float _envelope;
    private float _gain = 1f;

    /// <summary>
    /// 初始化 <see cref="CompressorEffect"/> 的新实例。
    /// </summary>
    public CompressorEffect(float threshold = -20f, float ratio = 4f,
        float attack = 10f, float release = 100f, float kneeWidth = 6f)
    {
        Parameters =
        [
            new AudioEffectParameter("Threshold", Math.Clamp(threshold, -60f, 0f), -60f, 0f),
            new AudioEffectParameter("Ratio", Math.Clamp(ratio, 1f, 20f), 1f, 20f),
            new AudioEffectParameter("Attack", Math.Clamp(attack, 0.1f, 100f), 0.1f, 100f),
            new AudioEffectParameter("Release", Math.Clamp(release, 10f, 1000f), 10f, 1000f),
            new AudioEffectParameter("KneeWidth", Math.Clamp(kneeWidth, 0f, 40f), 0f, 40f),
        ];
    }

    /// <inheritdoc/>
    public AudioFrame Process(AudioFrame frame)
    {
        if (!IsEnabled)
            return frame;

        var fmt = frame.SampleFormat;
        var channels = frame.Channels;
        var sampleRate = frame.SampleRate;
        int bps = PcmConversions.BytesPerSample(fmt);
        int sampleCount = frame.Data.Length / bps;

        var floats = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            PcmConversions.DecodeToFloat(frame.Data.Span[..(sampleCount * bps)], fmt, floats.AsSpan(0, sampleCount));
            ProcessFloats(floats.AsSpan(0, sampleCount), sampleRate);
            var outBytes = new byte[sampleCount * bps];
            PcmConversions.EncodeFromFloat(floats.AsSpan(0, sampleCount), fmt, outBytes);
            var ts = frame.Timestamp;
            var dur = frame.Duration;
            frame.Dispose();
            return new AudioFrame(outBytes, sampleRate, channels, fmt, ts, dur, sampleCount / channels);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floats);
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        // 复位峰值包络与平滑增益至静默初始态（无分配，AOT 友好）。
        _envelope = 0f;
        _gain = 1f;
    }

    private void ProcessFloats(Span<float> samples, float sampleRate)
    {
        int sr = Math.Max(1, (int)sampleRate);
        float aCoef = MathF.Exp(-1f / (Math.Max(0.0001f, Attack / 1000f) * sr));
        float rCoef = MathF.Exp(-1f / (Math.Max(0.0001f, Release / 1000f) * sr));
        float threshold = Threshold;
        float ratio = Math.Max(1f, Ratio);
        float knee = KneeWidth;
        float oneMinusInvRatio = 1f - 1f / ratio;

        for (int i = 0; i < samples.Length; i++)
        {
            float x = samples[i];
            float level = Math.Abs(x);

            // 峰值包络跟随：上升用 attack（更快），下降用 release（更慢）
            float coef = level > _envelope ? aCoef : rCoef;
            _envelope = _envelope + coef * (level - _envelope);

            // 计算目标增益衰减（dB）
            float envDb = 20f * MathF.Log10(_envelope + 1e-6f);
            float targetDb = 0f;
            if (envDb > threshold)
            {
                if (knee > 0f)
                {
                    float lower = threshold - knee / 2f;
                    if (envDb < lower) targetDb = 0f;
                    else if (envDb > threshold + knee / 2f)
                        targetDb = (envDb - (threshold + knee / 2f)) * oneMinusInvRatio;
                    else
                    {
                        float k = (envDb - lower) / knee;
                        targetDb = k * k * knee / 2f * oneMinusInvRatio;
                    }
                }
                else
                {
                    targetDb = (envDb - threshold) * oneMinusInvRatio;
                }
            }

            float targetGain = MathF.Pow(10f, -targetDb / 20f);

            // 增益平滑：压缩（targetGain < _gain，快速 attack）；恢复（慢速 release）
            float gCoef = targetGain < _gain ? aCoef : rCoef;
            _gain = _gain + gCoef * (targetGain - _gain);

            samples[i] = x * _gain;
        }
    }
}

namespace LingFan.Media.Audio.Effects;

using System;
using System.Buffers;
using System.Collections.Generic;
using LingFan.Media.Abstractions;

/// <summary>
/// 均衡器效果。单段 peaking（峰值）biquad（IIR）滤波器。
/// </summary>
/// <remarks>
/// <para>参数：Frequency（中心频率 20~20000Hz）、Gain（增益 -12~+12dB）、Q（品质因数 0.1~10）。</para>
/// <para><b>所有权转移</b>：<see cref="IAudioEffect.IsEnabled"/> 为 true 时，输入帧被 Dispose，返回新帧；
/// 禁用时透传（直接返回输入帧，不 Dispose、不创建新帧）。</para>
/// <para>biquad 系数在参数或采样率变化时重算（缓存），逐声道持有 Direct Form I 状态，无额外分配。</para>
/// <para>增益为 0dB 时该滤波器为恒等变换（分子=分母）。</para>
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
            _filter.Process(floats.AsSpan(0, sampleCount), sampleRate, channels, Frequency, Gain, Q);
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
        // 清零 biquad 逐声道 Direct Form I 状态（原地清零，不重分配，AOT 友好）。
        // 系数缓存（_coeffRate 等）保留——同参数下下次 Process 走快速路径，状态归零即可消除瞬态。
        _filter.Reset();
    }

    private readonly BiquadFilter _filter = new();
}

/// <summary>单段 biquad 滤波器（Direct Form I，逐声道状态）。</summary>
internal sealed class BiquadFilter
{
    private float _b0, _b1, _b2, _a1, _a2;
    private float[] _x1 = [], _x2 = [], _y1 = [], _y2 = [];
    private int _coeffRate = -1;
    private float _coeffFreq = float.NaN, _coeffGain = float.NaN, _coeffQ = float.NaN;

    public void Process(Span<float> samples, float sampleRate, int channels, float freqHz, float gainDb, float q)
    {
        if (channels <= 0 || samples.Length == 0) return;
        if (sampleRate != _coeffRate || freqHz != _coeffFreq || gainDb != _coeffGain || q != _coeffQ)
            Recompute(freqHz, gainDb, q, sampleRate);
        if (_x1.Length != channels)
        {
            _x1 = new float[channels]; _x2 = new float[channels];
            _y1 = new float[channels]; _y2 = new float[channels];
        }

        for (int ch = 0; ch < channels; ch++)
        {
            float x1 = _x1[ch], x2 = _x2[ch], y1 = _y1[ch], y2 = _y2[ch];
            for (int i = ch; i < samples.Length; i += channels)
            {
                float x = samples[i];
                float y = _b0 * x + _b1 * x1 + _b2 * x2 - _a1 * y1 - _a2 * y2;
                x2 = x1; x1 = x;
                y2 = y1; y1 = y;
                samples[i] = y;
            }
            _x1[ch] = x1; _x2[ch] = x2; _y1[ch] = y1; _y2[ch] = y2;
        }
    }

    /// <summary>
    /// 重置逐声道 Direct Form I 状态为静默初始态（原地清零，不重分配缓冲区）。
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _x1.Length; i++)
        {
            _x1[i] = 0f; _x2[i] = 0f; _y1[i] = 0f; _y2[i] = 0f;
        }
    }

    private void Recompute(float freqHz, float gainDb, float q, float sampleRate)
    {
        float A = MathF.Pow(10f, gainDb / 40f);
        float w0 = 2f * MathF.PI * freqHz / sampleRate;
        float cw = MathF.Cos(w0), sw = MathF.Sin(w0);
        float alpha = sw / (2f * MathF.Max(q, 1e-3f));
        float b0 = 1f + alpha * A;
        float b1 = -2f * cw;
        float b2 = 1f - alpha * A;
        float a0 = 1f + alpha / A;
        float a1 = -2f * cw;
        float a2 = 1f - alpha / A;
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
        _a1 = a1 / a0; _a2 = a2 / a0;
        _coeffRate = (int)sampleRate; _coeffFreq = freqHz; _coeffGain = gainDb; _coeffQ = q;
    }
}

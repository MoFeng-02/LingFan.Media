namespace LingFan.Media.Audio.Effects;

using System;
using System.Buffers;
using System.Collections.Generic;
using LingFan.Media.Abstractions;

/// <summary>
/// 混响效果。Schroeder 结构：4 路并联 comb filter（含一阶阻尼）+ 2 路串联 allpass filter，
/// 末级 Wet/Dry 交叉淡入。每声道独立持有延迟线状态。
/// </summary>
/// <remarks>
/// <para>参数：ReverbTime（混响时间 0.1~10s，控制 comb 反馈）、Wet（湿度 0~1，干湿比）、PreDelay（预延迟 0~200ms）。</para>
/// <para><b>所有权转移</b>：<see cref="IAudioEffect.IsEnabled"/> 为 true 时输入帧被 Dispose，返回新帧；禁用时透传。</para>
/// <para>效果为有状态（延迟线），由音频管线线程单线程调用，无需锁。采样率变化时延迟线自动按新采样率重分配。</para>
/// <para>Wet=0 时输出为干声（与输入恒等，仅预延迟作用于湿声路径）。</para>
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

    private static readonly float[] CombDelays = [0.0297f, 0.0371f, 0.0411f, 0.0437f];
    private static readonly float[] AllpassDelays = [0.0050f, 0.0017f];
    private const float AllpassGain = 0.5f;
    private const float CombDamp = 0.2f;

    private int _sampleRate;
    private int _channels;
    private CombFilter[] _combs = [];
    private AllpassFilter[] _allpasses = [];
    private float[] _preDelayBuf = [];
    private int[] _preDelayPos = [];
    private int _preDelayLen;

    /// <summary>
    /// 初始化 <see cref="ReverbEffect"/> 的新实例。
    /// </summary>
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
            ProcessFloats(floats.AsSpan(0, sampleCount), sampleRate, channels);
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
        // 清零所有延迟线状态（原地清零，不重分配，AOT 友好）。
        // 与 Process 同线程（解码锁内）调用，无需锁。
        foreach (var c in _combs) c?.Reset();
        foreach (var a in _allpasses) a?.Reset();
        if (_preDelayBuf.Length > 0) Array.Clear(_preDelayBuf, 0, _preDelayBuf.Length);
        if (_preDelayPos.Length > 0) Array.Clear(_preDelayPos, 0, _preDelayPos.Length);
    }

    private void ProcessFloats(Span<float> samples, float sampleRate, int channels)
    {
        if (channels <= 0) return;
        EnsureBuffers((int)sampleRate, channels);
        UpdateFeedback();

        float wet = Wet;
        float dry = 1f - wet;
        int combCount = CombDelays.Length;
        int allCount = AllpassDelays.Length;

        for (int ch = 0; ch < channels; ch++)
        {
            int pdOff = ch * _preDelayLen;
            for (int i = ch; i < samples.Length; i += channels)
            {
                float x = samples[i];

                // 预延迟：送入混响网络的信号先经过延迟环（仅影响湿声路径）
                float delayed = x;
                if (_preDelayLen > 1)
                {
                    delayed = _preDelayBuf[pdOff + _preDelayPos[ch]];
                    _preDelayBuf[pdOff + _preDelayPos[ch]] = x;
                    _preDelayPos[ch] = (_preDelayPos[ch] + 1) % _preDelayLen;
                }

                // 并联 comb
                float y = 0f;
                int baseC = ch * combCount;
                for (int c = 0; c < combCount; c++)
                    y += _combs[baseC + c].Process(delayed);

                // 串联 allpass
                int baseA = ch * allCount;
                for (int a = 0; a < allCount; a++)
                    y = _allpasses[baseA + a].Process(y);

                samples[i] = dry * x + wet * y;
            }
        }
    }

    private void EnsureBuffers(int sampleRate, int channels)
    {
        if (_sampleRate == sampleRate && _channels == channels && _combs.Length == channels * CombDelays.Length)
            return;
        _sampleRate = sampleRate;
        _channels = channels;
        int combCount = CombDelays.Length;
        int allCount = AllpassDelays.Length;
        _combs = new CombFilter[channels * combCount];
        _allpasses = new AllpassFilter[channels * allCount];
        for (int ch = 0; ch < channels; ch++)
        {
            for (int c = 0; c < combCount; c++)
            {
                int len = Math.Max(1, (int)(CombDelays[c] * sampleRate));
                _combs[ch * combCount + c] = new CombFilter(len, CombDamp);
            }
            for (int a = 0; a < allCount; a++)
            {
                int len = Math.Max(1, (int)(AllpassDelays[a] * sampleRate));
                _allpasses[ch * allCount + a] = new AllpassFilter(len, AllpassGain);
            }
        }
        _preDelayLen = Math.Max(1, (int)(PreDelay / 1000f * sampleRate));
        _preDelayBuf = new float[channels * _preDelayLen];
        _preDelayPos = new int[channels];
    }

    private void UpdateFeedback()
    {
        float rt = ReverbTime;
        int combCount = CombDelays.Length;
        for (int i = 0; i < _combs.Length; i++)
        {
            int c = i % combCount;
            _combs[i].SetFeedback(MathF.Pow(10f, -3f * CombDelays[c] / rt));
        }
    }
}

/// <summary>并联 comb filter（含一阶阻尼反馈）。</summary>
internal sealed class CombFilter
{
    private readonly float[] _buf;
    private readonly float _damp;
    private int _idx;
    private float _feedback = 0.5f;
    private float _prev;

    public CombFilter(int length, float damp)
    {
        _buf = new float[length];
        _damp = damp;
    }

    public void SetFeedback(float feedback) => _feedback = feedback;

    public float Process(float input)
    {
        float output = _buf[_idx];
        float fb = output * _feedback;
        fb = fb * (1f - _damp) + _prev * _damp; // 反馈路径一阶低通阻尼
        _prev = fb;
        _buf[_idx] = input + fb;
        _idx = (_idx + 1) % _buf.Length;
        return output;
    }

    /// <summary>
    /// 重置延迟线状态为静默初始态（原地清零，不重分配缓冲区）。
    /// </summary>
    public void Reset()
    {
        if (_buf.Length > 0) Array.Clear(_buf, 0, _buf.Length);
        _idx = 0;
        _prev = 0f;
    }
}

/// <summary>串联 allpass filter（全通，扩散效果）。</summary>
internal sealed class AllpassFilter
{
    private readonly float[] _buf;
    private readonly float _g;
    private int _idx;

    public AllpassFilter(int length, float g)
    {
        _buf = new float[length];
        _g = g;
    }

    public float Process(float input)
    {
        float output = _buf[_idx];
        _buf[_idx] = input + output * _g;
        _idx = (_idx + 1) % _buf.Length;
        return output - input;
    }

    /// <summary>
    /// 重置延迟线状态为静默初始态（原地清零，不重分配缓冲区）。
    /// </summary>
    public void Reset()
    {
        if (_buf.Length > 0) Array.Clear(_buf, 0, _buf.Length);
        _idx = 0;
    }
}

namespace LingFan.Media.Audio;

using System;
using System.Buffers;
using System.Collections.Generic;
using LingFan.Media.Abstractions;

/// <summary>
/// 多路音频混音器。将多个 <see cref="MixerChannel"/> 的音频数据混合为一路输出。
/// </summary>
/// <remarks>
/// <para>混音流程：</para>
/// <list type="number">
/// <item>从每个激活且未静音的通道获取 sampleCount 个采样（按各通道输入声道数读取）</item>
/// <item>自动将各通道采样数转换为输出声道布局（<see cref="MixerSettings.Channels"/>，AU6）</item>
/// <item>所有通道相加（每通道乘其 Volume）</item>
/// <item>乘 <see cref="MasterVolume"/></item>
/// <item>Clamp 到 [-1,1]，转换为输出格式返回 <see cref="AudioFrame"/></item>
/// </list>
/// <para>使用 <see cref="Span{T}"/> 做零拷贝 DSP 处理。实现 <see cref="IDisposable"/> 释放各通道池化缓冲。</para>
/// </remarks>
public sealed class AudioMixer : IDisposable
{
    private readonly List<MixerChannel> _channels = [];
    private readonly MixerSettings _settings;
    private float _masterVolume = 1.0f;

    /// <summary>混音通道列表（只读视图）。</summary>
    public IReadOnlyList<MixerChannel> Channels => _channels;

    /// <summary>主音量（0.0~1.0）。</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>混音输出设置。</summary>
    public MixerSettings Settings => _settings;

    /// <summary>
    /// 初始化 <see cref="AudioMixer"/> 的新实例。
    /// </summary>
    public AudioMixer(MixerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>创建并添加混音通道。</summary>
    public MixerChannel CreateChannel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var channel = new MixerChannel(name);
        _channels.Add(channel);
        return channel;
    }

    /// <summary>移除混音通道。</summary>
    public void RemoveChannel(MixerChannel channel) => _channels.Remove(channel);

    /// <summary>
    /// 混合指定采样数的音频。
    /// </summary>
    /// <param name="sampleCount">每声道采样数。</param>
    /// <returns>混合后的 <see cref="AudioFrame"/>。若无激活通道，返回静音帧。</returns>
    public AudioFrame Mix(int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);

        int outCh = _settings.Channels;
        int totalSamples = sampleCount * outCh;

        // A2: mixBuffer 大尺寸时改用 ArrayPool 租借（与下方 chBuf 一致），避免每帧 new float[] 的 LOH/GC 压力
        float[]? mixRented = null;
        Span<float> mixBuffer = totalSamples <= 256
            ? stackalloc float[256]
            : (mixRented = ArrayPool<float>.Shared.Rent(totalSamples));
        mixBuffer = mixBuffer[..totalSamples];

        try
        {
            mixBuffer.Clear();

            // 预计算最大输入声道数，循环外分配复用缓冲，避免循环内 stackalloc（CA2014）
            int maxInCh = 1;
            foreach (var ch in _channels)
                if (ch.IsActive && !ch.IsMuted && ch.Volume > 0f)
                    maxInCh = Math.Max(maxInCh, Math.Max(1, ch.InputChannels));

            int chCapacity = sampleCount * maxInCh;
            float[]? rented = null;
            Span<float> chBuf = chCapacity <= 256
                ? stackalloc float[256]
                : (rented = ArrayPool<float>.Shared.Rent(chCapacity));
            try
            {
                foreach (var channel in _channels)
                {
                    if (!channel.IsActive || channel.IsMuted || channel.Volume <= 0f)
                        continue;

                    int inCh = Math.Max(1, channel.InputChannels);
                    int needed = sampleCount * inCh;
                    int read = channel.Read(chBuf[..needed]);
                    if (read <= 0) continue;

                    float volume = channel.Volume;
                    AddConverted(chBuf[..read], inCh, outCh, sampleCount, volume, mixBuffer);
                }
            }
            finally
            {
                if (rented != null) ArrayPool<float>.Shared.Return(rented);
            }

            for (int i = 0; i < totalSamples; i++)
                mixBuffer[i] = Math.Clamp(mixBuffer[i] * _masterVolume, -1f, 1f);

            var data = ConvertFromFloat(mixBuffer, _settings.SampleFormat);
            return new AudioFrame(
                data,
                _settings.SampleRate,
                outCh,
                _settings.SampleFormat,
                TimeSpan.Zero,
                TimeSpan.Zero,
                sampleCount);
        }
        finally
        {
            if (mixRented != null) ArrayPool<float>.Shared.Return(mixRented);
        }
    }

    /// <summary>清空所有通道的缓冲区。</summary>
    public void ClearAll()
    {
        foreach (var channel in _channels)
            channel.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var channel in _channels)
            channel.Dispose();
        _channels.Clear();
    }

    /// <summary>
    /// 将单通道（inCh 声道）读出的交错采样转换为输出布局（outCh 声道）并叠加到混音缓冲。
    /// </summary>
    private static void AddConverted(ReadOnlySpan<float> src, int inCh, int outCh, int frames, float volume, Span<float> mix)
    {
        int srcFrames = src.Length / inCh;
        for (int f = 0; f < frames && f < srcFrames; f++)
        {
            int srcBase = f * inCh;
            int mixBase = f * outCh;
            for (int c = 0; c < outCh; c++)
            {
                float v;
                if (inCh == outCh) v = src[srcBase + c];
                else if (inCh == 1) v = src[srcBase];                                   // Mono → 任意：复制
                else if (inCh == 2 && outCh == 1) v = (src[srcBase] + src[srcBase + 1]) * 0.5f; // Stereo → Mono：平均
                else if (inCh == 2 && outCh == 6) v = c < 2 ? src[srcBase + c] : 0f;          // Stereo → 5.1：前左/前右，其余静音
                else if (outCh > inCh) v = src[srcBase + (c < inCh ? c : inCh - 1)];             // 通用上混音
                else
                {
                    int groupSize = inCh / outCh;                                           // 通用下混音：分组平均
                    float sum = 0f;
                    for (int k = 0; k < groupSize; k++) sum += src[srcBase + c * groupSize + k];
                    v = sum / groupSize;
                }
                mix[mixBase + c] += v * volume;
            }
        }
    }

    private static byte[] ConvertFromFloat(ReadOnlySpan<float> samples, SampleFormat format) => format switch
    {
        SampleFormat.F32 => ConvertToF32(samples),
        SampleFormat.S16 => ConvertToS16(samples),
        SampleFormat.S32 => ConvertToS32(samples),
        _ => ConvertToF32(samples),
    };

    private static byte[] ConvertToF32(ReadOnlySpan<float> samples)
    {
        var result = new byte[samples.Length * sizeof(float)];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(result);
        return result;
    }

    private static byte[] ConvertToS16(ReadOnlySpan<float> samples)
    {
        var result = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            result[i] = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
        var bytes = new byte[result.Length * sizeof(short)];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes<short>(result).CopyTo(bytes);
        return bytes;
    }

    private static byte[] ConvertToS32(ReadOnlySpan<float> samples)
    {
        var result = new int[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            if (samples[i] >= 1.0f) result[i] = int.MaxValue;
            else if (samples[i] <= -1.0f) result[i] = int.MinValue;
            else result[i] = (int)(samples[i] * 2147483647f);
        }
        var bytes = new byte[result.Length * sizeof(int)];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes<int>(result).CopyTo(bytes);
        return bytes;
    }
}

using System;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Audio;
using LingFan.Media.Audio.Effects;
using Xunit;

namespace LingFan.Media.Audio.Tests;

/// <summary>
/// V2-08 AU1~AU6 真实算法与混音单元验证。
/// 重点：所有权转移（启用时输入帧被 Dispose、返回新帧）、恒等变换、整数/溢出边界、环形缓冲与声道转换。
/// </summary>
public class DspAndMixerTests
{
    // ---- 测试帧构造助手（F32，便于精确浮点断言） ----

    private static AudioFrame MakeF32(float[] samples, int channels, int sampleRate = 48000)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        MemoryMarshal.AsBytes<float>(samples).CopyTo(bytes);
        return new AudioFrame(bytes, sampleRate, channels, SampleFormat.F32,
            TimeSpan.Zero, TimeSpan.Zero, samples.Length / channels);
    }

    private static float[] ReadF32(AudioFrame f)
    {
        var s = MemoryMarshal.Cast<byte, float>(f.Data.Span);
        return s.ToArray();
    }

    private static float MaxAbs(float[] a)
    {
        float m = 0f;
        foreach (var v in a) m = Math.Max(m, Math.Abs(v));
        return m;
    }

    // ---- AU1 EqualizerEffect ----

    [Fact]
    public void Equalizer_Disabled_Passthrough_ReturnsSameFrame()
    {
        var eq = new EqualizerEffect { IsEnabled = false };
        var frame = MakeF32([0.1f, -0.2f, 0.3f], 1);
        var r = eq.Process(frame);
        Assert.Same(frame, r); // 禁用：不 Dispose、不新建
    }

    [Fact]
    public void Equalizer_ZeroGain_IsIdentity()
    {
        var eq = new EqualizerEffect(frequency: 1000f, gain: 0f, q: 0.707f);
        var input = new float[] { 0.1f, -0.25f, 0.5f, -0.75f, 0.9f };
        var frame = MakeF32(input, 1);
        var outFrame = eq.Process(frame);
        var output = ReadF32(outFrame);
        frame.Dispose();
        Assert.Equal(input.Length, output.Length);
        for (int i = 0; i < input.Length; i++)
            Assert.True(Math.Abs(output[i] - input[i]) < 1e-5f, $"sample {i}: {output[i]} vs {input[i]}");
    }

    [Fact]
    public void Equalizer_NegativeGain_ChangesSamples()
    {
        var eq = new EqualizerEffect(frequency: 1000f, gain: -12f, q: 0.707f);
        var input = new float[] { 0.1f, -0.25f, 0.5f, -0.75f, 0.9f };
        var frame = MakeF32(input, 1);
        var outFrame = eq.Process(frame);
        var output = ReadF32(outFrame);
        frame.Dispose();
        float maxDiff = 0f;
        for (int i = 0; i < input.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(output[i] - input[i]));
        Assert.True(maxDiff > 1e-3f, "负增益应改变采样");
    }

    // ---- AU2 ReverbEffect ----

    [Fact]
    public void Reverb_Disabled_Passthrough_ReturnsSameFrame()
    {
        var rv = new ReverbEffect { IsEnabled = false };
        var frame = MakeF32([0.1f, 0.2f], 1);
        var r = rv.Process(frame);
        Assert.Same(frame, r);
    }

    [Fact]
    public void Reverb_ZeroWet_DryPassthrough()
    {
        var rv = new ReverbEffect(reverbTime: 2f, wet: 0f, preDelay: 20f);
        var input = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
        var frame = MakeF32(input, 1);
        var outFrame = rv.Process(frame);
        var output = ReadF32(outFrame);
        frame.Dispose();
        Assert.Equal(input.Length, output.Length);
        for (int i = 0; i < input.Length; i++)
            Assert.True(Math.Abs(output[i] - input[i]) < 1e-5f, $"sample {i}: {output[i]} vs {input[i]}");
    }

    // ---- AU3 CompressorEffect ----

    [Fact]
    public void Compressor_Disabled_Passthrough_ReturnsSameFrame()
    {
        var cp = new CompressorEffect { IsEnabled = false };
        var frame = MakeF32([0.1f, 0.2f], 1);
        var r = cp.Process(frame);
        Assert.Same(frame, r);
    }

    [Fact]
    public void Compressor_RatioOne_IsIdentity()
    {
        var cp = new CompressorEffect(threshold: -100f, ratio: 1f, attack: 5f, release: 50f);
        var input = new float[] { 0.1f, -0.2f, 0.3f, -0.4f, 0.5f };
        var frame = MakeF32(input, 1);
        var outFrame = cp.Process(frame);
        var output = ReadF32(outFrame);
        frame.Dispose();
        for (int i = 0; i < input.Length; i++)
            Assert.True(Math.Abs(output[i] - input[i]) < 1e-5f, $"sample {i}: {output[i]} vs {input[i]}");
    }

    [Fact]
    public void Compressor_HighThreshold_ReducesPeak()
    {
        // 满幅正弦：0dB 信号远超 -20dB 阈值，ratio 4 → 应被压缩
        int n = 200;
        var input = new float[n];
        for (int i = 0; i < n; i++) input[i] = MathF.Sin(2f * MathF.PI * 1000f * i / 48000f);
        var cp = new CompressorEffect(threshold: -20f, ratio: 4f, attack: 1f, release: 50f, kneeWidth: 6f);
        var frame = MakeF32(input, 1);
        var outFrame = cp.Process(frame);
        var output = ReadF32(outFrame);
        frame.Dispose();

        // 跳过首个瞬态（envelope 尚未建立），检查稳态峰值已被压缩
        float maxSteady = 0f;
        for (int i = 10; i < n; i++) maxSteady = Math.Max(maxSteady, Math.Abs(output[i]));
        Assert.True(maxSteady < 0.5f, $"压缩后稳态峰值应 < 0.5，实际 {maxSteady}");
    }

    // ---- AU5 MixerChannel 环形缓冲 ----

    [Fact]
    public void MixerChannel_RingBuffer_PreservesOrder()
    {
        var ch = new MixerChannel("c");
        var frame = MakeF32([1f, 2f, 3f, 4f], 1);
        ch.Submit(frame);
        frame.Dispose();

        var out1 = new float[4];
        int read = ch.Read(out1);
        Assert.Equal(4, read);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, out1);

        var out2 = new float[4];
        Assert.Equal(0, ch.Read(out2)); // 已耗尽
    }

    [Fact]
    public void MixerChannel_RingBuffer_GrowsOnOverflow()
    {
        // 单次提交超过初始容量(1024)，验证 Grow 后顺序完整
        int n = 1500;
        var samples = new float[n];
        for (int i = 0; i < n; i++) samples[i] = i;
        var ch = new MixerChannel("c");
        ch.Submit(MakeF32(samples, 1));

        // 分两次读取，验证顺序
        var buf = new float[n];
        int total = 0;
        while (total < n)
        {
            var chunk = new float[500];
            int r = ch.Read(chunk);
            if (r == 0) break;
            Array.Copy(chunk, 0, buf, total, r);
            total += r;
        }
        Assert.Equal(n, total);
        for (int i = 0; i < n; i++) Assert.Equal(i, buf[i]);
    }

    // ---- AU6 AudioMixer 声道数转换 ----

    [Fact]
    public void AudioMixer_MonoToStereo_Duplicates()
    {
        var mixer = new AudioMixer(new MixerSettings { Channels = 2, SampleFormat = SampleFormat.F32, SampleRate = 48000 });
        var ch = mixer.CreateChannel("c");
        ch.Submit(MakeF32([0.5f], 1)); // 单声道，1 采样
        var outFrame = mixer.Mix(1);
        var out2 = ReadF32(outFrame); // 2 声道
        ch.Dispose();
        outFrame.Dispose();
        Assert.True(out2.Length == 2);
        Assert.True(Math.Abs(out2[0] - 0.5f) < 1e-6f);
        Assert.True(Math.Abs(out2[1] - 0.5f) < 1e-6f); // 复制到两个声道
    }

    [Fact]
    public void AudioMixer_StereoToMono_Averages()
    {
        var mixer = new AudioMixer(new MixerSettings { Channels = 1, SampleFormat = SampleFormat.F32, SampleRate = 48000 });
        var ch = mixer.CreateChannel("c");
        ch.Submit(MakeF32([1f, -1f], 2)); // 立体声，1 帧
        var outFrame = mixer.Mix(1);
        var out1 = ReadF32(outFrame);
        ch.Dispose();
        outFrame.Dispose();
        Assert.True(out1.Length == 1);
        Assert.True(Math.Abs(out1[0]) < 1e-6f); // 平均 (1 + -1)/2 = 0
    }

    [Fact]
    public void AudioMixer_TwoChannels_SummedWithVolume()
    {
        var mixer = new AudioMixer(new MixerSettings { Channels = 1, SampleFormat = SampleFormat.F32, SampleRate = 48000 });
        var a = mixer.CreateChannel("a"); a.Volume = 1f;
        var b = mixer.CreateChannel("b"); b.Volume = 1f;
        a.Submit(MakeF32([0.2f], 1));
        b.Submit(MakeF32([0.3f], 1));
        var outFrame = mixer.Mix(1);
        var out1 = ReadF32(outFrame);
        mixer.Dispose();
        outFrame.Dispose();
        Assert.True(out1.Length == 1);
        Assert.True(Math.Abs(out1[0] - 0.5f) < 1e-6f); // 0.2 + 0.3 = 0.5
    }
}

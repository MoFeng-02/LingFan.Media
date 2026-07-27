using System;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Audio;
using LingFan.Media.Audio.Effects;
using Xunit;

namespace LingFan.Media.Audio.Tests;

/// <summary>
/// V2-08.1 音频效果器 Seek 残留 Reset 桥接验证。
/// 核心不变量：<see cref="IAudioEffect.Reset"/> 后，效果器内部跨位置状态回到静默初始态，
/// 其输出必须与一个<b>全新实例</b>对相同输入的输出<b>逐帧逐样本一致</b>。
/// 这正是 Seek/Flush 后无音频瞬态/拖尾的正确性定义。
/// </summary>
public class EffectResetTests
{
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

    // 处理一帧（所有权转移：Process 内部 Dispose 输入帧并返回新帧），读出后 Dispose 输出帧。
    private static float[] ProcessFrame(IAudioEffect effect, float[] samples, int channels)
    {
        var frame = MakeF32(samples, channels);
        var outFrame = effect.Process(frame); // 内部 Dispose 输入帧
        var output = ReadF32(outFrame);
        outFrame.Dispose();
        return output;
    }

    [Theory]
    [InlineData(0)] // Equalizer（biquad 跨位置状态）
    [InlineData(1)] // Reverb（comb/allpass 延迟线 + 预延迟缓冲）
    [InlineData(2)] // Compressor（峰值包络 + 平滑增益）
    public void Effect_Reset_RestoresSilentState_LikeFreshInstance(int kind)
    {
        const int channels = 1;
        const int frameSamples = 64;
        const int warmupFrames = 30; // 充分预热，建立非平凡内部状态
        const int probeFrames = 8;

        IAudioEffect Make() => kind switch
        {
            0 => new EqualizerEffect(frequency: 1000f, gain: -6f, q: 1.0f),
            1 => new ReverbEffect(reverbTime: 2f, wet: 0.5f, preDelay: 30f),
            _ => new CompressorEffect(threshold: -20f, ratio: 4f, attack: 5f, release: 80f, kneeWidth: 6f),
        };

        // 预热信号：满幅阶跃（0.5 常量），确保各效果内部状态被充分驱动
        var warmup = new float[frameSamples];
        for (int i = 0; i < frameSamples; i++) warmup[i] = 0.5f;

        // 探测信号：与预热不同的正弦，便于暴露状态差异
        var probe = new float[frameSamples];
        for (int i = 0; i < frameSamples; i++)
            probe[i] = MathF.Sin(2f * MathF.PI * 440f * i / 48000f);

        var e1 = Make();
        for (int f = 0; f < warmupFrames; f++) ProcessFrame(e1, warmup, channels);

        e1.Reset(); // 关键：清除预热建立的状态

        var e2 = Make(); // 全新实例，处于静默初始态
        for (int f = 0; f < probeFrames; f++)
        {
            var o1 = ProcessFrame(e1, probe, channels); // Reset 后
            var o2 = ProcessFrame(e2, probe, channels); // 全新实例
            Assert.Equal(o1.Length, o2.Length);
            for (int i = 0; i < o1.Length; i++)
                Assert.True(Math.Abs(o1[i] - o2[i]) < 1e-6f,
                    $"kind={kind} frame={f} sample={i}: {o1[i]} vs {o2[i]}（Reset 后应与全新实例一致）");
        }
    }

    [Fact]
    public void AudioPipelineConfig_ResetEffects_NullWhenNoEffects()
    {
        var cfg = new AudioPipelineConfig();
        Assert.Null(cfg.ResetEffects());
    }

    [Fact]
    public void AudioPipelineConfig_ResetEffects_ResetsAllEffects()
    {
        var eq = new EqualizerEffect(frequency: 1000f, gain: -6f, q: 1.0f);
        var cp = new CompressorEffect(threshold: -20f, ratio: 4f, attack: 5f, release: 80f, kneeWidth: 6f);
        var cfg = new AudioPipelineConfig { Effects = new IAudioEffect[] { eq, cp } };

        // 预热两个效果器，建立状态
        var warm = new float[64];
        for (int i = 0; i < 64; i++) warm[i] = 0.5f;
        ProcessFrame(eq, warm, 1);
        ProcessFrame(cp, warm, 1);

        // 经桥接助手触发 Reset
        var reset = cfg.ResetEffects();
        Assert.NotNull(reset);
        reset!();

        // Reset 后的压缩器输出必须与全新实例一致
        var fresh = new CompressorEffect(threshold: -20f, ratio: 4f, attack: 5f, release: 80f, kneeWidth: 6f);
        var probe = new float[64];
        for (int i = 0; i < 64; i++) probe[i] = MathF.Sin(2f * MathF.PI * 440f * i / 48000f);

        var o1 = ProcessFrame(cp, probe, 1);
        var o2 = ProcessFrame(fresh, probe, 1);
        Assert.Equal(o1.Length, o2.Length);
        for (int i = 0; i < o1.Length; i++)
            Assert.True(Math.Abs(o1[i] - o2[i]) < 1e-6f,
                $"sample {i}: {o1[i]} vs {o2[i]}（ResetEffects 应清除压缩器包络/增益）");
    }
}

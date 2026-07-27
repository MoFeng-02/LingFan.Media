namespace LingFan.Media.Audio;

using System;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;

/// <summary>
/// PCM 字节 ↔ float[-1,1] 采样互转（Audio 程序集内共享，AOT 友好、无反射）。
/// 供效果器（Equalizer/Reverb/Compressor）与 MixerChannel 复用，避免重复实现。
/// </summary>
internal static class PcmConversions
{
    public static int BytesPerSample(SampleFormat fmt) => fmt switch
    {
        SampleFormat.S16 => 2,
        SampleFormat.S32 => 4,
        SampleFormat.F32 => 4,
        _ => 4,
    };

    /// <summary>将 PCM 字节解码为 float 采样（范围约 [-1, 1]）。</summary>
    /// <param name="pcm">对齐的 PCM 字节（长度 = sampleCount * BytesPerSample）。</param>
    /// <param name="fmt">采样格式。</param>
    /// <param name="dest">输出 float 缓冲（长度 = sampleCount）。</param>
    public static void DecodeToFloat(ReadOnlySpan<byte> pcm, SampleFormat fmt, Span<float> dest)
    {
        switch (fmt)
        {
            case SampleFormat.S16:
            {
                var s = MemoryMarshal.Cast<byte, short>(pcm);
                for (int i = 0; i < s.Length; i++) dest[i] = s[i] / 32768f;
                break;
            }
            case SampleFormat.S32:
            {
                var s = MemoryMarshal.Cast<byte, int>(pcm);
                for (int i = 0; i < s.Length; i++) dest[i] = s[i] / 2147483648f;
                break;
            }
            case SampleFormat.F32:
                MemoryMarshal.Cast<byte, float>(pcm).CopyTo(dest);
                break;
            default:
                MemoryMarshal.Cast<byte, float>(pcm).CopyTo(dest);
                break;
        }
    }

    /// <summary>将 float 采样编码为 PCM 字节（clamp 到格式范围）。</summary>
    /// <param name="samples">float 采样（范围约 [-1, 1]）。</param>
    /// <param name="fmt">目标采样格式。</param>
    /// <param name="dest">输出 PCM 字节（长度 = samples.Length * BytesPerSample）。</param>
    public static void EncodeFromFloat(ReadOnlySpan<float> samples, SampleFormat fmt, Span<byte> dest)
    {
        switch (fmt)
        {
            case SampleFormat.S16:
            {
                var d = MemoryMarshal.Cast<byte, short>(dest);
                for (int i = 0; i < samples.Length; i++)
                    d[i] = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
                break;
            }
            case SampleFormat.S32:
            {
                var d = MemoryMarshal.Cast<byte, int>(dest);
                for (int i = 0; i < samples.Length; i++)
                {
                    if (samples[i] >= 1f) d[i] = int.MaxValue;
                    else if (samples[i] <= -1f) d[i] = int.MinValue;
                    else d[i] = (int)(samples[i] * 2147483647f);
                }
                break;
            }
            case SampleFormat.F32:
                samples.CopyTo(MemoryMarshal.Cast<byte, float>(dest));
                break;
            default:
                samples.CopyTo(MemoryMarshal.Cast<byte, float>(dest));
                break;
        }
    }
}

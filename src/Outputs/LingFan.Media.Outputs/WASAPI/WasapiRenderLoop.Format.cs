using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

// WasapiRenderLoop 的 Format 职责段（部分类，由 WasapiRenderLoop.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Submit/Pause/Resume/时钟/Dispose/STA 线程控制）在主文件；
// 全部 COM 调用收在唯一 STA 渲染线程，本段方法均经 RunControl 封送或仅在该线程执行。
internal sealed partial class WasapiRenderLoop
{
    /// <summary>
    /// 构建指定格式的 WAVEFORMATEX 结构体。
    /// </summary>
    internal static WAVEFORMATEX BuildWaveFormat(int sampleRate, int channels, SampleFormat format)
    {
        ushort bitsPerSample = format switch
        {
            SampleFormat.S16 => 16,
            SampleFormat.S32 => 32,
            SampleFormat.F32 => 32,
            _ => 32
        };

        ushort formatTag = format switch
        {
            SampleFormat.F32 => WasapiInterop.WAVE_FORMAT_IEEE_FLOAT,
            _ => WasapiInterop.WAVE_FORMAT_PCM
        };

        return new WAVEFORMATEX
        {
            wFormatTag = formatTag,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = bitsPerSample,
            nBlockAlign = (ushort)(channels * (bitsPerSample / 8)),
            nAvgBytesPerSec = (uint)(sampleRate * channels * (bitsPerSample / 8)),
            cbSize = 0
        };
    }

    /// <summary>
    /// 从 WAVEFORMATEX 指针解析采样格式（支持 PCM / IEEE_FLOAT / EXTENSIBLE）。
    /// </summary>
    internal static SampleFormat ParseSampleFormat(IntPtr pFormat)
    {
        if (pFormat == IntPtr.Zero)
            return SampleFormat.F32;

        var wfx = Marshal.PtrToStructure<WAVEFORMATEX>(pFormat);

        if (wfx.wFormatTag == WasapiInterop.WAVE_FORMAT_IEEE_FLOAT)
            return SampleFormat.F32;

        if (wfx.wFormatTag == WasapiInterop.WAVE_FORMAT_PCM)
        {
            return wfx.wBitsPerSample switch
            {
                16 => SampleFormat.S16,
                32 => SampleFormat.S32,
                _ => SampleFormat.F32
            };
        }

        if (wfx.wFormatTag == WasapiInterop.WAVE_FORMAT_EXTENSIBLE && wfx.cbSize >= 22)
        {
            var wfex = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(pFormat);
            if (wfex.SubFormat == WasapiInterop.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                return SampleFormat.F32;
            if (wfex.SubFormat == WasapiInterop.KSDATAFORMAT_SUBTYPE_PCM)
            {
                return wfx.wBitsPerSample switch
                {
                    16 => SampleFormat.S16,
                    32 => SampleFormat.S32,
                    _ => SampleFormat.F32
                };
            }
        }

        return SampleFormat.F32;
    }

    /// <summary>
    /// 将源 PCM 数据拷贝或转换到 WASAPI 缓冲区。格式匹配时零转换直接拷贝。
    /// </summary>
    internal static unsafe void CopyOrConvert(
        ReadOnlySpan<byte> src, IntPtr dstPtr, int sampleCount,
        SampleFormat srcFormat, SampleFormat dstFormat)
    {
        if (srcFormat == dstFormat)
        {
            var dst = new Span<byte>((void*)dstPtr, sampleCount * GetBytesPerSample(dstFormat));
            src.CopyTo(dst);
            return;
        }

        if (dstFormat == SampleFormat.F32)
        {
            var dst = new Span<float>((void*)dstPtr, sampleCount);
            if (srcFormat == SampleFormat.S16)
            {
                var srcTyped = MemoryMarshal.Cast<byte, short>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = srcTyped[i] / 32768.0f;
            }
            else
            {
                var srcTyped = MemoryMarshal.Cast<byte, int>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = srcTyped[i] / 2147483648.0f;
            }
        }
        else if (dstFormat == SampleFormat.S16)
        {
            var dst = new Span<short>((void*)dstPtr, sampleCount);
            if (srcFormat == SampleFormat.F32)
            {
                var srcTyped = MemoryMarshal.Cast<byte, float>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = (short)Math.Clamp(srcTyped[i] * 32768f, -32768f, 32767f);
            }
            else
            {
                var srcTyped = MemoryMarshal.Cast<byte, int>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = (short)(srcTyped[i] >> 16);
            }
        }
        else
        {
            var dst = new Span<int>((void*)dstPtr, sampleCount);
            if (srcFormat == SampleFormat.F32)
            {
                var srcTyped = MemoryMarshal.Cast<byte, float>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = (int)Math.Clamp(srcTyped[i] * 2147483648.0, -2147483648.0, 2147483647.0);
            }
            else
            {
                var srcTyped = MemoryMarshal.Cast<byte, short>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = srcTyped[i] << 16;
            }
        }
    }

    /// <summary>获取采样格式的每样本字节数。</summary>
    internal static int GetBytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.S16 => 2,
        SampleFormat.S32 => 4,
        SampleFormat.F32 => 4,
        _ => 4
    };
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using LingFan.Media.Abstractions;
using LingFan.Media.Outputs.Wasapi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace LingFan.Media.Outputs.Tests;

/// <summary>
/// WasapiOutput V2-13 单元测试。
/// 测试格式解析（O9）、PCM 转换逻辑（O9）、选项配置（O7/O8）。
/// </summary>
/// <remarks>
/// WASAPI 设备交互测试需要真实音频设备（CI 跳过），本测试仅覆盖纯 CPU 逻辑。
/// 被测方法属于 [SupportedOSPlatform("windows")] 类，本测试类同样标注以消除 CA1416 警告。
/// </remarks>
[SupportedOSPlatform("windows")]
public class WasapiOutputTests
{
    // ── ParseSampleFormat 测试 ──

    [Fact]
    public void ParseSampleFormat_NullPointer_ReturnsF32()
    {
        var result = WasapiRenderLoop.ParseSampleFormat(IntPtr.Zero);
        result.Should().Be(SampleFormat.F32);
    }

    [Fact]
    public void ParseSampleFormat_IeeeFloat_ReturnsF32()
    {
        var wfx = new WAVEFORMATEX
        {
            wFormatTag = WasapiInterop.WAVE_FORMAT_IEEE_FLOAT,
            nChannels = 2,
            nSamplesPerSec = 48000,
            wBitsPerSample = 32,
            nBlockAlign = 8,
            nAvgBytesPerSec = 48000 * 8,
            cbSize = 0
        };

        using var ptr = AllocFormatPtr(wfx);
        var result = WasapiRenderLoop.ParseSampleFormat(ptr);
        result.Should().Be(SampleFormat.F32);
    }

    [Fact]
    public void ParseSampleFormat_Pcm16_ReturnsS16()
    {
        var wfx = new WAVEFORMATEX
        {
            wFormatTag = WasapiInterop.WAVE_FORMAT_PCM,
            nChannels = 2,
            nSamplesPerSec = 44100,
            wBitsPerSample = 16,
            nBlockAlign = 4,
            nAvgBytesPerSec = 44100 * 4,
            cbSize = 0
        };

        using var ptr = AllocFormatPtr(wfx);
        var result = WasapiRenderLoop.ParseSampleFormat(ptr);
        result.Should().Be(SampleFormat.S16);
    }

    [Fact]
    public void ParseSampleFormat_Pcm32_ReturnsS32()
    {
        var wfx = new WAVEFORMATEX
        {
            wFormatTag = WasapiInterop.WAVE_FORMAT_PCM,
            nChannels = 2,
            nSamplesPerSec = 48000,
            wBitsPerSample = 32,
            nBlockAlign = 8,
            nAvgBytesPerSec = 48000 * 8,
            cbSize = 0
        };

        using var ptr = AllocFormatPtr(wfx);
        var result = WasapiRenderLoop.ParseSampleFormat(ptr);
        result.Should().Be(SampleFormat.S32);
    }

    [Fact]
    public void ParseSampleFormat_ExtensibleIeeeFloat_ReturnsF32()
    {
        var wfex = new WAVEFORMATEXTENSIBLE
        {
            Format = new WAVEFORMATEX
            {
                wFormatTag = WasapiInterop.WAVE_FORMAT_EXTENSIBLE,
                nChannels = 2,
                nSamplesPerSec = 48000,
                wBitsPerSample = 32,
                nBlockAlign = 8,
                nAvgBytesPerSec = 48000 * 8,
                cbSize = 22
            },
            wValidBitsPerSample = 32,
            dwChannelMask = 0x3, // FL+FR
            SubFormat = WasapiInterop.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        };

        using var ptr = AllocExtensiblePtr(wfex);
        var result = WasapiRenderLoop.ParseSampleFormat(ptr);
        result.Should().Be(SampleFormat.F32);
    }

    [Fact]
    public void ParseSampleFormat_ExtensiblePcm16_ReturnsS16()
    {
        var wfex = new WAVEFORMATEXTENSIBLE
        {
            Format = new WAVEFORMATEX
            {
                wFormatTag = WasapiInterop.WAVE_FORMAT_EXTENSIBLE,
                nChannels = 2,
                nSamplesPerSec = 44100,
                wBitsPerSample = 16,
                nBlockAlign = 4,
                nAvgBytesPerSec = 44100 * 4,
                cbSize = 22
            },
            wValidBitsPerSample = 16,
            dwChannelMask = 0x3,
            SubFormat = WasapiInterop.KSDATAFORMAT_SUBTYPE_PCM
        };

        using var ptr = AllocExtensiblePtr(wfex);
        var result = WasapiRenderLoop.ParseSampleFormat(ptr);
        result.Should().Be(SampleFormat.S16);
    }

    [Fact]
    public void ParseSampleFormat_ExtensiblePcm32_ReturnsS32()
    {
        var wfex = new WAVEFORMATEXTENSIBLE
        {
            Format = new WAVEFORMATEX
            {
                wFormatTag = WasapiInterop.WAVE_FORMAT_EXTENSIBLE,
                nChannels = 2,
                nSamplesPerSec = 48000,
                wBitsPerSample = 32,
                nBlockAlign = 8,
                nAvgBytesPerSec = 48000 * 8,
                cbSize = 22
            },
            wValidBitsPerSample = 32,
            dwChannelMask = 0x3,
            SubFormat = WasapiInterop.KSDATAFORMAT_SUBTYPE_PCM
        };

        using var ptr = AllocExtensiblePtr(wfex);
        var result = WasapiRenderLoop.ParseSampleFormat(ptr);
        result.Should().Be(SampleFormat.S32);
    }

    [Fact]
    public void ParseSampleFormat_UnknownFormatTag_ReturnsF32()
    {
        var wfx = new WAVEFORMATEX
        {
            wFormatTag = 0x9999, // Unknown
            nChannels = 2,
            nSamplesPerSec = 48000,
            wBitsPerSample = 32,
            nBlockAlign = 8,
            nAvgBytesPerSec = 48000 * 8,
            cbSize = 0
        };

        using var ptr = AllocFormatPtr(wfx);
        var result = WasapiRenderLoop.ParseSampleFormat(ptr);
        result.Should().Be(SampleFormat.F32);
    }

    // ── BuildWaveFormat 测试 ──

    [Fact]
    public void BuildWaveFormat_F32_CorrectStructure()
    {
        var wfx = WasapiRenderLoop.BuildWaveFormat(48000, 2, SampleFormat.F32);

        wfx.wFormatTag.Should().Be(WasapiInterop.WAVE_FORMAT_IEEE_FLOAT);
        wfx.nChannels.Should().Be(2);
        wfx.nSamplesPerSec.Should().Be(48000u);
        wfx.wBitsPerSample.Should().Be(32);
        wfx.nBlockAlign.Should().Be(8); // 2ch * 4 bytes
        wfx.nAvgBytesPerSec.Should().Be(48000u * 8);
        wfx.cbSize.Should().Be(0);
    }

    [Fact]
    public void BuildWaveFormat_S16_CorrectStructure()
    {
        var wfx = WasapiRenderLoop.BuildWaveFormat(44100, 2, SampleFormat.S16);

        wfx.wFormatTag.Should().Be(WasapiInterop.WAVE_FORMAT_PCM);
        wfx.nChannels.Should().Be(2);
        wfx.nSamplesPerSec.Should().Be(44100u);
        wfx.wBitsPerSample.Should().Be(16);
        wfx.nBlockAlign.Should().Be(4); // 2ch * 2 bytes
        wfx.nAvgBytesPerSec.Should().Be(44100u * 4);
        wfx.cbSize.Should().Be(0);
    }

    [Fact]
    public void BuildWaveFormat_S32_CorrectStructure()
    {
        var wfx = WasapiRenderLoop.BuildWaveFormat(48000, 6, SampleFormat.S32);

        wfx.wFormatTag.Should().Be(WasapiInterop.WAVE_FORMAT_PCM);
        wfx.nChannels.Should().Be(6);
        wfx.nSamplesPerSec.Should().Be(48000u);
        wfx.wBitsPerSample.Should().Be(32);
        wfx.nBlockAlign.Should().Be(24); // 6ch * 4 bytes
        wfx.nAvgBytesPerSec.Should().Be(48000u * 24);
        wfx.cbSize.Should().Be(0);
    }

    // ── CopyOrConvert 测试 ──

    [Fact]
    public unsafe void CopyOrConvert_F32ToF32_DirectCopy()
    {
        // 4 samples of F32 data
        float[] srcFloats = [0.5f, -0.5f, 1.0f, -1.0f];
        var src = MemoryMarshal.AsBytes(srcFloats.AsSpan()).ToArray();
        var dst = new byte[src.Length];

        fixed (byte* dstPtr = dst)
        {
            WasapiRenderLoop.CopyOrConvert(src, (IntPtr)dstPtr, 4, SampleFormat.F32, SampleFormat.F32);
        }

        var dstFloats = MemoryMarshal.Cast<byte, float>(dst);
        dstFloats.ToArray().Should().Equal(srcFloats);
    }

    [Fact]
    public unsafe void CopyOrConvert_S16ToS16_DirectCopy()
    {
        short[] srcShorts = [100, -100, 32767, -32768];
        var src = MemoryMarshal.AsBytes(srcShorts.AsSpan()).ToArray();
        var dst = new byte[src.Length];

        fixed (byte* dstPtr = dst)
        {
            WasapiRenderLoop.CopyOrConvert(src, (IntPtr)dstPtr, 4, SampleFormat.S16, SampleFormat.S16);
        }

        var dstShorts = MemoryMarshal.Cast<byte, short>(dst);
        dstShorts.ToArray().Should().Equal(srcShorts);
    }

    [Fact]
    public unsafe void CopyOrConvert_S32ToS32_DirectCopy()
    {
        int[] srcInts = [1000000, -1000000, int.MaxValue, int.MinValue];
        var src = MemoryMarshal.AsBytes(srcInts.AsSpan()).ToArray();
        var dst = new byte[src.Length];

        fixed (byte* dstPtr = dst)
        {
            WasapiRenderLoop.CopyOrConvert(src, (IntPtr)dstPtr, 4, SampleFormat.S32, SampleFormat.S32);
        }

        var dstInts = MemoryMarshal.Cast<byte, int>(dst);
        dstInts.ToArray().Should().Equal(srcInts);
    }

    [Fact]
    public unsafe void CopyOrConvert_S16ToF32_CorrectConversion()
    {
        // S16: 32767 → F32: ~1.0, -32768 → F32: -1.0, 0 → 0
        short[] srcShorts = [32767, -32768, 0, 16384];
        var src = MemoryMarshal.AsBytes(srcShorts.AsSpan()).ToArray();
        var dst = new byte[4 * 4]; // 4 samples * 4 bytes (F32)

        fixed (byte* dstPtr = dst)
        {
            WasapiRenderLoop.CopyOrConvert(src, (IntPtr)dstPtr, 4, SampleFormat.S16, SampleFormat.F32);
        }

        var dstFloats = MemoryMarshal.Cast<byte, float>(dst).ToArray();
        dstFloats[0].Should().BeApproximately(32767f / 32768f, 0.0001f);  // ~1.0
        dstFloats[1].Should().BeApproximately(-32768f / 32768f, 0.0001f); // -1.0
        dstFloats[2].Should().Be(0f);
        dstFloats[3].Should().BeApproximately(16384f / 32768f, 0.0001f);  // ~0.5
    }

    [Fact]
    public unsafe void CopyOrConvert_S32ToF32_CorrectConversion()
    {
        int[] srcInts = [int.MaxValue, int.MinValue, 0, int.MaxValue / 2];
        var src = MemoryMarshal.AsBytes(srcInts.AsSpan()).ToArray();
        var dst = new byte[4 * 4];

        fixed (byte* dstPtr = dst)
        {
            WasapiRenderLoop.CopyOrConvert(src, (IntPtr)dstPtr, 4, SampleFormat.S32, SampleFormat.F32);
        }

        var dstFloats = MemoryMarshal.Cast<byte, float>(dst).ToArray();
        dstFloats[0].Should().BeApproximately(1.0f, 0.0001f);   // int.MaxValue / 2^31 ≈ 1.0
        dstFloats[1].Should().BeApproximately(-1.0f, 0.0001f);  // int.MinValue / 2^31 = -1.0
        dstFloats[2].Should().Be(0f);
        dstFloats[3].Should().BeApproximately(0.5f, 0.0001f);
    }

    [Fact]
    public unsafe void CopyOrConvert_F32ToS16_CorrectConversionWithClamping()
    {
        // F32 values: normal, out-of-range (should clamp), zero
        float[] srcFloats = [0.5f, 1.5f, -1.5f, 0f];
        var src = MemoryMarshal.AsBytes(srcFloats.AsSpan()).ToArray();
        var dst = new byte[4 * 2]; // 4 samples * 2 bytes (S16)

        fixed (byte* dstPtr = dst)
        {
            WasapiRenderLoop.CopyOrConvert(src, (IntPtr)dstPtr, 4, SampleFormat.F32, SampleFormat.S16);
        }

        var dstShorts = MemoryMarshal.Cast<byte, short>(dst).ToArray();
        // 审计修复：缩放因子改为 32768（与 S16→F32 的 1/32768 对称）
        dstShorts[0].Should().Be((short)(0.5f * 32768f));   // 16384
        dstShorts[1].Should().Be(32767);                     // clamped
        dstShorts[2].Should().Be(-32768);                    // clamped
        dstShorts[3].Should().Be(0);
    }

    [Fact]
    public unsafe void CopyOrConvert_F32ToS32_CorrectConversionWithClamping()
    {
        float[] srcFloats = [0.5f, 1.5f, -1.5f, 0f];
        var src = MemoryMarshal.AsBytes(srcFloats.AsSpan()).ToArray();
        var dst = new byte[4 * 4];

        fixed (byte* dstPtr = dst)
        {
            WasapiRenderLoop.CopyOrConvert(src, (IntPtr)dstPtr, 4, SampleFormat.F32, SampleFormat.S32);
        }

        var dstInts = MemoryMarshal.Cast<byte, int>(dst).ToArray();
        // 审计修复：使用 double 字面量避免 float 精度问题（2147483647f 实际为 2^31 导致溢出）
        dstInts[0].Should().Be((int)(0.5 * 2147483648.0));    // 1073741824
        dstInts[1].Should().Be(int.MaxValue);    // clamped
        dstInts[2].Should().Be(int.MinValue);    // clamped (note: -1.5 * max → negative, clamped to MinValue)
        dstInts[3].Should().Be(0);
    }

    [Fact]
    public unsafe void CopyOrConvert_S16ToS32_CorrectConversion()
    {
        short[] srcShorts = [32767, -32768, 0, 16384];
        var src = MemoryMarshal.AsBytes(srcShorts.AsSpan()).ToArray();
        var dst = new byte[4 * 4];

        fixed (byte* dstPtr = dst)
        {
            WasapiRenderLoop.CopyOrConvert(src, (IntPtr)dstPtr, 4, SampleFormat.S16, SampleFormat.S32);
        }

        var dstInts = MemoryMarshal.Cast<byte, int>(dst).ToArray();
        // S16 → S32: left shift by 16
        dstInts[0].Should().Be(32767 << 16);
        dstInts[1].Should().Be(-32768 << 16);
        dstInts[2].Should().Be(0);
        dstInts[3].Should().Be(16384 << 16);
    }

    [Fact]
    public unsafe void CopyOrConvert_S32ToS16_CorrectConversion()
    {
        int[] srcInts = [int.MaxValue, int.MinValue, 0, 1 << 16];
        var src = MemoryMarshal.AsBytes(srcInts.AsSpan()).ToArray();
        var dst = new byte[4 * 2];

        fixed (byte* dstPtr = dst)
        {
            WasapiRenderLoop.CopyOrConvert(src, (IntPtr)dstPtr, 4, SampleFormat.S32, SampleFormat.S16);
        }

        var dstShorts = MemoryMarshal.Cast<byte, short>(dst).ToArray();
        // S32 → S16: right shift by 16
        dstShorts[0].Should().Be((short)(int.MaxValue >> 16));   // 32767
        dstShorts[1].Should().Be((short)(int.MinValue >> 16));   // -32768
        dstShorts[2].Should().Be(0);
        dstShorts[3].Should().Be((short)1);
    }

    [Fact]
    public unsafe void CopyOrConvert_F32ToS32_BoundaryOneDoesNotOverflow()
    {
        // 审计回归测试：1.0f 输入不应溢出为 int.MinValue
        // 旧代码用 2147483647f（实际为 2^31）作乘数，Math.Clamp 上界也是 2^31，
        // 导致 (int)2147483648.0f 在 unchecked 上下文溢出为 int.MinValue。
        // 修复后用 double 字面量，Math.Clamp 返回 2147483647.0 (double)，(int) 正确为 int.MaxValue。
        float[] srcFloats = [1.0f, -1.0f, 0.999999f, -0.999999f];
        var src = MemoryMarshal.AsBytes(srcFloats.AsSpan()).ToArray();
        var dst = new byte[4 * 4];

        fixed (byte* dstPtr = dst)
        {
            WasapiRenderLoop.CopyOrConvert(src, (IntPtr)dstPtr, 4, SampleFormat.F32, SampleFormat.S32);
        }

        var dstInts = MemoryMarshal.Cast<byte, int>(dst).ToArray();
        dstInts[0].Should().Be(int.MaxValue);   // 1.0 * 2^31 = 2^31, clamped to int.MaxValue
        dstInts[1].Should().Be(int.MinValue);   // -1.0 * 2^31 = -2^31, clamped to int.MinValue
        dstInts[2].Should().BeLessThan(int.MaxValue);   // 0.999999 * 2^31 < int.MaxValue
        dstInts[3].Should().BeGreaterThan(int.MinValue); // -0.999999 * 2^31 > int.MinValue
    }

    // ── WasapiOptions 测试 ──

    [Fact]
    public void WasapiOptions_DefaultValues_V2()
    {
        var options = new WasapiOptions();

        options.ExclusiveMode.Should().BeFalse();
        options.EventDrivenMode.Should().BeTrue();       // V2 默认事件驱动
        options.PreferredSampleFormat.Should().BeNull(); // V2 默认自动检测
        // 1d1b07f 起默认由 50ms 调整为 100ms：共享模式下兼顾播放稳定性与 A/V 同步。
        options.BufferDuration.Should().Be(TimeSpan.FromMilliseconds(100));
        options.SampleRate.Should().Be(44100);
        options.Channels.Should().Be(2);
    }

    [Fact]
    public void WasapiOptions_CustomValues_V2()
    {
        var options = new WasapiOptions
        {
            ExclusiveMode = true,
            EventDrivenMode = false,         // 回退 V1 轮询
            PreferredSampleFormat = SampleFormat.S32,
            BufferDuration = TimeSpan.FromMilliseconds(10),
            SampleRate = 48000,
            Channels = 6
        };

        options.ExclusiveMode.Should().BeTrue();
        options.EventDrivenMode.Should().BeFalse();
        options.PreferredSampleFormat.Should().Be(SampleFormat.S32);
        options.BufferDuration.Should().Be(TimeSpan.FromMilliseconds(10));
        options.SampleRate.Should().Be(48000);
        options.Channels.Should().Be(6);
    }

    [Fact]
    public void WasapiOptions_EventDrivenMode_RoundTrip()
    {
        // 验证 EventDrivenMode 可正确设置和读取
        var options = new WasapiOptions { EventDrivenMode = true };
        options.EventDrivenMode.Should().BeTrue();

        options.EventDrivenMode = false;
        options.EventDrivenMode.Should().BeFalse();
    }

    [Fact]
    public void WasapiOptions_PreferredSampleFormat_AllFormats()
    {
        // 验证所有 SampleFormat 都可设置
        foreach (var format in Enum.GetValues<SampleFormat>())
        {
            var options = new WasapiOptions { PreferredSampleFormat = format };
            options.PreferredSampleFormat.Should().Be(format);
        }

        // 验证可重置为 null
        var opts = new WasapiOptions { PreferredSampleFormat = SampleFormat.S16 };
        opts.PreferredSampleFormat = null;
        opts.PreferredSampleFormat.Should().BeNull();
    }

    // ── 辅助方法 ──

    /// <summary>
    /// 分配非托管内存并写入 WAVEFORMATEX 结构体。
    /// 调用方负责释放（通过 using 模式）。
    /// </summary>
    private static FormatPtr AllocFormatPtr(WAVEFORMATEX wfx)
    {
        int size = Marshal.SizeOf<WAVEFORMATEX>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(wfx, ptr, false);
        return new FormatPtr(ptr);
    }

    /// <summary>
    /// 分配非托管内存并写入 WAVEFORMATEXTENSIBLE 结构体。
    /// </summary>
    private static FormatPtr AllocExtensiblePtr(WAVEFORMATEXTENSIBLE wfex)
    {
        int size = Marshal.SizeOf<WAVEFORMATEXTENSIBLE>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(wfex, ptr, false);
        return new FormatPtr(ptr);
    }

    /// <summary>
    /// 非托管格式指针的 IDisposable 包装。
    /// </summary>
    private sealed class FormatPtr(IntPtr ptr) : IDisposable
    {
        public IntPtr Pointer { get; } = ptr;
        public static implicit operator IntPtr(FormatPtr fp) => fp.Pointer;

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WAVEFORMATEX>(Pointer);
                Marshal.FreeHGlobal(Pointer);
            }
        }
    }

    // ── 无头冒烟测试（V2-13 W1 待开发）──

    /// <summary>
    /// 无 UI 环境下 WASAPI 生命周期冒烟：InitializeAsync → Initialize → Submit(静音帧) → Dispose。
    /// WASAPI 使用默认音频端点（无需窗口句柄），故无头环境可初始化；
    /// 当无音频端点、或设备不支持请求格式（CI 容器/无声卡/受限声卡）时跳过，而非失败。
    /// </summary>
    [Fact]
    public async Task Headless_InitializeSubmitDispose_DefaultEndpoint()
    {
        var output = new WasapiOutput(new WasapiOptions(), NullLogger<WasapiOutput>.Instance);

        try
        {
            await output.InitializeAsync(TestContext.Current.CancellationToken);
            output.Initialize(44100, 2);
        }
        catch (Exception ex) when (
            ex is COMException
            or InvalidOperationException
            or PlatformNotSupportedException
            or NotSupportedException)
        {
            // 无音频端点 / 设备不支持请求格式（无头/CI/受限声卡环境）→ 跳过，
            // 不在 CI 中制造虚假失败
            Assert.Skip(
                $"无音频端点或设备不支持请求格式（无头/CI 环境），跳过 WASAPI 无头冒烟测试：{ex.Message}");
        }

        try
        {
            // 1 帧静音 F32（2 声道 × 4 字节）
            var silent = new byte[2 * 4];
            var frame = new AudioFrame(
                silent, 44100, 2, SampleFormat.F32, TimeSpan.Zero, TimeSpan.Zero, 1);
            output.Submit(frame);
        }
        finally
        {
            output.Dispose();
        }
    }
}

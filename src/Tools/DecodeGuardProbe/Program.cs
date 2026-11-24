using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Backends.FFmpeg.Decoders;
using Microsoft.Extensions.DependencyInjection;
using LingFan.Media.GPUShare.D3D11;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace DecodeGuardProbe;

/// <summary>
/// 解码设备对齐守卫全链路探针：验证 FFmpeg 解码器「渲染侧对齐提示 → 能力探测 → 采纳/回落」完整选卡路径。
/// 用 WARP（软件适配器，无硬件解码引擎）构造「提示指向无引擎设备」场景——此前仅探测函数被单测过，
/// 本探针把守卫放进真实解码器初始化链路验证。
/// </summary>
/// <remarks>
/// 场景 A（守卫拒绝）：假生产者提示 WARP LUID → 解码器应拒绝采纳、回落独显优先、硬解仍激活。
/// 场景 B（守卫采纳）：假生产者提示独显 LUID → 解码器应采纳提示、硬解激活。
/// 判据：日志选卡分支 + IsHardwareAccelerated + 无异常。
/// </remarks>
internal static class Program
{
    private static int Main()
    {
        using var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(LogLevel.Information));
        var logger = loggerFactory.CreateLogger("DecodeGuardProbe");

        // WARP LUID：经 IDXGIDevice.GetAdapter 取（WARP 不在 DXGI 默认枚举，须直建设备取身份）
        byte[]? warpLuid = null;
        try
        {
            using var warp = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport);
            using var dxgi = warp.QueryInterface<Vortice.DXGI.IDXGIDevice>();
            dxgi.GetAdapter(out var adapter);
            using (adapter)
            {
                using var a1 = adapter!.QueryInterface<Vortice.DXGI.IDXGIAdapter1>();
                var desc = a1.Description1;
                warpLuid = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(warpLuid.AsSpan(0), desc.Luid.LowPart);
                BinaryPrimitives.WriteInt32LittleEndian(warpLuid.AsSpan(4), desc.Luid.HighPart);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GUARD-PROBE] WARP 不可用（{ex.GetType().Name}），场景 A 跳过");
        }

        var preferred = D3D11AdapterLuid.QueryPreferredAdapter();
        if (preferred is null)
        {
            Console.WriteLine("[GUARD-PROBE] FAIL: 无首选适配器");
            return 1;
        }
        Console.WriteLine($"[GUARD-PROBE] 首选适配器: {preferred.Value.Description} luid={Convert.ToHexString(preferred.Value.Luid)}");

        int failures = 0;
        var options = new FFmpegOptions { FFmpegLibraryPath = AppContext.BaseDirectory, HardwareAcceleration = true };

        // 场景 A：提示指向 WARP（无解码引擎）→ 守卫应拒绝，回落独显
        if (warpLuid != null)
        {
            Console.WriteLine("[GUARD-PROBE] === 场景 A：提示=WARP（无引擎），期望拒绝采纳回落独显 ===");
            var producer = new FakeProducer(GPUApiType.OpenGL, warpLuid);
            var factory = new FFmpegVideoDecoderFactory(loggerFactory, new FakeGpuContext(GPUApiType.OpenGL), options, new[] { (IGpuFrameProducer)producer });
            var decoder = factory.Create(VideoCodec.H264, new VideoSettings { EnableHardwareAcceleration = true });
            try
            {
                bool ok = decoder.IsHardwareAccelerated;
                Console.WriteLine($"[GUARD-PROBE] 场景 A: IsHardwareAccelerated={ok} => {(ok ? "PASS（守卫拒绝 WARP 后独显硬解仍激活）" : "FAIL（硬解丢失）")}");
                if (!ok) failures++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GUARD-PROBE] 场景 A FAIL: {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
            finally { decoder.Dispose(); }
        }

        // 场景 B：提示指向独显（有引擎）→ 守卫应采纳
        Console.WriteLine("[GUARD-PROBE] === 场景 B：提示=独显（有引擎），期望采纳 ===");
        var producerB = new FakeProducer(GPUApiType.OpenGL, preferred.Value.Luid);
        var factoryB = new FFmpegVideoDecoderFactory(loggerFactory, new FakeGpuContext(GPUApiType.OpenGL), options, new[] { (IGpuFrameProducer)producerB });
        var decoderB = factoryB.Create(VideoCodec.H264, new VideoSettings { EnableHardwareAcceleration = true });
        try
        {
            bool ok = decoderB.IsHardwareAccelerated;
            Console.WriteLine($"[GUARD-PROBE] 场景 B: IsHardwareAccelerated={ok} => {(ok ? "PASS" : "FAIL")}");
            if (!ok) failures++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GUARD-PROBE] 场景 B FAIL: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally { decoderB.Dispose(); }

        Console.WriteLine($"[GUARD-PROBE] => {(failures == 0 ? "总体 PASS" : "总体 FAIL")}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>假 GPU 上下文：只声明 ApiType（非 D3D11 即可触发 ffmpeg 自有设备分支），句柄恒零。</summary>
    private sealed class FakeGpuContext : IGpuDeviceContext
    {
        public FakeGpuContext(GPUApiType api) { ApiType = api; }
        public GPUApiType ApiType { get; }
        public nint DeviceHandle => nint.Zero;
        public nint ContextHandle => nint.Zero;
        public object? SharedDevice => null;
        public object? SharedPhysicalDevice => null;
        public uint VideoQueueFamilyIndex => uint.MaxValue;
        public uint GraphicsQueueFamilyIndex => uint.MaxValue;
        public bool IsInitialized => true;
        public Task InitializeAsync(System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public GpuDeviceCapabilities GetCapabilities() => new("fake", 0, 0, 0, false, false, -1);
    }

    /// <summary>假帧生产者：只携带对齐提示（TryImport 恒 false——本探针不喂帧，只验选卡）。</summary>
    private sealed class FakeProducer : IGpuFrameProducer
    {
        private readonly byte[]? _hint;
        public FakeProducer(GPUApiType api, byte[]? hint) { ApiType = api; _hint = hint; }
        public GPUApiType ApiType { get; }
        public byte[]? DecodeAdapterLuidHint => _hint;
        public bool TryImport(GpuFrameImportSource source, out IGpuTextureResource? texture)
        {
            texture = null;
            return false;
        }
        public bool IsImportSupported(GpuFrameImportKind kind) => false;
    }
}
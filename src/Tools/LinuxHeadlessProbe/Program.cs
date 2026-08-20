using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Threading;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinuxHeadlessProbe;

/// <summary>
/// Linux/WSL2 无头探针（Phase 1）：仅验证「ffmpeg 后端 + 生产管线 + 帧流转」能在 Linux 上跑通。
/// 不建 GPU 设备、不渲染上屏（NoOp 无头渲染器），把变量收敛到「ffmpeg 原生库能否在 Linux 加载 + 管线是否通」。
/// 渲染路径（OpenGL/Vulkan 无头零拷贝）见 Phase 2，待补 offscreen present 接线。
/// </summary>
/// <remarks>
/// <para>用法（在 WSL2 / Linux 内执行）：</para>
/// <para>1) 设置 ffmpeg 原生库目录环境变量 LF_FFMPEG_LIB 指向 BtbN 解包后的 lib 目录；</para>
/// <para>2) 将该目录加入 LD_LIBRARY_PATH；</para>
/// <para>3) 用 dotnet run 启动本探针，参数前缀用两个短横线，例如 --file /abs/video.mp4 --seconds 12 -v。</para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? file = ParseOption(args, "--file") ?? "Resources/Video/m1.mp4";
        bool verbose = args.Contains("-v") || args.Contains("--verbose");
        bool useHw = args.Contains("--hw");
        double seconds = ParseDouble(args, "--seconds") ?? 8.0;
        int sample = (int)(ParseDouble(args, "--sample") ?? 25);
        int saveFrames = (int)(ParseDouble(args, "--save-frames") ?? 0);
        string saveDir = ParseOption(args, "--save-dir")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "TestInfo", "Diagnostics", "LinuxHeadlessProbe");
        // ffmpeg 原生库目录：优先 env LF_FFMPEG_LIB，否则应用目录（配合 LD_LIBRARY_PATH）。
        string ffmpegLib = Environment.GetEnvironmentVariable("LF_FFMPEG_LIB") ?? AppContext.BaseDirectory;

        Console.WriteLine("=== LingFan.Media Linux 无头探针（ffmpeg 解码 + 无头 NoOp 渲染 + 静音）===");
        if (!File.Exists(file))
        {
            Console.WriteLine($"找不到媒体文件：{file}（用 --file 指定绝对路径）");
            return 2;
        }
        Console.WriteLine($"媒体文件      : {file}");
        Console.WriteLine($"ffmpeg 库目录 : {ffmpegLib}");
        Console.WriteLine($"硬解/零拷贝   : {(useHw ? "请求(--hw)；Linux 解码侧 VAAPI 为 Phase 2 桩，当前回落软解并打印告警" : "关（软解软渲）")}");
        Console.WriteLine($"渲染器        : NoOp 无头（视频帧经事件出餐，不建 GPU 设备）");
        Console.WriteLine($"音频输出      : 静音（仅数据出餐）");
        Console.WriteLine($"日志级别      : {(verbose ? "Debug" : "Information")}");
        Console.WriteLine($"帧落盘        : {(saveFrames > 0 ? $"每 {saveFrames} 帧 -> {saveDir}" : "关闭（加 --save-frames N 开启，均匀抽帧成 PNG 判读解码正确性）")}");
        Console.WriteLine();

        var services = new ServiceCollection();
        services.AddLogging(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        // 🔑 跨平台三人组：ffmpeg 解码 + 无头 NoOp 渲染 + 静音输出。全部 net10.0，Linux 可直接运行。
        services.AddLingFanMedia()
                .AddFFmpeg(o =>
                {
                    o.FFmpegLibraryPath = ffmpegLib;
                    o.HardwareAcceleration = useHw;
                })
                .AddHeadlessRenderer()
                .AddSilentAudioOutput();

        await using var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        long videoFrames = 0, audioCallbacks = 0, audioSamples = 0;
        int sampleRate = 0;
        long gpuServed = 0, cpuServed = 0;
        long savedFrames = 0;

        void OnVideo(VideoFrame f)
        {
            Interlocked.Increment(ref videoFrames);
            if (f.Resource is IGpuTextureResource) Interlocked.Increment(ref gpuServed);
            else Interlocked.Increment(ref cpuServed);
            if (videoFrames % sample == 0)
            {
                string res = f.Resource is IGpuTextureResource ? "GPU(零拷贝句柄)" : "CPU(软解内存)";
                Console.WriteLine($"  [抽样#{videoFrames}] t={f.Timestamp:g} {f.Width}x{f.Height} fmt={f.Format} 资源={res}");
            }
            if (saveFrames > 0 && videoFrames % saveFrames == 0)
            {
                FrameDumper.DumpFrame(f, (int)videoFrames, saveDir);
                Interlocked.Increment(ref savedFrames);
            }
        }
        void OnAudio(AudioFrame f)
        {
            Interlocked.Increment(ref audioCallbacks);
            Interlocked.Add(ref audioSamples, f.FrameCount);
            if (sampleRate == 0) sampleRate = f.SampleRate;
        }

        player.VideoFrameAvailable += OnVideo;
        player.AudioDataAvailable += OnAudio;
        player.ErrorOccurred += (_, e) => Console.WriteLine($"[错误] {e.Message}");

        try
        {
            await player.OpenAsync(new FileMediaSource(file));
        }
        catch (MediaBackendUnsupportedException ex)
        {
            Console.WriteLine($"[失败] {ex.Message}");
            return 3;
        }

        Console.WriteLine($"时长={player.Duration:g} 状态={player.State}");
        await player.PlayAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            while (!cts.IsCancellationRequested && player.State == MediaState.Playing)
            {
                await Task.Delay(500, cts.Token);
                Console.WriteLine($"  t={player.Position:g} 视频帧={videoFrames} 音频回调={audioCallbacks}");
            }
        }
        catch (OperationCanceledException) { /* 时间到 */ }
        await player.StopAsync();
        await player.DisposeAsync();

        Console.WriteLine();
        Console.WriteLine("=== 汇总 ===");
        Console.WriteLine($"视频帧数      : {videoFrames}");
        Console.WriteLine($"音频回调      : {audioCallbacks}  采样数: {audioSamples}  采样率: {sampleRate}");
        Console.WriteLine($"GPU 纹理帧    : {gpuServed}   CPU 内存帧: {cpuServed}");
        Console.WriteLine($"丢帧          : {player.VideoDroppedFrames}");
        if (saveFrames > 0)
            Console.WriteLine($"帧落盘        : {savedFrames} 张 -> {saveDir}");
        Console.WriteLine($"判读          : 资源=GPU(零拷贝句柄) ⇒ 硬解产出 GPU 纹理；资源=CPU(软解内存) ⇒ 软解（每 {sample} 帧抽一帧打印）");
        if (videoFrames > 0 && audioCallbacks > 0)
            Console.WriteLine("判定          : 管线通——ffmpeg 后端在 Linux 上成功解码并出餐音视频帧");
        else
            Console.WriteLine("判定          : 异常——未见音视频帧出餐，请检查上方 ffmpeg 原生库加载日志");
        return 0;
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static double? ParseDouble(string[] args, string name)
    {
        var s = ParseOption(args, name);
        return double.TryParse(s, out var v) ? v : null;
    }
}

// 帧落盘诊断：把视频帧（CPU 软解内存 / GPU 硬解回读）转 RGBA 后写极简 PNG。自包含零依赖。
// 支持 YUV420P（ffmpeg Linux 软解默认）/ NV12 / NV21 / BGRA32 / RGBA32。
// 均匀抽帧落盘比纯随机更利于判读（不会漏掉某一段），用于肉眼确认解码/上色是否正确。
internal static class FrameDumper
{
    internal static int DumpedCount;

    internal static void DumpFrame(VideoFrame frame, int frameIndex, string dir)
    {
        try
        {
            byte[]? rgba = null; int w = 0, h = 0;
            if (frame.Resource is SoftwareFrameResource sfr)
            {
                w = sfr.Width; h = sfr.Height;
                var span = sfr.Data.Span;
                switch (sfr.Format)
                {
                    case PixelFormat.YUV420P:
                        rgba = new byte[w * h * 4];
                        PlanarYuv420ToRgba(span, w, h, rgba);
                        break;
                    case PixelFormat.NV12:
                        rgba = new byte[w * h * 4];
                        SemiPlanarToRgba(span, w, h, rgba, false);
                        break;
                    case PixelFormat.NV21:
                        rgba = new byte[w * h * 4];
                        SemiPlanarToRgba(span, w, h, rgba, true);
                        break;
                    case PixelFormat.BGRA32:
                        rgba = new byte[w * h * 4];
                        ReorderBgraToRgba(span, w * h, rgba);
                        break;
                    case PixelFormat.RGBA32:
                        rgba = new byte[w * h * 4];
                        span.Slice(0, w * h * 4).CopyTo(rgba);
                        break;
                    default:
                        Console.WriteLine($"  [SAVE] 帧 {frameIndex} 格式 {sfr.Format} 不可落盘，跳过");
                        return;
                }
            }
            else if (frame.Resource is IGpuTextureResource gpu)
            {
                using var rb = gpu.ReadbackToCpu();
                w = rb.Width; h = rb.Height;
                var span = rb.Data.Span;
                rgba = new byte[w * h * 4];
                ReorderBgraToRgba(span, w * h, rgba);
            }
            else
            {
                Console.WriteLine($"  [SAVE] 帧 {frameIndex} 资源类型未知，跳过");
                return;
            }

            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"frame_{frameIndex:D6}.png");
            EncodeRgbaPng(path, w, h, rgba);
            Interlocked.Increment(ref DumpedCount);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [SAVE] 帧 {frameIndex} 落盘失败: {ex.Message}");
        }
    }

    // I420 planar（紧凑布局：Y[w*h] U[w*h/4] V[w*h/4]，无 stride padding）。
    private static void PlanarYuv420ToRgba(ReadOnlySpan<byte> data, int w, int h, byte[] rgba)
    {
        int ySize = w * h;
        int uvSize = w * h / 4;
        ReadOnlySpan<byte> yp = data.Slice(0, ySize);
        ReadOnlySpan<byte> up = data.Slice(ySize, uvSize);
        ReadOnlySpan<byte> vp = data.Slice(ySize + uvSize, uvSize);
        int halfW = w / 2;
        for (int y = 0; y < h; y++)
        {
            int yRow = y * w;
            int uvRow = (y / 2) * halfW;
            int o = yRow * 4;
            for (int x = 0; x < w; x++)
            {
                int Y = yp[yRow + x];
                int U = up[uvRow + (x / 2)];
                int V = vp[uvRow + (x / 2)];
                int c = Y - 16, d = U - 128, e = V - 128;
                rgba[o]     = (byte)Clamp((298 * c + 409 * e + 128) >> 8);
                rgba[o + 1] = (byte)Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                rgba[o + 2] = (byte)Clamp((298 * c + 516 * d + 128) >> 8);
                rgba[o + 3] = 255;
                o += 4;
            }
        }
    }

    private static void SemiPlanarToRgba(ReadOnlySpan<byte> nv, int w, int h, byte[] rgba, bool nv21)
    {
        int ySize = w * h;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int yi = y * w + x;
                int ui = ySize + (y / 2) * w + (x / 2) * 2;
                int Y = nv[yi], U = nv[ui], V = nv[ui + 1];
                if (nv21) (U, V) = (V, U);
                int c = Y - 16, d = U - 128, e = V - 128;
                int o = yi * 4;
                rgba[o]     = (byte)Clamp((298 * c + 409 * e + 128) >> 8);
                rgba[o + 1] = (byte)Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                rgba[o + 2] = (byte)Clamp((298 * c + 516 * d + 128) >> 8);
                rgba[o + 3] = 255;
            }
        }
    }

    private static void ReorderBgraToRgba(ReadOnlySpan<byte> bgra, int pixels, byte[] rgba)
    {
        for (int i = 0; i < pixels; i++)
        {
            int si = i * 4, di = i * 4;
            rgba[di] = bgra[si + 2];
            rgba[di + 1] = bgra[si + 1];
            rgba[di + 2] = bgra[si];
            rgba[di + 3] = bgra[si + 3];
        }
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static readonly uint[] CrcTable = BuildCrc();

    internal static void EncodeRgbaPng(string path, int w, int h, ReadOnlySpan<byte> rgba)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 0x08; ihdr[9] = 0x06;
        WriteChunk(fs, "IHDR", ihdr);
        using var ms = new MemoryStream();
        using (var zs = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[w * 4 + 1];
            row[0] = 0;
            for (int y = 0; y < h; y++)
            {
                rgba.Slice(y * w * 4, w * 4).CopyTo(row.AsSpan(1));
                zs.Write(row);
            }
        }
        WriteChunk(fs, "IDAT", ms.ToArray());
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream fs, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        fs.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        fs.Write(typeBytes);
        fs.Write(data);
        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, Crc(typeBytes, data));
        fs.Write(crcBuf);
    }

    private static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte x in a) crc = (crc >> 8) ^ CrcTable[(crc ^ x) & 0xFF];
        foreach (byte x in b) crc = (crc >> 8) ^ CrcTable[(crc ^ x) & 0xFF];
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
}

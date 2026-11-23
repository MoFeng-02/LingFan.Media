using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.OpenAL;
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
/// <para>1) Linux 侧构建时，构建目标会自动把 ThirdParty 的 ffmpeg 共享库复制到输出目录，无需环境变量；</para>
/// <para>2) 若库未随构建复制（ThirdParty 缺失），设置 LF_FFMPEG_LIB 指向解包后的 lib 目录作兜底；</para>
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

        // 音频输出选择：--audio openal（默认，真实出声，经 libopenal → Pulse/ALSA）/ silent（仅数据出餐）。
        // OpenAL 原生库缺失时自动回落静音（Ubuntu: sudo apt install libopenal1）。
        string audioMode = (ParseOption(args, "--audio") ?? "openal").ToLowerInvariant();
        bool openalAvailable = audioMode != "silent" &&
            (NativeLibrary.TryLoad("libopenal.so.1", out _) || NativeLibrary.TryLoad("libopenal.so", out _));
        if (audioMode != "silent" && !openalAvailable)
            Console.WriteLine("[提示] libopenal 未找到（Ubuntu: sudo apt install libopenal1），本次回落静音输出。");

        Console.WriteLine("=== LingFan.Media Linux 无头探针（ffmpeg 解码 + 无头 NoOp 渲染 + 音频 openal/silent）===");
        if (!File.Exists(file))
        {
            Console.WriteLine($"找不到媒体文件：{file}（用 --file 指定绝对路径）");
            return 2;
        }
        Console.WriteLine($"媒体文件      : {file}");
        Console.WriteLine($"ffmpeg 库目录 : {ffmpegLib}");
        Console.WriteLine($"硬解/零拷贝   : {(useHw ? "请求(--hw)；Linux 解码侧 VAAPI 为 Phase 2 桩，当前回落软解并打印告警" : "关（软解软渲）")}");
        Console.WriteLine($"渲染器        : NoOp 无头（视频帧经事件出餐，不建 GPU 设备）");
        Console.WriteLine($"音频输出      : {(openalAvailable ? "OpenAL（真实出声；--audio silent 关闭）" : "静音（仅数据出餐）")}");
        Console.WriteLine($"日志级别      : {(verbose ? "Debug" : "Information")}");
        Console.WriteLine($"帧落盘        : {(saveFrames > 0 ? $"每 {saveFrames} 帧 -> {saveDir}" : "关闭（加 --save-frames N 开启，均匀抽帧成 PNG 判读解码正确性）")}");
        Console.WriteLine();

        var services = new ServiceCollection();
        services.AddLogging(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        //  跨平台：ffmpeg 解码 + 无头 NoOp 渲染 + 音频输出（openal 真实出声 / silent 数据出餐）。
        var mediaBuilder = services.AddLingFanMedia()
                .AddFFmpeg(o =>
                {
                    o.FFmpegLibraryPath = ffmpegLib;
                    o.HardwareAcceleration = useHw;
                })
                .AddHeadlessRenderer();
        if (openalAvailable)
            mediaBuilder.AddOpenALOutput();
        else
            mediaBuilder.AddSilentAudioOutput();

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

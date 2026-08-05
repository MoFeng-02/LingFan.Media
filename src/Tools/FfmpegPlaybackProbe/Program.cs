using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FfmpegPlaybackProbe;

/// <summary>
/// 最小可验证播放程序：在<b>带控制台窗口的独立真实进程</b>里跑 LingFan.Media 的 <b>FFmpeg 后端生产链路</b>
/// （MediaPlayer + FFmpeg 解封装 + FFmpeg 解码），逐秒输出可观测指标，用于验证 HEVC 等经 ffmpeg 后端
/// 能否端到端解码（含 D3D11VA 零拷贝硬件路径）。
/// </summary>
/// <remarks>
/// <para>与 MF 探针分工：MF 后端无法在当前进程激活 Store HEVC MFT（GPL/静态约束）；本工具用 FFmpeg 后端
/// 独立验证 HEVC 解码，是「HEVC 在 Windows 走 ffmpeg 后端」的最小测试。</para>
/// <para>LGPL 合规：FFmpeg 经 <see cref="FFmpeg.AutoGen"/> 的 DynamicallyLoaded 绑定在运行时<b>动态加载</b>共享 DLL，
/// 不静态合并；DLL 来自合规的 BtbN lgpl-shared 构建（见仓库 ThirdParty/ffmpeg 与说明）。</para>
/// <para>用法：</para>
/// <code>
/// dotnet run --project src\Tools\FfmpegPlaybackProbe
/// dotnet run --project src\Tools\FfmpegPlaybackProbe -- -v            // Debug 级日志
/// dotnet run --project src\Tools\FfmpegPlaybackProbe -- --hw          // 启用 D3D11VA 零拷贝硬件解码
/// dotnet run --project src\Tools\FfmpegPlaybackProbe -- --file "D:\x.mp4"
/// </code>
/// </remarks>
internal static class Program
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

    private static async Task<int> Main(string[] args)
    {
        bool verbose = args.Contains("-v") || args.Contains("--verbose");
        bool useHw = args.Contains("--hw");
        string? file = ParseOption(args, "--file") ?? ResolveDefaultMedia();

        Console.WriteLine("=== LingFan.Media FFmpeg 后端最小播放验证（生产链路） ===");
        if (file is null || !File.Exists(file))
        {
            Console.WriteLine($"找不到媒体文件：{file ?? "(null)"}");
            Console.WriteLine("请用 --file <路径> 指定。");
            return 2;
        }
        Console.WriteLine($"媒体文件      : {file}");
        Console.WriteLine($"视频渲染      : NoOp 无头渲染器（隔离上屏，专注解码链路）");
        Console.WriteLine($"硬件加速      : {(useHw ? "启用 D3D11VA（另注册 D3D11 设备上下文供解码器取设备，仍不上屏）" : "禁用（纯软件解码，验证后端基础链路）")}");
        Console.WriteLine($"日志级别      : {(verbose ? "Debug" : "Information")}");
        Console.WriteLine($"原生库路径    : AppContext.BaseDirectory（FFmpegOptions.FFmpegLibraryPath，LGPL 可替换）");
        Console.WriteLine();

        var services = new ServiceCollection();
        services.AddLogging(b => b
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "[HH:mm:ss.fff] ";
            })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        // 仅注册 FFmpeg 后端（与 MediaFoundation 解耦）。FFmpegLibraryPath 指向输出目录——
        // 构建目标 CopyFFmpegNative 会把合规的共享 DLL 复制到此，运行时动态加载。
        var builder = services.AddLingFanMedia()
            .AddFFmpeg(o =>
            {
                o.FFmpegLibraryPath = AppContext.BaseDirectory;
                o.LogLevel = verbose ? 32 /* AV_LOG_DEBUG */ : 16 /* AV_LOG_ERROR */;
            })
            .AddSilentAudioOutput();

        // --hw：只为拿到 IGpuDeviceContext（窗口无关的共享 ID3D11Device），供 FFmpeg D3D11VA 取设备。
        // 🔴 注册顺序有意为之：D3D11 在前、无头渲染器在后 —— 后注册者赢得 IVideoRendererFactory，
        // 于是「呈现走 NoOp（无窗口、不上屏）」与「解码走 D3D11VA 零拷贝」两件事同时成立。
        // 这依赖 AddD3D11Renderer 里 IGpuDeviceContext 由具体类型 D3D11RendererFactory 派生
        // （而非从 IVideoRendererFactory 强转），否则此处会 InvalidCastException。
        if (useHw)
            builder.AddD3D11Renderer();
        builder.AddHeadlessRenderer();

        await using var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        // ---- 视频观测量 ----
        long videoFrames = 0;          // 累计交付（呈现）帧数
        long gpuZeroCopyFrames = 0;    // 其中 GPU 纹理（零拷贝）帧数
        long firstVideoWallMs = -1;
        var sw = new Stopwatch();

        // ---- 音频观测量（确认音频也经 ffmpeg 解码）----
        long audioFrames = 0;

        player.VideoFrameAvailable += f =>
        {
            Interlocked.Increment(ref videoFrames);
            if (firstVideoWallMs < 0) firstVideoWallMs = sw.ElapsedMilliseconds;
            if (f.Resource is IGpuTextureResource) Interlocked.Increment(ref gpuZeroCopyFrames);
        };
        player.AudioDataAvailable += _ => Interlocked.Increment(ref audioFrames);

        try
        {
            var source = new FileMediaSource(file);
            sw.Start();
            await player.OpenAsync(source, CancellationToken.None);
            double openSec = sw.Elapsed.TotalSeconds;

            var duration = player.Duration;
            Console.WriteLine();
            Console.WriteLine($"OpenAsync 耗时: {openSec:F2}s   Duration={duration:g}   " +
                              $"VideoTracks={player.Session?.VideoTracks.Count ?? 0}   " +
                              $"AudioTracks={player.Session?.AudioTracks.Count ?? 0}");
            Console.WriteLine($"视频编码      : {player.Session?.VideoTracks.FirstOrDefault()?.VideoCodec}   " +
                              $"音频编码: {player.Session?.AudioTracks.FirstOrDefault()?.AudioCodec}");

            if (duration <= TimeSpan.Zero)
            {
                Console.WriteLine("⚠ Duration 为 0，后端未查到容器时长，后续判定不可靠。");
                duration = TimeSpan.FromSeconds(40);
            }

            await player.PlayAsync();
            Console.WriteLine();
            Console.WriteLine("  t(s)    pos(s)   videoFrames  gpuZeroCopy   audioFrames   state");
            Console.WriteLine("  ------  -------  -----------  -----------  -----------  -----------");

            long prevV = 0, prevG = 0, prevA = 0;
            double limitSec = duration.TotalSeconds + 3.0;
            while (sw.Elapsed.TotalSeconds < limitSec && player.State != MediaState.Stopped)
            {
                await Task.Delay(SampleInterval);
                long curV = Interlocked.Read(ref videoFrames);
                long curG = Interlocked.Read(ref gpuZeroCopyFrames);
                long curA = Interlocked.Read(ref audioFrames);
                Console.WriteLine($"  {sw.Elapsed.TotalSeconds,6:F1}  {player.Position.TotalSeconds,7:F02}  " +
                                  $"{curV,11}  {curG,11}  {curA,11}  {player.State,-11}");
                prevV = curV; prevG = curG; prevA = curA;
            }

            double totalWall = sw.Elapsed.TotalSeconds;
            await player.StopAsync(CancellationToken.None);

            Console.WriteLine();
            Console.WriteLine("=== 汇总 ===");
            Console.WriteLine($"  容器时长          : {duration.TotalSeconds:F2}s");
            Console.WriteLine($"  墙钟总耗时        : {totalWall:F2}s");
            Console.WriteLine($"  最终播放位置      : {player.Position.TotalSeconds:F2}s");
            Console.WriteLine($"  视频帧(交付)      : {Interlocked.Read(ref videoFrames)}");
            Console.WriteLine($"  GPU零拷贝帧       : {Interlocked.Read(ref gpuZeroCopyFrames)}");
            Console.WriteLine($"  视频丢帧          : {player.VideoDroppedFrames}");
            Console.WriteLine($"  音频帧            : {Interlocked.Read(ref audioFrames)}");
            if (firstVideoWallMs > 0)
                Console.WriteLine($"  首帧视频墙钟      : {firstVideoWallMs / 1000.0:F2}s");

            Console.WriteLine();
            Console.WriteLine("=== 判定 ===");
            long vf = Interlocked.Read(ref videoFrames);
            if (vf < 30)
                Console.WriteLine($"  ⚠ 视频帧数过少({vf})：解码链路可能未跑通，检查上方 [FFmpeg] 解码器日志。");
            else
                Console.WriteLine("  ✓ 视频解码链路跑通（帧数覆盖时长）。");
            if (useHw)
            {
                long gz = Interlocked.Read(ref gpuZeroCopyFrames);
                if (gz > 0)
                    Console.WriteLine($"  ✓ D3D11VA 零拷贝生效：{gz}/{vf} 帧为 GPU 纹理（未读回系统内存）。");
                else
                    Console.WriteLine("  ⚠ --hw 下零拷贝帧为 0：D3D11VA 未生效（可能设备不可用或回落软件，见解码器日志硬件加速=False）。");
            }
            else
            {
                Console.WriteLine("  （未启用 --hw：本跑为软件解码，验证后端基础链路。零拷贝需加 --hw。）");
            }
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            await player.DisposeAsync();
            await Task.Delay(500);
        }

        Console.WriteLine("=== 诊断完成。把以上输出整段贴回即可定位。 ===");
        return 0;
    }

    private static string? ParseOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>优先用输出目录下随工程复制的 Resources\Video\m1.mp4。</summary>
    private static string? ResolveDefaultMedia()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "Resources", "Video", "m1.mp4");
        if (File.Exists(local)) return local;
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Resources", "Video", "m1.mp4");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.VLC;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VlcPlaybackProbe;

/// <summary>
/// 最小可验证播放程序：在<b>带控制台窗口的独立真实进程</b>里跑 LingFan.Media 的 <b>VLC 后端生产链路</b>
/// （MediaPlayer + VLC 解封装/解码 + 直通解码器），逐秒输出可观测指标，用于验证 VLC 后端能否端到端解码。
/// </summary>
/// <remarks>
/// <para>与 FFmpeg/MF 探针分工：VLC 后端由 LibVLCSharp 驱动 VLC 引擎，VLC 内部一体化完成解封装+解码，
/// 通过 <c>SetVideoCallbacks</c> 内存捕获把<b>已解码 BGRA32 帧</b>推给我们管线（直通解码器）。
/// 因此 VLC 路径<b>永远是 CPU 内存帧</b>，走不到 ffmpeg D3D11VA 那样的 GPU 零拷贝。
/// <c>--hw</c> 仅让 VLC 内部启用硬解（DXVA2 等），但回调交付仍是 BGRA32 CPU 内存。</para>
/// <para>原生库：VLC 后端仅依赖托管层 LibVLCSharp（AOT 合规、零原生依赖）。真正的原生 libvlc 运行时
/// 经 <b>VideoLAN.LibVLC.Windows</b> NuGet 包随本探针输出目录自带分发包（LGPL），无需目标机预装 VLC。
/// 库本体（Backends.VLC）不含任何原生运行时、保持 AOT 100%；该原生包只落在 Tools 探针里，不污染库分发。
/// 本程序启动期定位 libvlc.dll 所在目录并<b>前置进当前进程 PATH</b>，使 LibVLCSharp 的原生加载器（LoadLibrary 搜 PATH）能稳定找到 libvlc。</para>
/// <para>用法：</para>
/// <code>
/// dotnet run --project src\Tools\VlcPlaybackProbe
/// dotnet run --project src\Tools\VlcPlaybackProbe -- -v            // Debug 级日志
/// dotnet run --project src\Tools\VlcPlaybackProbe -- --hw          // 启用 VLC 内部硬解（仍 CPU 交付）
/// dotnet run --project src\Tools\VlcPlaybackProbe -- --file "D:\x.mp4"
/// </code>
/// </remarks>
internal static class Program
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

    private static async Task<int> Main(string[] args)
    {
        // ---- 全局异常/故障兜底（诊断用，与 ffmpeg 探针一致）----
        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as System.Exception;
                Console.Error.WriteLine($"[UNHANDLED] {(ex?.GetType().Name ?? e.ExceptionObject?.GetType().ToString())}: {ex?.Message}");
                Console.Error.WriteLine(ex?.StackTrace);
                Console.Error.WriteLine($"  IsTerminating={e.IsTerminating}");
            }
            catch { }
            finally { try { Console.Out.Flush(); Console.Error.Flush(); } catch { } }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                Console.Error.WriteLine($"[UNOBSERVED-TASK] {e.Exception.GetType().Name}: {e.Exception.Message}");
                Console.Error.WriteLine(e.Exception.StackTrace);
                e.SetObserved();
            }
            catch { }
            finally { try { Console.Out.Flush(); Console.Error.Flush(); } catch { } }
        };
        void Trace(string s) { Console.WriteLine($"  [trace] {s}"); Console.Out.Flush(); }

        bool verbose = args.Contains("-v") || args.Contains("--verbose");
        bool useHw = args.Contains("--hw");
        string? file = ParseOption(args, "--file") ?? ResolveDefaultMedia();

        Console.WriteLine("=== LingFan.Media VLC 后端最小播放验证（生产链路） ===");
        if (file is null || !File.Exists(file))
        {
            Console.WriteLine($"找不到媒体文件：{file ?? "(null)"}");
            Console.WriteLine("请用 --file <路径> 指定。");
            return 2;
        }
        Console.WriteLine($"媒体文件      : {file}");
        Console.WriteLine($"视频渲染      : NoOp 无头渲染器（隔离上屏，专注解码链路）");
        Console.WriteLine($"硬件加速      : {(useHw ? "启用 VLC 内部硬解（DXVA2 等，但回调仍交付 CPU BGRA32）" : "禁用（VLC 软件解码，验证后端基础链路）")}");
        Console.WriteLine($"日志级别      : {(verbose ? "Debug" : "Information")}");
        Console.WriteLine();

        // ---- VLC 原生库定位 ----
        // 🔴 VLC 后端运行时必须有原生 libvlc。优先用随探针自带分发的 VideoLAN.LibVLC.Windows
        // 原生包（输出目录下的 libvlc.dll + libvlccore.dll + plugins/）；其次回退到本机已装 VLC。
        // 无论哪种来源，都把其目录前置进进程 PATH，使 LibVLCSharp 的原生加载器（LoadLibrary 搜 PATH）能稳定找到 libvlc.dll。
        // 注意：LibVLCSharp 3.10.0 的 Core 仅含 Initialize 方法、无 LibVLCPath 属性，故用 PATH 注入（版本无关、稳定）。
        Console.WriteLine("VLC 原生库定位:");
        string? vlcDir = LocateLibVlc();
        if (vlcDir is null)
        {
            Console.WriteLine("  [失败] 未找到原生 libvlc.dll（VideoLAN.LibVLC.Windows 原生包未随输出分发，且系统也未装 VLC）。");
            Console.WriteLine("         请确认 VlcPlaybackProbe.csproj 已引用 VideoLAN.LibVLC.Windows，或安装 VLC（https://www.videolan.org/，LGPL）。");
            return 3;
        }
        // 将 VLC 目录前置进当前进程 PATH，使 LibVLCSharp 的原生加载器（LoadLibrary 搜 PATH）能找到 libvlc.dll。
        // 不依赖 LibVLCSharp 内部路径 API（Core 在 3.10.0 仅含 Initialize，无 LibVLCPath 属性），此方式版本无关且稳定。
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (existingPath.IndexOf(vlcDir, StringComparison.OrdinalIgnoreCase) < 0)
            Environment.SetEnvironmentVariable("PATH", vlcDir + Path.PathSeparator + existingPath);
        Console.WriteLine($"  [OK]   libvlc 目录(已前置 PATH): {vlcDir}");
        Console.WriteLine();

        Trace("DI：开始构建 ServiceCollection");
        var services = new ServiceCollection();
        services.AddLogging(b => b
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "[HH:mm:ss.fff] ";
            })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));
        Trace("DI：AddLogging OK");

        // 仅注册 VLC 后端（与 MF/FFmpeg 解耦）。VLC 解码器为直通（BGRA32 内存帧），
        // 不需要 D3D11 渲染器提供 GPU 设备上下文，故不注册 D3D11。
        var builder = services.AddLingFanMedia()
            .AddVLC(o =>
            {
                o.EnableHardwareDecoding = useHw;
                o.Headless = true; // --vout=dummy：无窗口，但仍经 SetVideoCallbacks 内存捕获
            })
            .AddSilentAudioOutput();
        Trace("DI：AddLingFanMedia + AddVLC + AddSilentAudioOutput OK");

        builder.AddHeadlessRenderer();
        Trace("DI：AddHeadlessRenderer OK");

        await using var sp = services.BuildServiceProvider();
        Trace("DI：BuildServiceProvider OK");
        var player = sp.GetRequiredService<IMediaPlayer>();
        Trace("DI：GetRequiredService<IMediaPlayer> OK");

        // ---- 视频观测量 ----
        long videoFrames = 0;          // 累计交付（呈现）帧数
        long firstVideoWallMs = -1;
        var sw = new Stopwatch();

        // ---- 音频观测量（确认音频也经 VLC 回调捕获）----
        long audioFrames = 0;

        player.VideoFrameAvailable += f =>
        {
            Interlocked.Increment(ref videoFrames);
            if (firstVideoWallMs < 0) firstVideoWallMs = sw.ElapsedMilliseconds;
        };
        player.AudioDataAvailable += _ => Interlocked.Increment(ref audioFrames);

        try
        {
            var source = new FileMediaSource(file);
            sw.Start();
            Trace("OpenAsync 开始");
            await player.OpenAsync(source, CancellationToken.None);
            Trace($"OpenAsync 完成 ({sw.Elapsed.TotalSeconds:F2}s)");
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

            Trace("PlayAsync 开始");
            await player.PlayAsync();
            Trace("PlayAsync 返回");
            Console.WriteLine();
            Console.WriteLine("  t(s)    pos(s)   videoFrames   audioFrames   state");
            Console.WriteLine("  ------  -------  -----------  -----------  -----------");

            long prevV = 0, prevA = 0;
            double limitSec = duration.TotalSeconds + 3.0;
            while (sw.Elapsed.TotalSeconds < limitSec && player.State != MediaState.Stopped)
            {
                await Task.Delay(SampleInterval);
                long curV = Interlocked.Read(ref videoFrames);
                long curA = Interlocked.Read(ref audioFrames);
                Console.WriteLine($"  {sw.Elapsed.TotalSeconds,6:F1}  {player.Position.TotalSeconds,7:F02}  " +
                                  $"{curV,11}  {curA,11}  {player.State,-11}");
                prevV = curV; prevA = curA;
            }

            double totalWall = sw.Elapsed.TotalSeconds;
            await player.StopAsync(CancellationToken.None);

            Console.WriteLine();
            Console.WriteLine("=== 汇总 ===");
            Console.WriteLine($"  容器时长          : {duration.TotalSeconds:F02}s");
            Console.WriteLine($"  墙钟总耗时        : {totalWall:F02}s");
            Console.WriteLine($"  最终播放位置      : {player.Position.TotalSeconds:F02}s");
            Console.WriteLine($"  视频帧(交付)      : {Interlocked.Read(ref videoFrames)}");
            Console.WriteLine($"  视频丢帧          : {player.VideoDroppedFrames}");
            Console.WriteLine($"  音频帧            : {Interlocked.Read(ref audioFrames)}");
            if (firstVideoWallMs > 0)
                Console.WriteLine($"  首帧视频墙钟      : {firstVideoWallMs / 1000.0:F02}s");

            Console.WriteLine();
            Console.WriteLine("=== 判定 ===");
            long vf = Interlocked.Read(ref videoFrames);
            if (vf < 30)
                Console.WriteLine($"  ⚠ 视频帧数过少({vf})：解码链路可能未跑通，检查上方 [VLC] 解码器日志。");
            else
                Console.WriteLine("  ✓ 视频解码链路跑通（帧数覆盖时长）。");
            Console.WriteLine("  （VLC 后端经回调内存捕获，交付恒为 BGRA32 CPU 帧；无 GPU 零拷贝路径。）");
            Console.WriteLine($"  {(useHw ? "✓ 已请求 VLC 内部硬解；交付仍 CPU BGRA32（与 ffempg D3D11VA 零拷贝不同）。" : "（未启用 --hw：VLC 软件解码，验证后端基础链路。）")}");
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

    /// <summary>定位原生 libvlc 目录（libvlc.dll 所在目录）：优先探针自带分发的 NuGet 原生包，其次本机已装 VLC。</summary>
    private static string? LocateLibVlc()
    {
        string baseDir = AppContext.BaseDirectory;

        // 1) 探针输出目录树（VideoLAN.LibVLC.Windows 原生包随构建复制到此；位置不固定，
        //    可能在根目录、libvlc 子目录或 win-x64/libvlc 等）。深度受限搜索避免误入无关目录。
        string? bundled = FindBundledLibVlc(baseDir);
        if (bundled is not null) return bundled;

        // 2) 进程 PATH 中各目录
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "libvlc.dll")))
                    return dir;
            }
            catch { /* 忽略无权限目录 */ }
        }

        // 3) 常见系统安装位置（无自带包时的回退）
        var candidates = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string? pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string? pfX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (pf is not null) candidates.Add(Path.Combine(pf, "VideoLAN", "VLC"));
            if (pfX86 is not null) candidates.Add(Path.Combine(pfX86, "VideoLAN", "VLC"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux：系统包管理器提供 libvlc（如 /usr/lib/x86_64-linux-gnu），通常已在默认搜索路径
            candidates.Add("/usr/lib/x86_64-linux-gnu");
            candidates.Add("/usr/lib");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add("/Applications/VLC.app/Contents/MacOS/lib");
            candidates.Add("/usr/local/lib");
        }

        foreach (var cand in candidates)
        {
            try
            {
                if (File.Exists(Path.Combine(cand, "libvlc.dll")) ||
                    File.Exists(Path.Combine(cand, "libvlc.so")) ||
                    File.Exists(Path.Combine(cand, "libvlc.dylib")))
                    return cand;
            }
            catch { /* 忽略 */ }
        }

        return null;
    }

    /// <summary>在输出目录树中找含 libvlc.dll 的目录（NuGet 原生包随构建复制的位置不固定）。优先与当前进程架构匹配的那一份。</summary>
    private static string? FindBundledLibVlc(string startDir)
    {
        // VideoLAN.LibVLC.Windows 把原生库放在 libvlc/win-<arch>/ 下。
        // 必须按进程架构选对子目录——把 x86 的 libvlc.dll 注入 64 位进程会导致 LoadLibrary 失败（BAD_EXE_FORMAT）。
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => "x64"
        };
        string archDir = Path.Combine(startDir, "libvlc", "win-" + arch);
        if (File.Exists(Path.Combine(archDir, "libvlc.dll")))
            return archDir;

        // 回退：深度受限扫描（覆盖根目录或其它布局）
        if (File.Exists(Path.Combine(startDir, "libvlc.dll")))
            return startDir;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(startDir))
            {
                if (File.Exists(Path.Combine(sub, "libvlc.dll")))
                    return sub;
                foreach (var sub2 in Directory.EnumerateDirectories(sub))
                {
                    if (File.Exists(Path.Combine(sub2, "libvlc.dll")))
                        return sub2;
                }
            }
        }
        catch { /* 忽略无权限/并发删除 */ }
        return null;
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

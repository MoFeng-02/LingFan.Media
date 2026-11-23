using System.Diagnostics;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Sources;
using MediaSourceProbe.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaSourceProbe;

/// <summary>
/// 媒体源三路验证工具：本地文件 / 自定义流（可定位与不可定位包装）/ 网络 URL（http/https）。
/// 验证 OpenAsync 打开链路、播放到自然结束、播放中定位（网络源即 Range 重开连接定位）、
/// 读超时停滞解除，以及离线 SSRF 守卫自检。无头（不创建窗口），音频走静音输出。
/// </summary>
/// <remarks>
/// <para>用法：</para>
/// <code>
/// MediaSourceProbe &lt;文件路径|URL&gt; [选项]
/// MediaSourceProbe --ssrf-selftest
/// </code>
/// <para>完整选项见 <see cref="ProbeOptions.Usage"/>；<c>-h</c>/<c>--help</c> 亦打印该说明。</para>
/// <para>退出码：0=总体 PASS，1=判定失败或运行期异常，2=用法错误，3=FFmpeg 原生库预检失败。</para>
/// </remarks>
internal static partial class Program
{
    /// <summary>逐行采样间隔。</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>播放开始到注入定位的等待时长：留出起播与首帧交付的稳定窗口，避免在缓冲期定位。</summary>
    private static readonly TimeSpan SeekInjectionDelay = TimeSpan.FromMilliseconds(1200);

    private static async Task<int> Main(string[] args)
    {
        InstallFaultHandlers();

        if (args.Contains("--ssrf-selftest"))
            return SsrfSelfTest.Run();

        if (args.Contains("-h") || args.Contains("--help"))
        {
            Console.WriteLine(ProbeOptions.Usage);
            return 0;
        }

        if (!ProbeOptions.TryParse(args, out var options, out string? parseError))
        {
            Console.WriteLine(parseError);
            return 2;
        }

        PrintHeader(options);

        if (!FfmpegPrecheck.Run())
            return 3;

        using var probeSource = TryCreateSource(options, out int sourceExitCode);
        if (probeSource is null)
            return sourceExitCode;

        Console.WriteLine($"[SOURCE] 已构造: Type={probeSource.Source.Type} Identifier={probeSource.Source.Identifier}");

        await using var services = BuildServices(options);
        var player = services.GetRequiredService<IMediaPlayer>();

        await InspectSourceContractAsync(services, probeSource.Source);

        var observation = new Observation();
        player.VideoFrameAvailable += _ => observation.OnVideoFrame();
        player.AudioDataAvailable += _ => observation.OnAudioFrame();
        player.StateChanged += (_, e) => observation.OnStateChanged(e.OldState, e.NewState);
        player.ErrorOccurred += (_, e) => observation.OnError(e);

        var run = await RunPlaybackAsync(player, probeSource.Source, options, observation);
        return ProbeReport.Print(run) ? 0 : 1;
    }

    /// <summary>全局异常/故障兜底：任何逃逸的异常都打上标签，避免静默退出。</summary>
    private static void InstallFaultHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                Console.Error.WriteLine($"[UNHANDLED] {(ex?.GetType().Name ?? e.ExceptionObject?.GetType().ToString())}: {ex?.Message}");
                Console.Error.WriteLine(ex?.StackTrace);
                Console.Error.WriteLine($"  IsTerminating={e.IsTerminating}");
            }
            catch { }
            finally { Flush(); }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                Console.Error.WriteLine($"[UNOBSERVED-TASK] {e.Exception.GetType().Name}: {e.Exception.Message}");
                Console.Error.WriteLine(e.Exception.StackTrace);
                e.SetObserved();
            }
            catch { }
            finally { Flush(); }
        };
    }

    private static void Flush()
    {
        try { Console.Out.Flush(); Console.Error.Flush(); } catch { }
    }

    private static void PrintHeader(ProbeOptions options)
    {
        Console.WriteLine("=== LingFan.Media 媒体源三路验证（文件 / 流 / 网络） ===");
        Console.WriteLine($"源参数        : {options.SourceArgument}");
        string streamNote = options.SourceType == MediaSourceType.Stream
            ? (options.NonSeekable ? "（不可定位包装）" : "（可定位透传）")
            : string.Empty;
        Console.WriteLine($"源类型        : {options.SourceType}{streamNote}");
        Console.WriteLine($"定位测试      : {(options.SeekTarget is { } target ? $"播放稳定后定位到 {target.TotalSeconds:F2}s" : "否")}");
        Console.WriteLine($"日志级别      : {(options.Verbose ? "Debug" : "Information")}");
        Console.WriteLine();
    }

    /// <summary>
    /// 构造媒体源。源被 SSRF 守卫拒绝属于预期防护行为（退出码 0），其余构造失败按用法错误处理。
    /// </summary>
    private static ProbeSource? TryCreateSource(ProbeOptions options, out int exitCode)
    {
        exitCode = 0;
        try
        {
            return SourceFactory.Create(options);
        }
        catch (ArgumentException ex)
        {
            // 构造期 SSRF 拒绝（内网/回环/保留地址）在此表现为 ArgumentException
            Console.WriteLine($"[SOURCE] 源被拒绝: {ex.Message}");
            Console.WriteLine("判定: 拒绝生效（若本意是访问内网，请加 --allow-private）");
            return null;
        }
        catch (NotSupportedException ex)
        {
            Console.WriteLine($"[SOURCE] 源不支持: {ex.Message}");
            exitCode = 2;
            return null;
        }
    }

    private static ServiceProvider BuildServices(ProbeOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "[HH:mm:ss.fff] ";
            })
            .SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information));

        services.AddLingFanMedia()
            .AddFFmpeg(o =>
            {
                o.FFmpegLibraryPath = AppContext.BaseDirectory;
                o.LogLevel = options.Verbose ? 32 : 16;
            })
            .AddSilentAudioOutput()
            .AddHeadlessRenderer();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 契约检视：工厂建流（不连接），打印地址/可定位性/建议打开头。
    /// </summary>
    /// <remarks>
    /// Stream 源跳过本检视：探测流与播放共享同一底层流，探测读取会推进底层位置，
    /// 使后续播放从中间开始。File/Network 源的探测流各自独立打开，无此影响。
    /// </remarks>
    private static async Task InspectSourceContractAsync(IServiceProvider services, IMediaSource source)
    {
        if (source.Type == MediaSourceType.Stream)
        {
            Console.WriteLine("[SOURCE] Stream 源跳过契约检视（探测流与播放共享底层流，避免推进读取位置）");
            Console.WriteLine();
            return;
        }

        try
        {
            var streamFactory = services.GetRequiredService<IMediaStreamFactory>();
            var probe = await streamFactory.CreateAsync(source);
            Console.WriteLine($"[SOURCE] Location={probe.Location ?? "(null)"}");
            Console.WriteLine($"[SOURCE] CanSeek(未建连)={probe.CanSeek}  Length={probe.Length}");

            var openHeaders = probe.SuggestedOpenHeaders;
            if (openHeaders is null)
            {
                Console.WriteLine("[SOURCE] SuggestedOpenHeaders=(null，非网络流)");
            }
            else
            {
                Console.WriteLine($"[SOURCE] SuggestedOpenHeaders ({openHeaders.Count} 项):");
                foreach (var kv in openHeaders)
                    Console.WriteLine($"    {kv.Key}: {kv.Value}");
            }
            probe.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOURCE] 契约检视失败（不阻断播放验证）: {ex.Message}");
        }
        Console.WriteLine();
    }


}

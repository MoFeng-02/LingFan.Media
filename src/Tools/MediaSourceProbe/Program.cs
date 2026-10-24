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
internal static class Program
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

    /// <summary>
    /// 打开、播放、可选定位、收尾，最后组装判定输入。
    /// </summary>
    /// <remarks>
    /// 定位判定不放在采样循环内：循环只负责记录与在终态处收束，
    /// 判定在循环结束后按「位置是否正确 / 定位后是否出新帧 / 定位后是否转入终态」三项组合给出，
    /// 因此播放器在定位后转入 Ended 也能得到明确原因，而不是笼统的 FAIL。
    /// </remarks>
    private static async Task<ReportInputs> RunPlaybackAsync(
        IMediaPlayer player, IMediaSource source, ProbeOptions options, Observation observation)
    {
        var sw = Stopwatch.StartNew();
        MediaState lastState = MediaState.Idle;
        TimeSpan lastPosition = TimeSpan.Zero;
        TimeSpan lastDuration = TimeSpan.Zero;
        long droppedFrames = 0;
        double stopAsyncMs = -1, disposeAsyncMs = -1;
        bool reachedTerminal = false;
        Exception? fatal = null;
        TimeSpan verdictPosition = TimeSpan.Zero;

        try
        {
            Console.WriteLine("[OPEN] OpenAsync 开始…");
            await player.OpenAsync(source, CancellationToken.None);
            Console.WriteLine($"[OPEN] 完成（{sw.Elapsed.TotalSeconds:F2}s）  Duration={player.Duration:g}  " +
                              $"VideoTracks={player.Session?.VideoTracks.Count ?? 0}  AudioTracks={player.Session?.AudioTracks.Count ?? 0}");
            Console.WriteLine($"[OPEN] 视频={player.Session?.VideoTracks.FirstOrDefault()?.VideoCodec}  " +
                              $"音频={player.Session?.AudioTracks.FirstOrDefault()?.AudioCodec}  State={player.State}");

            if (options.OpenOnly)
            {
                Console.WriteLine("[OPEN] --open-only：到此为止。");
                Console.WriteLine();
                Console.WriteLine("判定: 打开链路 PASS");
                return OpenOnlyResult(options, observation, sw);
            }

            await player.PlayAsync();
            Console.WriteLine();
            Console.WriteLine("  t(s)    pos(s)   videoFrames  audioFrames  state");
            Console.WriteLine("  ------  -------  -----------  -----------  -----------");

            bool seekSkip = IsSeekOutOfScope(options);
            if (seekSkip)
            {
                Console.WriteLine("[SEEK] 跳过：--nonseekable 的不可定位流不支持定位（如需验证定位，去掉 --nonseekable）。");
                Console.WriteLine();
            }

            double limitSec = (player.Duration > TimeSpan.Zero ? player.Duration.TotalSeconds : 60) + 5.0;
            bool seekInjected = false;

            while (sw.Elapsed.TotalSeconds < limitSec)
            {
                await Task.Delay(SampleInterval);
                var state = player.State;
                long v = observation.VideoFrames;
                long a = observation.AudioFrames;
                Console.WriteLine($"  {sw.Elapsed.TotalSeconds,6:F1}  {player.Position.TotalSeconds,7:F02}  {v,11}  {a,11}  {state,-11}");

                if (!seekInjected && !seekSkip && options.SeekTarget is { } target
                    && observation.ElapsedMs >= SeekInjectionDelay.TotalMilliseconds)
                {
                    seekInjected = true;
                    await InjectSeekAsync(player, observation, target);
                }

                if (state is MediaState.Ended or MediaState.Stopped)
                {
                    reachedTerminal = true;
                    Console.WriteLine($"[EOF] 到达终态 {state}（t={sw.Elapsed.TotalSeconds:F1}s, pos={player.Position.TotalSeconds:F2}s）");
                    break;
                }

                if (state == MediaState.Error)
                {
                    Console.WriteLine($"[ERROR-STATE] 播放器进入 Error 状态（t={sw.Elapsed.TotalSeconds:F1}s）");
                    break;
                }
            }

            if (!reachedTerminal && player.State != MediaState.Error)
                Console.WriteLine($"[TIMEOUT] 达到采样上限（{limitSec:F0}s）仍未到终态，state={player.State}");
        }
        catch (Exception ex)
        {
            fatal = ex;
            Console.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            // 快照 Stop 前的状态/位置/时长（StopAsync 会复位时钟与状态，汇总必须反映播放终态）
            lastState = player.State;
            lastPosition = player.Position;
            lastDuration = player.Duration;
            droppedFrames = player.VideoDroppedFrames;
            verdictPosition = lastPosition;

            // 退出计时（挂死定位：若停滞在此，看 [EXIT] 缺哪段）
            var exitSw = Stopwatch.StartNew();
            try
            {
                await player.StopAsync(CancellationToken.None);
                stopAsyncMs = exitSw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"[EXIT] StopAsync={stopAsyncMs:F0}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EXIT] StopAsync 异常: {ex.Message}");
            }

            var disposeSw = Stopwatch.StartNew();
            try
            {
                await player.DisposeAsync();
                disposeAsyncMs = disposeSw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"[EXIT] DisposeAsync={disposeAsyncMs:F0}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EXIT] DisposeAsync 异常: {ex.Message}");
            }
            await Task.Delay(300);
        }

        bool seekSkipFinal = IsSeekOutOfScope(options);
        var verdict = EvaluateSeek(options, observation, lastState, lastPosition, lastDuration, seekSkipFinal);

        return new ReportInputs
        {
            Options = options,
            Observation = observation,
            LastState = lastState,
            LastPosition = lastPosition,
            LastDuration = lastDuration,
            DroppedFrames = droppedFrames,
            StopAsyncMs = stopAsyncMs,
            DisposeAsyncMs = disposeAsyncMs,
            TotalSeconds = sw.Elapsed.TotalSeconds,
            ReachedTerminal = reachedTerminal,
            SeekSkipped = seekSkipFinal,
            SeekVerdict = verdict,
            PositionAtVerdict = verdictPosition,
            Fatal = fatal,
            OpenOnly = false,
        };
    }

    private static ReportInputs OpenOnlyResult(ProbeOptions options, Observation observation, Stopwatch sw) => new()
    {
        Options = options,
        Observation = observation,
        LastState = MediaState.Idle,
        LastPosition = TimeSpan.Zero,
        LastDuration = TimeSpan.Zero,
        DroppedFrames = 0,
        StopAsyncMs = -1,
        DisposeAsyncMs = 0,
        TotalSeconds = sw.Elapsed.TotalSeconds,
        ReachedTerminal = false,
        SeekSkipped = true,
        SeekVerdict = SeekVerdict.Skipped,
        PositionAtVerdict = TimeSpan.Zero,
        Fatal = null,
        OpenOnly = true,
    };

    /// <summary>不可定位流的定位请求不属于本工具验证范围，直接跳过并说明。</summary>
    private static bool IsSeekOutOfScope(ProbeOptions options)
        => options.NonSeekable && options.SourceType == MediaSourceType.Stream && options.SeekTarget is not null;

    private static async Task InjectSeekAsync(IMediaPlayer player, Observation observation, TimeSpan target)
    {
        TimeSpan prePosition = player.Position;
        observation.MarkSeekIssued(target, prePosition);
        Console.WriteLine($"[SEEK] 定位请求: {prePosition.TotalSeconds:F2}s -> {target.TotalSeconds:F2}s …");

        try
        {
            await player.SeekAsync(target);
            observation.MarkSeekReturned(player.Position);
            Console.WriteLine($"[SEEK] 返回  pos={player.Position.TotalSeconds:F2}s  耗时={observation.SeekReturnedAtMs - observation.SeekIssuedAtMs:F0}ms");
        }
        catch (Exception ex)
        {
            observation.MarkSeekFailed($"{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[SEEK] 抛出  {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 定位判定：按「位置是否正确 / 定位后是否出新帧 / 定位后是否转入终态」三项组合给结论。
    /// </summary>
    /// <remarks>
    /// 判定不依赖「判定时刻仍在 Playing」：播放器在定位后转入 Ended/Stopped/Error 时，
    /// 结论落在 <see cref="SeekVerdict.TerminatedAfterSeek"/>，与「位置正确但零新帧」区分开。
    /// 判定只看观测量，不看采样循环当时的状态——循环可能在终态处提前收束。
    /// </remarks>
    private static SeekVerdict EvaluateSeek(
        ProbeOptions options, Observation observation,
        MediaState lastState, TimeSpan lastPosition, TimeSpan lastDuration, bool seekSkipped)
    {
        if (options.SeekTarget is null)
            return SeekVerdict.NotRequested;
        if (seekSkipped)
            return SeekVerdict.Skipped;
        if (!observation.SeekIssued)
            return SeekVerdict.Skipped;
        if (observation.SeekError is not null)
            return SeekVerdict.SeekThrew;

        bool positionOk = Math.Abs((observation.PositionAtReturn - observation.SeekTarget).TotalSeconds)
                          <= ProbeReport.SeekPositionTolerance.TotalSeconds;
        if (!positionOk)
            return SeekVerdict.PositionMismatch;

        long framesAfterSeek = observation.VideoFramesAfterSeek + observation.AudioFramesAfterSeek;

        if (observation.TerminalAfterSeek)
        {
            // 定位后已转入终态：Ended 却离时长还差一截，或定位后零新帧且随即终止，都属于播放器在定位后异常终止。
            bool endedPrematurely = observation.TerminalAfterSeekState == MediaState.Ended
                                    && lastDuration > TimeSpan.Zero
                                    && lastPosition + ProbeReport.PrematureEndTolerance < lastDuration;
            if (endedPrematurely || framesAfterSeek == 0)
                return SeekVerdict.TerminatedAfterSeek;

            // 定位后仍有新帧且播放走到时长末尾 = 正常走完。
            return SeekVerdict.Pass;
        }

        if (framesAfterSeek == 0)
            return SeekVerdict.PositionOkNoFrames;

        return SeekVerdict.Pass;
    }
}

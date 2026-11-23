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

// Program 的播放观测/Seek 注入与判定段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
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

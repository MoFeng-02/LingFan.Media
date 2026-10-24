using LingFan.Media.Abstractions;

namespace MediaSourceProbe;

/// <summary>
/// 汇总与判定输出所需的全部观测量。由 <see cref="Program"/> 在退出阶段填充。
/// </summary>
internal sealed record ReportInputs
{
    public required ProbeOptions Options { get; init; }
    public required Observation Observation { get; init; }

    /// <summary>StopAsync 之前的播放状态快照。</summary>
    public required MediaState LastState { get; init; }

    /// <summary>StopAsync 之前的播放位置快照。</summary>
    public required TimeSpan LastPosition { get; init; }

    /// <summary>StopAsync 之前的时长快照（释放后不可再读）。</summary>
    public required TimeSpan LastDuration { get; init; }

    /// <summary>停止前的累计丢帧数。</summary>
    public required long DroppedFrames { get; init; }

    public required double StopAsyncMs { get; init; }
    public required double DisposeAsyncMs { get; init; }
    public required double TotalSeconds { get; init; }
    public required bool ReachedTerminal { get; init; }

    /// <summary>本次是否因源不可定位而跳过定位注入。</summary>
    public required bool SeekSkipped { get; init; }

    /// <summary>定位判定结论。</summary>
    public required SeekVerdict SeekVerdict { get; init; }

    /// <summary>判定时刻的播放位置（用于展示定位后位置漂移）。</summary>
    public required TimeSpan PositionAtVerdict { get; init; }

    public required Exception? Fatal { get; init; }
    public required bool OpenOnly { get; init; }
}

/// <summary>
/// 汇总表与判定段的打印。所有字段按「一眼判读」组织：先给结论，再给支撑数据。
/// </summary>
internal static class ProbeReport
{
    /// <summary>定位落点容差：网络源按关键帧重开连接，落点通常略早于目标。</summary>
    public static readonly TimeSpan SeekPositionTolerance = TimeSpan.FromSeconds(3);

    /// <summary>提前结束容差：Ended 时位置距时长还差这么多即判为提前结束。</summary>
    internal static readonly TimeSpan PrematureEndTolerance = TimeSpan.FromSeconds(1.5);

    /// <summary>打印汇总与判定，返回总体是否通过。</summary>
    public static bool Print(ReportInputs input)
    {
        var observation = input.Observation;
        long videoFrames = observation.VideoFrames;
        long audioFrames = observation.AudioFrames;

        bool openOk = input.Fatal is null;
        bool framesOk = videoFrames > 0 || audioFrames > 0;
        bool exitOk = input.DisposeAsyncMs >= 0;
        bool prematureEnd = IsPrematureEnd(input.LastState, input.LastPosition, input.LastDuration);
        bool terminalOk = input.Fatal is null
                          && input.LastState is MediaState.Ended or MediaState.Stopped
                          && !prematureEnd;

        // 只有「请求了定位、且源确实声明可定位」时才把定位结论计入总体判定。
        // 不可定位流上主动请求定位属于超出验证范围，不该把总体结论拉成失败。
        bool seekRequested = input.Options.SeekTarget is not null;
        bool seekCounts = seekRequested && !input.SeekSkipped;
        bool seekOk = !seekCounts || input.SeekVerdict == SeekVerdict.Pass;

        PrintSummary(input, videoFrames, audioFrames);

        if (!input.OpenOnly)
        {
            if (seekRequested)
                PrintSeekBreakdown(input);
            PrintPipelineSurvival(input);
        }

        Console.WriteLine();
        Console.WriteLine("=== 判定 ===");
        Console.WriteLine($"  打开链路      : {(openOk ? "PASS" : "FAIL（见 [FATAL]）")}");

        bool allPass = openOk && exitOk;
        if (!input.OpenOnly)
        {
            Console.WriteLine($"  出餐链路      : {(framesOk ? $"PASS（视频 {videoFrames} 帧 / 音频 {audioFrames} 帧）" : "FAIL（无任何音视频帧）")}");
            if (seekRequested)
                Console.WriteLine($"  定位链路      : {DescribeSeekLine(input)}");
            Console.WriteLine($"  终态          : {DescribeTerminal(input, prematureEnd)}");
            allPass &= framesOk && terminalOk && seekOk && (input.ReachedTerminal || input.Fatal is not null);
        }

        Console.WriteLine($"  退出链路      : {(exitOk ? "PASS" : "FAIL（DisposeAsync 未完成——若停滞即读超时/挂死排查点）")}");

        Console.WriteLine();
        Console.WriteLine($"=== 总体 {(allPass ? "PASS" : "FAIL")} ===");
        return allPass;
    }

    /// <summary>Ended 但位置离时长还差一截即视为提前结束（播放器没走到结尾就结束）。</summary>
    private static bool IsPrematureEnd(MediaState state, TimeSpan position, TimeSpan duration)
        => state == MediaState.Ended
           && duration > TimeSpan.Zero
           && position + PrematureEndTolerance < duration;

    private static void PrintSummary(ReportInputs input, long videoFrames, long audioFrames)
    {
        var observation = input.Observation;

        Console.WriteLine();
        Console.WriteLine("=== 汇总 ===");
        Console.WriteLine($"  播放终态          : {input.LastState}（停止前快照）");
        Console.WriteLine($"  播放终位置        : {input.LastPosition.TotalSeconds:F2}s（停止前快照）");
        Console.WriteLine($"  时长              : {(input.LastDuration > TimeSpan.Zero ? $"{input.LastDuration.TotalSeconds:F2}s" : "(未知)")}");
        Console.WriteLine($"  视频帧(交付)      : {videoFrames}");
        Console.WriteLine($"  音频帧            : {audioFrames}");
        Console.WriteLine($"  视频丢帧          : {input.DroppedFrames}");
        Console.WriteLine($"  [EXIT] 三段       : StopAsync={input.StopAsyncMs:F0}ms / DisposeAsync={input.DisposeAsyncMs:F0}ms / 总墙钟={input.TotalSeconds:F1}s");

        if (observation.Errors.Count > 0)
        {
            Console.WriteLine($"  错误事件          : {observation.Errors.Count} 次");
            foreach (var error in observation.Errors)
                Console.WriteLine($"      - {error}");
        }
    }

    /// <summary>定位分段：把一次定位切成「发起前 / 返回时 / 之后」三段，逐段给帧数与时刻。</summary>
    private static void PrintSeekBreakdown(ReportInputs input)
    {
        var observation = input.Observation;
        Console.WriteLine();
        Console.WriteLine("=== 定位分段 ===");

        if (input.SeekSkipped)
        {
            Console.WriteLine("  定位              : 跳过（源不可定位，线性播放兜底场景不做定位）");
            return;
        }

        if (!observation.SeekIssued)
        {
            Console.WriteLine("  定位              : 未执行（播放未到稳定点即进入终态）");
            return;
        }

        Console.WriteLine($"  定位目标          : {observation.SeekTarget.TotalSeconds:F2}s（容差 ±{SeekPositionTolerance.TotalSeconds:F1}s）");
        Console.WriteLine($"  定位发起/返回     : t={observation.SeekIssuedAtMs / 1000:F2}s / {FormatMs(observation.SeekReturnedAtMs)}" +
                          $"（耗时 {FormatDeltaMs(observation.SeekIssuedAtMs, observation.SeekReturnedAtMs)}）");
        Console.WriteLine($"  位置              : 发起 {observation.PositionAtIssue.TotalSeconds:F2}s" +
                          $" -> 返回 {observation.PositionAtReturn.TotalSeconds:F2}s" +
                          $" -> 判定时 {input.PositionAtVerdict.TotalSeconds:F2}s");
        Console.WriteLine($"  定位前帧数        : 视频 {observation.VideoFramesAtIssue} / 音频 {observation.AudioFramesAtIssue}");
        Console.WriteLine($"  定位后帧增量      : 视频 {observation.VideoFramesAfterSeek} / 音频 {observation.AudioFramesAfterSeek}");
        Console.WriteLine($"  定位后首帧延迟    : 视频 {FormatFirstFrame(observation.FirstVideoFrameAfterSeekMs, observation.SeekReturnedAtMs)}" +
                          $" / 音频 {FormatFirstFrame(observation.FirstAudioFrameAfterSeekMs, observation.SeekReturnedAtMs)}");
        Console.WriteLine($"  定位后末帧时刻    : 视频 {FormatMs(observation.LastVideoFrameMs)} / 音频 {FormatMs(observation.LastAudioFrameMs)}");
        if (observation.SeekError is not null)
            Console.WriteLine($"  定位调用异常      : {observation.SeekError}");
    }

    /// <summary>定位后管线存活：管线是否还在出帧、是否转入终态、状态跃迁顺序。</summary>
    private static void PrintPipelineSurvival(ReportInputs input)
    {
        var observation = input.Observation;
        Console.WriteLine();
        Console.WriteLine("=== 定位后管线存活 ===");

        if (input.Options.SeekTarget is null || input.SeekSkipped || !observation.SeekIssued)
        {
            Console.WriteLine("  说明              : 本次未执行定位，以下为整段播放的存活情况");
        }

        bool hasSeekWindow = observation.SeekIssued && !input.SeekSkipped;
        Console.WriteLine($"  视频管线          : {DescribePipeline(observation.VideoFramesAfterSeek, observation.LastVideoFrameMs, hasSeekWindow, observation.ElapsedMs, observation)}");
        Console.WriteLine($"  音频管线          : {DescribePipeline(observation.AudioFramesAfterSeek, observation.LastAudioFrameMs, hasSeekWindow, observation.ElapsedMs, observation)}");
        Console.WriteLine($"  定位后完成事件    : {DescribeCompletion(observation, hasSeekWindow)}");

        if (!hasSeekWindow)
        {
            Console.WriteLine($"  定位后终态跃迁    : (不适用)");
        }
        else if (observation.TerminalAfterSeek)
        {
            bool premature = observation.TerminalAfterSeekState == MediaState.Ended
                             && input.LastDuration > TimeSpan.Zero
                             && input.LastPosition + PrematureEndTolerance < input.LastDuration;
            Console.WriteLine($"  定位后终态跃迁    : 是 —— t={observation.TerminalAfterSeekAtMs / 1000:F2}s 转入 {observation.TerminalAfterSeekState}" +
                              $"（距定位返回 {FormatDeltaMs(observation.SeekReturnedAtMs, observation.TerminalAfterSeekAtMs)}）" +
                              (premature ? "，且位置未到时长" : ""));
        }
        else
        {
            Console.WriteLine("  定位后终态跃迁    : 否（定位后未出现 Ended/Stopped/Error）");
        }

        Console.WriteLine($"  状态时间线        : {DescribeTimeline(observation)}");
    }

    private static string DescribePipeline(
        long framesAfterSeek, double lastFrameMs, bool hasSeekWindow, double nowMs, Observation observation)
    {
        if (!hasSeekWindow)
            return lastFrameMs < 0 ? "未出帧" : $"运行中（末帧 t={lastFrameMs / 1000:F2}s）";

        // 定位后转入终态时管线已停，不能再用「运行中」描述，否则会掩盖播放器已死这一事实。
        bool stoppedAfterSeek = observation.TerminalAfterSeek;

        if (framesAfterSeek > 0)
            return stoppedAfterSeek
                ? $"已停止（定位后交付 {framesAfterSeek} 帧，随后 t={observation.TerminalAfterSeekAtMs / 1000:F2}s 转入 {observation.TerminalAfterSeekState}）"
                : $"运行中（定位后 {framesAfterSeek} 帧）";

        if (lastFrameMs < 0)
            return stoppedAfterSeek
                ? $"已停止（定位后零新帧，整段未出帧，t={observation.TerminalAfterSeekAtMs / 1000:F2}s 转入 {observation.TerminalAfterSeekState}）"
                : "停摆（整段未出帧）";

        double silentMs = nowMs - lastFrameMs;
        return stoppedAfterSeek
            ? $"已停止（定位后零新帧，末帧在 {silentMs / 1000:F2}s 前，t={observation.TerminalAfterSeekAtMs / 1000:F2}s 转入 {observation.TerminalAfterSeekState}）"
            : $"停摆（定位后零新帧，末帧在 {silentMs / 1000:F2}s 前）";
    }

    /// <summary>
    /// 定位后是否收到「播放完成」信号。
    /// </summary>
    /// <remarks>
    /// 播放器接口不对外暴露完成事件，完成信号只表现为状态转入 Ended，
    /// 因此这里以「首次观察到 Ended 的时刻」作为它的可观测代理。
    /// </remarks>
    private static string DescribeCompletion(Observation observation, bool hasSeekWindow)
    {
        if (!observation.EndedObserved)
            return "未触发（整段未观察到 Ended 跃迁）";

        string at = $"t={observation.EndedAtMs / 1000:F2}s 转入 Ended";
        if (!hasSeekWindow)
            return $"已触发 —— {at}";

        return observation.EndedAtMs >= observation.SeekReturnedAtMs
            ? $"已触发 —— 定位返回后 {FormatDeltaMs(observation.SeekReturnedAtMs, observation.EndedAtMs)}（{at}）"
            : $"已触发 —— 但在定位返回之前（{at}）";
    }

    private static string DescribeTimeline(Observation observation)
    {
        var timeline = observation.StateTimeline;
        if (timeline.Count == 0)
            return "(无状态变更事件)";

        const int maxEntries = 16;
        var parts = new List<string>(Math.Min(timeline.Count, maxEntries));
        for (int i = 0; i < timeline.Count && i < maxEntries; i++)
            parts.Add($"{timeline[i].From}->{timeline[i].To}@t={timeline[i].AtMs / 1000:F2}s");

        if (timeline.Count > maxEntries)
            parts.Add($"…共 {timeline.Count} 次跃迁（省略中间 {timeline.Count - maxEntries} 次）");

        return string.Join("  ", parts);
    }

    /// <summary>
    /// 定位链路判定行：带 PASS/FAIL/SKIP 前缀，正文区分跳过、未注入、位置不正确、
    /// 位置正确但零新帧、定位后异常终止五种情形。
    /// </summary>
    private static string DescribeSeekLine(ReportInputs input)
    {
        var observation = input.Observation;
        string pos = $"{observation.PositionAtReturn.TotalSeconds:F2}s";
        string target = $"{observation.SeekTarget.TotalSeconds:F2}s";
        string frames = $"视频 {observation.VideoFramesAfterSeek} 帧 / 音频 {observation.AudioFramesAfterSeek} 帧";

        if (input.SeekSkipped)
            return "SKIP —— 源不可定位，本次未注入定位（不计入总体判定）";

        if (!observation.SeekIssued)
            return "FAIL —— 未能注入定位（播放未到注入点即进入终态，定位链路未验证）";

        return input.SeekVerdict switch
        {
            SeekVerdict.Pass =>
                $"PASS —— 位置 {pos} ≈ 目标 {target}，定位后 {frames}",

            SeekVerdict.PositionOkNoFrames =>
                $"FAIL —— 位置正确（{pos} ≈ 目标 {target}）但定位后零新帧，管线停摆（{DescribeSilence(observation)}）",

            SeekVerdict.PositionMismatch =>
                $"FAIL —— 位置不正确（返回 {pos}，目标 {target}，偏差 " +
                $"{Math.Abs(observation.PositionAtReturn.TotalSeconds - observation.SeekTarget.TotalSeconds):F2}s，" +
                $"容差 ±{SeekPositionTolerance.TotalSeconds:F1}s）",

            SeekVerdict.TerminatedAfterSeek =>
                $"FAIL —— 定位后异常终止（t={observation.TerminalAfterSeekAtMs / 1000:F2}s 转入 {observation.TerminalAfterSeekState}，" +
                $"距定位返回 {FormatDeltaMs(observation.SeekReturnedAtMs, observation.TerminalAfterSeekAtMs)}，定位后 {frames}）",

            SeekVerdict.SeekThrew =>
                $"FAIL —— 定位调用抛出异常：{observation.SeekError}",

            _ => $"FAIL —— 未判定（位置 {pos}，定位后 {frames}）",
        };
    }

    /// <summary>零新帧时说明最后一次交付距今多久；整段无帧时直接说明，避免出现无意义的负数。</summary>
    private static string DescribeSilence(Observation observation)
    {
        double lastMs = Math.Max(observation.LastVideoFrameMs, observation.LastAudioFrameMs);
        if (lastMs < 0)
            return "整段未交付任何帧";
        return $"末帧在 {(observation.ElapsedMs - lastMs) / 1000:F2}s 前";
    }

    private static string DescribeTerminal(ReportInputs input, bool prematureEnd)
    {
        if (input.Fatal is not null)
            return "FAIL（运行中抛出异常，见 [FATAL]）";

        if (prematureEnd)
            return $"FAIL（Ended 但位置 {input.LastPosition.TotalSeconds:F2}s 未到时长 {input.LastDuration.TotalSeconds:F2}s —— 提前结束）";

        return input.LastState switch
        {
            MediaState.Ended => "PASS（自然结束）",
            MediaState.Stopped => "PASS（已停止）",
            _ => $"FAIL（终态={input.LastState}）",
        };
    }

    private static string FormatMs(double milliseconds)
        => milliseconds < 0 ? "(未发生)" : $"t={milliseconds / 1000:F2}s";

    private static string FormatDeltaMs(double fromMs, double toMs)
    {
        if (fromMs < 0 || toMs < 0)
            return "(未知)";
        return $"{toMs - fromMs:F0}ms";
    }

    /// <summary>首帧输出的两种读法：相对定位返回的延迟，以及它在整段播放中的绝对时刻。</summary>
    private static string FormatFirstFrame(double frameMs, double referenceMs)
    {
        if (frameMs < 0)
            return "(未交付)";
        string at = $"t={frameMs / 1000:F2}s";
        return referenceMs < 0 ? at : $"{frameMs - referenceMs:F0}ms（{at}）";
    }
}

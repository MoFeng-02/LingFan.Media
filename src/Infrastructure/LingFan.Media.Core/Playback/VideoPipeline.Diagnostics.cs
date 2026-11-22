using System;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

// VideoPipeline 的 Diagnostics 职责段（部分类，由 VideoPipeline.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/StartCore/PipelineLoop/DecodeLoop/Dispose）在主文件；
// 呈现消费者与解码生产者双循环共享 _runId/_cts 会话状态，拆分不改变状态归属与线程模型。
public sealed partial class VideoPipeline
{
    /// <summary>启动冻结看门狗（独立诊断线程）：每 2s 打一条管线心跳快照，管线卡死时仍持续输出。</summary>
    private void StartFreezeWatchdog()
    {
        // 判「同一代看门狗是否在跑」：存活标记与令牌源必须同时匹配才早退。
        // 按引用非空早退会让取消退出后的重建永久失效；只按任务未完成早退则会在
        // 首次 await 之后恒判为已完成（外层包装任务），导致每次启动多起一条循环。
        var cts = _cts;
        if (_freezeWatchdogRunning && ReferenceEquals(_freezeWatchdogCts, cts))
            return;
        _freezeWatchdogCts = cts;
        _freezeWatchdogRunning = true;
        _loopHeartbeatQpc = _sinkEnterQpc = _sinkExitQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        _decodeHeartbeatQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        _decodePhase = 0;
        _pipelinePhase = 0;
        CancellationToken ct = cts.Token;
        _freezeWatchdogTask = Task.Factory.StartNew(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    double loopIdleSec = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _loopHeartbeatQpc)).TotalSeconds;
                    double sinkIdleSec = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _sinkExitQpc)).TotalSeconds;
                    double decodeIdleSec = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _decodeHeartbeatQpc)).TotalSeconds;
                    // 呈/解线程存活状态：心跳停更既可能是卡死也可能是线程已自然退出（EOS 收尾），
                    // 不加此列会把「已退出」误诊为「死锁」（实测结论）。
                    string alive = $"呈线程={(IsRunning ? "运行" : "已退出")} 解线程={(_decodeTask?.IsCompleted == true ? "已退出" : "运行")}";
                    // 异常升级：任一心跳停更超 1s（且对应线程仍标注运行中）时以 Warning 级输出，
                    // 使「卡死在某个不打日志的调用里」的场景在默认日志级别下可见；
                    // 正常心跳保持 Trace 级，不产生常态噪声。
                    bool loopStalled = loopIdleSec > 1.0 && IsRunning;
                    bool decodeStalled = decodeIdleSec > 1.0 && _decodeTask?.IsCompleted == false;
                    string message =
                        $"[FREEZE] 循环心跳={loopIdleSec:F2}s前 解码心跳={decodeIdleSec:F2}s前 master={_synchronizer.GetCurrentMasterTime():g} 队列={_frameQueue.Count} 已呈={_presentedCount} " +
                        $"sinkInFlight={Volatile.Read(ref _sinkInFlight)} sink末次返回={sinkIdleSec:F2}s前 阶段(呈/解)={_pipelinePhase}/{_decodePhase} {alive}";
                    if (loopStalled || decodeStalled)
                        _logger.LogWarning(message);
                    else
                        _logger.LogTrace("{Message}", message);

                    // [SYNC] 节拍快照：输出呈现线程发布的窗口统计。日志写入在本（看门狗）线程
                    // 执行，呈现线程零 I/O；写入耗时自测量随下一条快照输出——若单次写入达
                    // 数十 ms，即坐实"诊断日志自身制造迟到帧"的通道成本。
                    var stats = Volatile.Read(ref _publishedPacingStats);
                    if (stats is not null && stats.PublishedQpc != Volatile.Read(ref _lastEmittedStatsQpc))
                    {
                        Volatile.Write(ref _lastEmittedStatsQpc, stats.PublishedQpc);
                        long writeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        _logger.LogInformation(
                            "[SYNC] 快照 master={Master:g} 队列={Queue} 已呈={Presented} 累计丢={Dropped} " +
                            "窗口帧数={Window} 呈现均耗时={AvgMs:F1}ms 上帧={LastMs:F1}ms " +
                            "间隔均值={IvAvg:F1}ms/峰值={IvMax:F1}ms 迟到={Late}帧(峰值{LateMax:F1}ms) " +
                            "队空等待={QeWait:F1}ms GC={Gc} 时钟比={ClockRatio:F3} " +
                            "PTS间距={PtsAvg:F1}ms/峰{PtsMax:F1}ms 快照写入={WriteMs:F1}ms",
                            _synchronizer.GetCurrentMasterTime(), _frameQueue.Count,
                            _presentedCount, Interlocked.Read(ref _droppedFrames),
                            stats.WindowFrames, stats.AvgPresentMs, stats.LastPresentMs,
                            stats.AvgIntervalMs, stats.MaxIntervalMs, stats.LateFrames, stats.LateMaxMs,
                            stats.QueueEmptyWaitMs, stats.GcDelta, stats.MasterClockRatio,
                            stats.PtsSpacingAvgMs, stats.PtsSpacingMaxMs,
                            _lastSnapshotWriteMs);
                        _lastSnapshotWriteMs = System.Diagnostics.Stopwatch.GetElapsedTime(writeStart).TotalMilliseconds;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 停止/释放时正常退出。
            }
            finally
            {
                // 只清自己这一代的标记：取消换代后新一轮看门狗已接管，
                // 旧循环收尾不得把新一轮的存活标记抹掉。
                if (ReferenceEquals(_freezeWatchdogCts, cts))
                    _freezeWatchdogRunning = false;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// 等待视频帧队列预滚动到 <see cref="VideoPrerollFrames"/> 帧（或超时/解码结束）。
    /// 由 <see cref="MediaPipelineHost.StartAsync"/> 在 <see cref="Start"/> 之后、**启动音频设备之前** await，
    /// 使主时钟（取自音频播放游标）在视频已有帧可呈现后才起跑。
    /// </summary>
    /// <remarks>
    /// <para>这是「重播先卡一下」的解决点：重播时 demuxer 重定位 + 解码器 Reset 需较长时间，
    /// 若音频先起跑，这段空窗会被计入播放时间线，导致开头帧被判 Drop、画面停在上次末帧后突跳。
    /// 把等待前移到音频启动前，该开销就落在「点击播放到出声」之间，用户感知为正常起播延迟而非卡顿。</para>
    /// <para>首播路径下 <c>BufferManager</c> 已在 <c>OpenAsync</c> 阶段暖好包队列，本方法通常在
    /// 一两个轮询周期内立即返回，无回归。</para>
    /// <para>三条提前返回路径确保绝不卡死播放：取消、解码已结束（极短流/无帧）、超时（<see cref="VideoPrerollTimeoutMs"/>）。</para>
    /// </remarks>
    public async Task WaitForPrerollAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
            return;

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            if (cancellationToken.IsCancellationRequested || _cts.IsCancellationRequested)
                return;
            if (_frameQueue.Count >= VideoPrerollFrames)
                return;
            // 解码生产者已收尾（流极短或异常）：再等也不会有帧，立即放行。
            if (_decodeDone || _frameQueue.IsCompleted)
                return;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds > VideoPrerollTimeoutMs)
            {
                _logger.LogWarning(
                    "视频预滚动等待超时（{Timeout}ms，队列仅 {Count} 帧），继续启动音频以免阻塞播放。",
                    VideoPrerollTimeoutMs, _frameQueue.Count);
                return;
            }

            try
            {
                await Task.Delay(5, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 等待视频首帧真正上屏（<see cref="ProcessFrame"/> 首次 Present 调用返回）。供
    /// <see cref="MediaPipelineHost.StartAsync"/> 在启动音频前 await，使音频 WASAPI 出声不早于
    /// 视频首帧上屏（A/V 启动对齐）。带超时兜底，绝不阻塞播放。
    /// </summary>
    /// <remarks>无视频轨（<c>VideoPipeline</c> 为 null）或被提前释放时，调用方判空不会走到这里。</remarks>
    public async Task WaitForFirstFramePresentedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_firstFramePresented) return;
        var tcs = _firstFramePresentedTcs;
        if (tcs == null) return;
        try { await Task.WhenAny(tcs.Task, Task.Delay(timeout, cancellationToken)); }
        catch (OperationCanceledException) { }
    }
}

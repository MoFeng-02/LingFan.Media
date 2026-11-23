using System;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

/// <summary>
/// VideoPipeline 的呈现节拍分部：ProcessFrame 同步判定与呈现、WaitUntilDue 等待与过调探针。
/// 字段与生命周期编排在主文件 VideoPipeline.cs。
/// </summary>
public sealed partial class VideoPipeline
{
    private void ProcessFrame(VideoFrame frame)
    {
        // 每帧耗时实测（含 Present + 帧资源归池）：进快照均值
        long presentStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            ProcessFrameCore(frame);
        }
        finally
        {
            _lastPresentMs = System.Diagnostics.Stopwatch.GetElapsedTime(presentStart).TotalMilliseconds;
            _presentMsAccum += _lastPresentMs;
            // 呈现节拍：本次 Present 与上次 Present 的间隔（理想 ≈ 内容帧间隔）。
            // 峰值显著偏大 = 存在迟到呈现/短暂停顿，画面节拍不匀的直接证据。
            if (_lastPresentQpc != 0)
            {
                double intervalMs = System.Diagnostics.Stopwatch.GetElapsedTime(_lastPresentQpc, presentStart).TotalMilliseconds;
                _presentIntervalAccum += intervalMs;
                if (intervalMs > _presentIntervalMaxMs)
                    _presentIntervalMaxMs = intervalMs;
            }
            _lastPresentQpc = presentStart;
            _presentedCount++;     // 播放以来累计（快照中「已呈」展示用）
            _presentedInWindow++;  // 本快照窗口内计数（「呈现均耗时」的分母）
        }
    }

    private void ProcessFrameCore(VideoFrame frame)
    {
        // 仅处理「呈现」分支。同步决策（Wait / Drop）已上移到 PipelineLoop 的
        // Peek-then-Dequeue 逻辑：Wait 时帧留队头等待时钟追近，Drop 时取走并归还过期帧。此处被调用即表示队头帧已判定为 Present。
        // 呈现提前量每帧同步：渲染器可在运行期更新 PresentationLatency（如按实测显示刷新率
        // 自适应），每帧取最新值保证 CheckVideoFrame / WaitUntilDue / 呈现误差三者判据一致。
        _synchronizer.PresentationLatency = _renderer.PresentationLatency + _manualLeadOffset;

        // 呈现误差：相对"最早可呈现时刻"(frame.Timestamp − 真实上屏延迟)的偏移。
        // Peek 方案下理想值 ≈ 0~2ms（WaitUntilDue 自旋收口精度）；若该值显著 >0
        // 说明帧在队头等到了过期后才被取走呈现（仍被某处阻塞）。
        // 常开统计（成本=两次无锁时钟读）：超阈值计入迟到帧数/峰值，进 [SYNC] 快照——
        // 迟到呈现使画面重复一拍但不进丢帧计数，是"肉眼卡顿而其他指标全绿"时的关键信号。
        // 必须在 ReturnFrame 之前计算：帧归还池后 Timestamp 可能被复用改写。
        var thr = _synchronizer.PresentationLatency;
        var masterNow = _synchronizer.GetCurrentMasterTime();
        double errMs = (masterNow - (frame.Timestamp - thr)).TotalMilliseconds;
        if (errMs > LatePresentThresholdMs)
        {
            _latePresentsInWindow++;
            if (errMs > _lateMaxMs)
                _lateMaxMs = errMs;
        }
        // 视频 PTS 间距逐帧统计：呈现完全跟随内容 PTS——本统计与间隔均值的对照可区分
        // 「内容帧率/PTS 抖动（卡顿源于素材）」与「管线 pacing 问题」。
        if (_hasLastPresentPts)
        {
            double ptsGapMs = (frame.Timestamp - _lastPresentPts).TotalMilliseconds;
            if (ptsGapMs > 0)
            {
                _ptsSpacingAccum += ptsGapMs;
                _ptsSpacingCount++;
                if (ptsGapMs > _ptsSpacingMaxMs)
                    _ptsSpacingMaxMs = ptsGapMs;
            }
        }
        _lastPresentPts = frame.Timestamp;
        _hasLastPresentPts = true;
        if (PacingDiagnostics.Enabled)
        {
            string? report = PacingDiagnostics.Present.OnPresent(frame.Timestamp, _frameQueue.Count, errMs);
            if (report != null)
            {
                _logger.LogInformation("[PACING] {Report}", report);
                _logger.LogInformation("[CLOCK] {Snapshot}", PacingDiagnostics.Clock.Snapshot());
            }
        }

        // A/V 同步诊断（LINGFAN_SYNC_DIAG=1）：**独立于 PacingDiagnostics**，呈现瞬间记录
        // 「视频帧 PTS − 音频主时钟」= 用户实际感知到的音画偏差。delta>0 ⇒ 视频领先音频（画面先到）；
        // delta<0 ⇒ 视频落后音频（声音先到）。前 8 帧全采以暴露起始偏移；之后每 500ms 采样一次，
        // 观察是否单调增长（时钟速率漂移）或长期恒定≈某值（固定偏移 = 视频/音频时间线起点不齐，
        // 典型为 MF/MP4 edit list 使视频流 PTS 起点与音频时钟原点错开）。
        // 纯观测：SyncDiagEnabled 恒 false 时整块跳过，且不依赖 PacingDiagnostics.Enabled。
        if (SyncDiagEnabled)
        {
            // masterNow 复用本方法前段呈现误差计算的读取值（同一呈现瞬间，且避免重复声明）。
            double syncDeltaMs = (frame.Timestamp - masterNow).TotalMilliseconds;
            long nowTicks = DateTime.UtcNow.Ticks;
            if (_presentCount < 8 || nowTicks - _lastSyncDiagTicks > 5_000_000L) // 前 8 帧 + 每 500ms
            {
                _logger.LogInformation(
                    "[SYNC] present videoPTS={V:g} audioClock={A:g} delta={D,7:F1}ms queue={Q} pidx={P}",
                    frame.Timestamp, masterNow, syncDeltaMs, _frameQueue.Count, _presentCount);
                _lastSyncDiagTicks = nowTicks;
            }
            _presentCount++;
        }

        try
        {
            // 唯一帧出口（帧契约）：所有帧经注入的 _videoFrameSink（生产路径中恒为非空 lambda，
            // MediaPlayer 注入为 frame => _frameChannel.Emit，内部再扇出到订阅的 Sink——
            // 无头计算 / Skia 软渲染 / D3D11 零拷贝 GPU 呈现三者互斥，由 Sink 内部路由）。
            // 绝不在管线内直接 _renderer.Present(frame)（曾经的 else 兜底分支已删除：它构成第二条
            // 绕开 FrameChannel 扇出的呈现路径，违反「帧路由唯一、绝双路径」原则）。
            // 若 _videoFrameSink 为 null（仅测试/无 UI 且未接 Sink 的调用方），帧在此静默归池、不呈现。
            // 注意：路由决策在 Sink lambda 内完成，不可在此判 _videoFrameSink == null 来决定 D3D11——
            // 那样会让 D3D11 模式（无订阅方，但 lambda 非空）永不调用渲染器，导致视频不显示。
            // 冻结看门狗：sink 链（Emit→OnFrame→PresentFrame→renderer.Present→TryWriteFrame）为同步调用，
            // 在途深度 + 进入时刻暴露该链是否卡死。
            long sinkStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _pipelinePhase = PipelinePhaseSink;
            Interlocked.Exchange(ref _sinkEnterQpc, System.Diagnostics.Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _sinkInFlight);
            try
            {
                _videoFrameSink?.Invoke(frame);
            }
            finally
            {
                Interlocked.Decrement(ref _sinkInFlight);
                Interlocked.Exchange(ref _sinkExitQpc, System.Diagnostics.Stopwatch.GetTimestamp());
            }
            // 呈现段尖峰：sink 链（含渲染器帧间驱动调用与帧资源归还前的导入/退役开销）单帧
            // 超过阈值即限频告警——与队空等待形成归因对照（本线程阻塞 vs 解码供帧不及）。
            double sinkMs = System.Diagnostics.Stopwatch.GetElapsedTime(sinkStart).TotalMilliseconds;
            if (sinkMs > SinkSpikeLogThresholdMs &&
                System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastSpikeLogQpc)).TotalMilliseconds > 3000)
            {
                Volatile.Write(ref _lastSpikeLogQpc, System.Diagnostics.Stopwatch.GetTimestamp());
                _logger.LogWarning(
                    "[SYNC] 呈现段尖峰 {SinkMs:F1}ms 队列={Q} —— 帧间驱动调用（AHB 导入/退役销毁）或同线程 GC 停顿，为迟到帧直接来源",
                    sinkMs, _frameQueue.Count);
            }
            // 首帧已真正提交上屏：通知 A/V 启动编排等待点，
            // 使音频 WASAPI 启动不早于视频首帧上屏 → 解决「声音比视频先出」。
            if (!_firstFramePresented)
            {
                _firstFramePresented = true;
                _firstFramePresentedTcs?.TrySetResult(true);
            }
        }
        finally
        {
            _pipelinePhase = PipelinePhaseReturn;
            ReturnFrame(frame); // Present 后归还到池
            _pipelinePhase = 0;
        }
    }

    /// <summary>
    /// 同步精确等待直到视频帧到达呈现时刻（<c>frameTimestamp − PresentationLatency</c>）。
    /// 仅在 LongRunning 专用实时线程上调用（热路径零 await，无续体调度延迟）。
    /// </summary>
    /// <remarks>
    /// <para>进入时把「due 点」一次性换算为<b>绝对墙钟年线</b>（音频主时钟 ≈ 1× 实时），睡眠
    /// 分段（单片 ≤16ms，逐轮减半收细醒点）与尾部自旋都以该年线收口——主时钟的窗内摆动、
    /// 记账步进回跳不再扰动醒点（master 读数仅保留给停摆看门狗）。每次调用以进入时刻的
    /// master 重新换算年线，master 的长期速率仍主导呈现节拍（音画同步不变）。</para>
    /// <para>停摆看门狗为<b>跨调用锚点</b>：master 推进 ≥ 墙钟 50% 视为健康（重锚；从停摆恢复
    /// 时复位降级标志），推进 &lt; 墙钟 10% 且跨调用累计等待超过宽限期（首判 500ms，确认后
    /// 50ms）判定停摆，立即返回交由 Wait 分支限速出帧——宁可轻微不同步也绝不冻结。</para>
    /// <para>与 <see cref="Synchronizer.CheckVideoFrame"/> 使用同一主时钟源与同一阈值，判据一致。</para>
    /// </remarks>
    private void WaitUntilDue(TimeSpan frameTimestamp, CancellationToken ct = default)
    {
        const double tailMs = 1.5;
        // 呈现目标 = frameTimestamp − 真实上屏延迟：帧在 audioClock 到达 PTS 前「本延迟」时刻调用 Present，
        // 像素恰在 PTS 时可见（vsync）。此前错误用 SyncThreshold(50ms) → 视频系统性提前。
        var threshold = _synchronizer.PresentationLatency;
        long targetQpc = 0;

        // 绝对墙钟年线：进入时一次性换算（100ns ticks）。remaining ≤ 0（帧已过期，如停摆降级、
        // 追帧）→ 年线取当前时刻，自旋立即退出，本帧即时呈现。
        long nowQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        double remainingAtEntry = (frameTimestamp - threshold - _synchronizer.GetCurrentMasterTime()).TotalMilliseconds;
        long dueWallQpc = nowQpc + (long)(Math.Max(remainingAtEntry, 0) * 10_000);

        // 停摆看门狗锚点：首次调用建立，之后跨调用累计（见方法 remarks）。
        if (!_stallWatchAnchored)
        {
            _stallWatchAnchored = true;
            _stallWatchQpc = nowQpc;
            _stallWatchMaster = _synchronizer.GetCurrentMasterTime();
        }

        // 分段睡眠：醒点误差 = 末段睡眠的调度抖动（单片 ≤16ms），远小于整段长睡的抖动。
        int segmentMs = 16;
        while (true)
        {
            if (ct.IsCancellationRequested) return;
            nowQpc = System.Diagnostics.Stopwatch.GetTimestamp();
            double wallRemainingMs = (dueWallQpc - nowQpc) / 10_000.0;
            if (wallRemainingMs <= tailMs)
            {
                // 尾部自旋收口目标 = 绝对墙钟年线（进入时定格）：master 的窗内摆动不再推迟醒点。
                targetQpc = dueWallQpc;
                break;
            }

            var master = _synchronizer.GetCurrentMasterTime();
            double waitedMs = System.Diagnostics.Stopwatch.GetElapsedTime(_stallWatchQpc).TotalMilliseconds;
            double advancedMs = (master - _stallWatchMaster).TotalMilliseconds;

            if (!_masterClockEverAdvanced)
            {
                if (advancedMs >= 1.0)
                {
                    // 起播瞬态结束：主时钟首次观测到推进（音频设备起跑），回到正常 PTS 同步。
                    _masterClockEverAdvanced = true;
                    _stallWatchQpc = nowQpc;
                    _stallWatchMaster = master;
                    if (waitedMs > 100.0)
                        _logger.LogInformation(
                            "[SYNC] 主时钟起跑（起播保持 {HeldMs:F0}ms），呈现回到 PTS 同步。", waitedMs);
                    // 保持期的旧年线已失效（按冻结 master 换算），按当前 master 重算后正常等待。
                    dueWallQpc = nowQpc + (long)(Math.Max((frameTimestamp - threshold - master).TotalMilliseconds, tailMs) * 10_000);
                    continue;
                }

                if (_firstFramePresented && !_masterClockStalled)
                {
                    // 起播保持：门控放行后音频设备仍在启动（设计内时序，master 冻结 ≠ 停摆）。
                    // 首帧海报【不】参与保持（其 Wait 时 _firstFramePresented 尚未置位，直接走
                    // 正常等待立即上屏）——WaitForFirstFramePresentedAsync 才能及时返回，窗口不黑屏。
                    // 音频启动失败且时钟无任何推进时由保持预算兜底降级，保底出帧。
                    if (waitedMs > StartupClockHoldTimeoutMs)
                    {
                        _masterClockStalled = true;
                        _logger.LogWarning(
                            "[SYNC] 起播保持超时：{Waited:F0}ms 内主时钟无推进（master={Master}），降级按宽限期出帧。",
                            waitedMs, master);
                        _stallWatchQpc = System.Diagnostics.Stopwatch.GetTimestamp();
                        _stallWatchMaster = master;
                        return;   // 立即呈现（进入 50ms 降级节拍）
                    }
                    double remainingNowMs = (frameTimestamp - threshold - master).TotalMilliseconds;
                    if (remainingNowMs <= tailMs)
                    {
                        targetQpc = nowQpc;
                        break;
                    }
                    dueWallQpc = nowQpc + (long)(remainingNowMs * 10_000);
                    Thread.Sleep(segmentMs);
                    segmentMs = Math.Max(1, segmentMs / 2);
                    continue;
                }
                // 已因保持超时进入降级（时钟始终未起跑）：落入下方既有停摆逻辑按 50ms 限速。
            }

            if (advancedMs < 0)
            {
                // master 回跳 = 时间线重建（音频管线重启/定位复位 后重锚点、Seek）的合法形态，
                // 不是停摆：重锚累计，避免跨调用等待把合法跳变误判为停摆。
                _stallWatchQpc = nowQpc;
                _stallWatchMaster = master;
            }
            else
            {
                // 健康/恢复判定：master 以接近实时的速率推进（≥墙钟 50%）→ 重锚；从停摆恢复时
                // 复位降级标志，回到正常同步等待。
                if (advancedMs * 2 >= waitedMs)
                {
                    _stallWatchQpc = nowQpc;
                    _stallWatchMaster = master;
                    if (_masterClockStalled)
                    {
                        _masterClockStalled = false;
                        _logger.LogInformation("[SYNC] 主时钟已恢复推进，同步等待回到正常模式。");
                    }
                }
                else if (waitedMs > (_masterClockStalled ? 50.0 : 500.0) && advancedMs * 10 < waitedMs)
                {
                    // 停摆判定（速率）：master 推进 < 墙钟 × 10%（对冻结/半冻结/极慢一律触发）。
                    if (!_masterClockStalled)
                    {
                        _masterClockStalled = true;
                        _logger.LogWarning(
                            "[SYNC] 主时钟停摆：等待 {Waited:F0}ms 内主时钟仅前进 {Advanced:F1}ms（master={Master}），" +
                            "放弃等待直接呈现帧 PTS={Pts}，避免画面永久冻结。后续帧改用 50ms 宽限期降级出帧。",
                            waitedMs, advancedMs, master, frameTimestamp);
                    }
                    // 重锚：已在停摆降级态，后续调用以 50ms 宽限期重新累计，维持限速出帧节拍。
                    _stallWatchQpc = System.Diagnostics.Stopwatch.GetTimestamp();
                    _stallWatchMaster = master;
                    return;   // 立即呈现（跳过尾部自旋收口）
                }
            }

            // 分段睡眠（按墙钟年线预算）：同时不超过停摆宽限期余量，保证看门狗有机会触发
            // （master 停摆时墙钟年线可能仍远，若无余量上限将整段睡穿宽限期）。
            double graceMs = _masterClockStalled ? 50.0 : 500.0;
            double graceRemainingMs = Math.Max(graceMs - waitedMs, 1.0);
            double sleepBudget = Math.Min(wallRemainingMs - tailMs, Math.Min(graceRemainingMs, segmentMs));
            segmentMs = Math.Max(1, segmentMs / 2);
            Thread.Sleep((int)Math.Ceiling(sleepBudget));
        }

        // 尾部：自旋到绝对墙钟年线（deadline 恒会到来，无死自旋风险），零跨线程时钟读取。
        while (System.Diagnostics.Stopwatch.GetTimestamp() < targetQpc)
        {
            if (ct.IsCancellationRequested) return;
            System.Threading.Thread.SpinWait(10);
        }

        // 等待过调探针：醒来时已越过墙钟年线超过阈值 ⇒ 呈现线程被抢占（GL 桥/解码/合成器
        // 争抢大核）或睡眠过冲——这是"间隔峰值/迟到帧"的最后一块无观测盲区（errMs 大但
        // sink/队空/GC 均干净的唯一剩余解释）。限频 3s；过调样本用于评估呈现线程优先级与调度配置。
        double overshootMs = System.Diagnostics.Stopwatch.GetElapsedTime(targetQpc).TotalMilliseconds;
        if (overshootMs > WaitOvershootLogThresholdMs &&
            System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastOvershootLogQpc)).TotalMilliseconds > 3000)
        {
            Volatile.Write(ref _lastOvershootLogQpc, System.Diagnostics.Stopwatch.GetTimestamp());
            _logger.LogWarning(
                "[SYNC] 等待过调 {OvershootMs:F1}ms PTS={Pts:g} —— 呈现线程被抢占或睡眠过冲，该帧将迟到同量级",
                overshootMs, frameTimestamp);
        }
    }

    /// <summary>
    /// 时间兜底输出节奏报告（仅诊断路径调用）。
    /// </summary>
    /// <remarks>
    /// 画面冻结时所有帧都走 Wait / Drop，<c>OnPresent</c> 不被调用 ⇒ 按次数触发的报告永不出现。
    /// 恰恰是这种时刻的现场最关键，故 Wait / Drop 分支轮询本方法强制出报告。
    /// </remarks>
    private void LogPacingIfDue()
    {
        string? report = PacingDiagnostics.Present.PollReport();
        if (report == null)
            return;

        _logger.LogInformation("[PACING] {Report}", report);
        _logger.LogInformation("[CLOCK] {Snapshot}", PacingDiagnostics.Clock.Snapshot());
    }
}

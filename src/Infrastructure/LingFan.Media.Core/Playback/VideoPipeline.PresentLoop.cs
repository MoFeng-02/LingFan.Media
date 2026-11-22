using System;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

// VideoPipeline 的呈现消费者循环职责段（部分类，按职责拆分，编译期等价）。
// Peek-then-Dequeue 同步判定与呈现节拍；共享 _runId/_cts 会话状态在主文件，线程模型不变。
public sealed partial class VideoPipeline
{
    /// <summary>
    /// 呈现消费者循环（单读线程，<see cref="FrameQueue"/> 以 <c>SingleReader=true</c> 创建，
    /// 本循环是唯一读者）。采用 **Peek-then-Dequeue**：队头帧不出队就先判定同步动作，
    /// 仅当判 <see cref="SyncAction.Present"/> 才真正取走呈现；判 <see cref="SyncAction.Wait"/>
    /// 时帧**留队头**等待时钟追近（<c>continue</c> 后下一轮重新判定），绝不把超前帧塞回队尾 ——
    /// 消除「超前帧塞队尾→取到更超前帧→队列轮转、期间零呈现→卡完突然向前」的队列轮转缺陷。
    /// 解码延迟已被 <see cref="DecodeLoop"/> 完全隔离，呈现节拍不受解码抖动污染。
    /// </summary>
    private void PipelineLoop()
    {
        // 本轮循环的运行代号：定位复位后旧循环可能仍在收尾，其收尾动作与完成上报都必须
        // 只对当轮生效，否则会把新一轮的状态改坏。
        int runId = Volatile.Read(ref _runId);
        try
        {
            // 视频呈现是实时循环：提升专用线程优先级到 Highest，降低 OS/GC 抢占造成的帧间墙钟抖动。
            // LongRunning 专用线程上该优先级全程有效；Thread.Priority 是跨平台 BCL API，各平台同样受益
            // （设置失败静默降级，与 AudioPipeline 喂帧线程同款处理）。
            try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest; }
            catch { }
            while (!_cts.IsCancellationRequested)
            {
                // 复位后旧轮作废：线程尚未退出也不得再呈现帧或消费队列。
                if (Volatile.Read(ref _runId) != runId)
                    break;

                // 冻结看门狗心跳：每轮循环体入口打卡（含空队列自旋/等待分支返回）。
                Interlocked.Exchange(ref _loopHeartbeatQpc, System.Diagnostics.Stopwatch.GetTimestamp());

                if (_isPaused)
                {
                    _pauseAckTcs?.TrySetResult(true);
                    Thread.Sleep(10);
                    continue;
                }

                // 首帧门控：音频设备（主时钟源）启动前，**不做任何同步判定/呈现**。
                // 门控放行发生在音频启动之前（起播瞬态设计，见 MediaPipelineHost.StartAsync）；
                // 放行后主时钟仍冻结的窗口由 WaitUntilDue 的「起播保持」兜住：帧等 master 追上
                // PTS 才呈现，不前导不爆发推进；主时钟起跑后从 PTS=0 起同刻呈现，衔接无跳变。
                if (!_audioGateOpened)
                {
                    long gateStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    while (!_audioReadySignaled)
                    {
                        if (_cts.IsCancellationRequested)
                            break;
                        // 门控等待期间仍需应答 定位复位 的暂停确认，避免 FlushAsync 白等 50ms 走慢速路径。
                        if (_isPaused)
                        {
                            _pauseAckTcs?.TrySetResult(true);
                        }
                        if (System.Diagnostics.Stopwatch.GetElapsedTime(gateStart).TotalMilliseconds > AudioGateTimeoutMs)
                        {
                            _logger.LogWarning(
                                "视频首帧门控等待音频就绪超时（{Timeout}ms），降级直接呈现以免画面永久冻结。",
                                AudioGateTimeoutMs);
                            break;
                        }
                        Thread.Sleep(2);
                    }
                    _audioGateOpened = true;
                    if (_cts.IsCancellationRequested)
                        break;
                    continue;   // 重新走一轮，确保暂停/取消状态在放行后被重新评估
                }

                // 主时钟节拍窗口（每 2s）：master 应以 1× 实时推进、队列应小且流动、呈现耗时应 <10ms。
                // 窗口统计由呈现线程发布（纯内存引用替换，零 I/O），由冻结看门狗线程读取并输出——
                // 日志写入（Console/Debug 双 provider → logcat/调试器）单次可达数十 ms，周期性落在
                // 呈现线程会制造它自己所度量的迟到帧。
                long snapQpc = System.Diagnostics.Stopwatch.GetTimestamp();
                if (System.Diagnostics.Stopwatch.GetElapsedTime(_pacingSnapshotQpc).TotalMilliseconds >= 2000)
                {
                    _pacingSnapshotQpc = snapQpc;
                    // 均值必须用**窗口内**帧数做分母（_presentedInWindow），不能用累计 _presentedCount。
                    // 帧间隔均值用窗口内间隔数（= 窗口帧数 − 1）做分母；间隔/迟到是呈现节拍抖动的
                    // 直接观感——理想间隔≈内容帧间隔，峰值显著偏大即该帧迟到（画面重复一拍）。
                    int windowFrames = _presentedInWindow;
                    double avgPresentMs = windowFrames > 0 ? _presentMsAccum / windowFrames : 0;
                    double avgIntervalMs = windowFrames > 1 ? _presentIntervalAccum / (_presentedInWindow - 1) : 0;
                    // GC 代际窗口差：STW 停顿是呈现线程偶发阻塞的候选来源之一，计数差与
                    // 迟到/间隔峰值逐窗对照即可定量归因。
                    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                    string gcDelta = $"{g0 - _lastGcGen0}/{g1 - _lastGcGen1}/{g2 - _lastGcGen2}";
                    _lastGcGen0 = g0;
                    _lastGcGen1 = g1;
                    _lastGcGen2 = g2;
                    // 主时钟速率对表：窗口内 master 增量 / 墙钟增量（同线程同源 Stopwatch 测量）。
                    double masterNow = _synchronizer.GetCurrentMasterTime().TotalMilliseconds;
                    double wallDeltaMs = System.Diagnostics.Stopwatch.GetElapsedTime(_lastPublishWallQpc).TotalMilliseconds;
                    double clockRatio = 0;
                    if (_hasLastPublish && wallDeltaMs > 100)
                        clockRatio = (masterNow - _lastPublishMaster) / wallDeltaMs;
                    _lastPublishMaster = masterNow;
                    _lastPublishWallQpc = snapQpc;
                    _hasLastPublish = true;
                    double ptsSpacingAvgMs = _ptsSpacingCount > 0 ? _ptsSpacingAccum / _ptsSpacingCount : 0;
                    double queueEmptyWaitMs = _queueEmptyWaitMsAccum;
                    _queueEmptyWaitMsAccum = 0;
                    Volatile.Write(ref _publishedPacingStats, new PacingWindowStats(
                        windowFrames, avgPresentMs, _lastPresentMs,
                        avgIntervalMs, _presentIntervalMaxMs,
                        _latePresentsInWindow, _lateMaxMs,
                        queueEmptyWaitMs, gcDelta, clockRatio,
                        ptsSpacingAvgMs, _ptsSpacingMaxMs, snapQpc));
                    _ptsSpacingAccum = 0;
                    _ptsSpacingMaxMs = 0;
                    _ptsSpacingCount = 0;
                    _presentMsAccum = 0;
                    _presentedInWindow = 0;
                    _presentIntervalAccum = 0;
                    _presentIntervalMaxMs = 0;
                    _latePresentsInWindow = 0;
                    _lateMaxMs = 0;
                }

                // 队头帧不出队即判定（Peek 在 SingleReader 下安全）。
                var head = _frameQueue.Peek();
                if (head is null)
                {
                    // 队列空：若生产者已结束（流尾）则收尾；否则短暂让出，等待生产者补帧。
                    if (_frameQueue.IsCompleted || _decodeDone)
                    {
                        // 流末自然耗尽（非取消）：标记后退出，finally 中触发 Completed 事件。
                        if (EosDiagEnabled)
                            _logger.LogInformation(
                                "[VIDEO-EOS] 自然完成 masterTime={Master:g} 帧队列余量={Cnt} decodeDone={DecodeDone}",
                                _synchronizer.GetCurrentMasterTime(), _frameQueue.Count, _decodeDone);
                        // 只有当轮循环才能标记自然完成：复位后旧循环的判定不得污染新一轮。
                        if (Volatile.Read(ref _runId) == runId)
                            _completedNaturally = true;
                        break;
                    }
                    long emptyWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    Thread.Sleep(1);
                    // 队空等待累积：呈现端因解码供帧不及而等待的墙钟时间——解码侧抖动
                    //（ConvertLatest 阻塞往返/glFinish/gralloc 分配）的直接观测，
                    // 与"呈现段尖峰"（本线程阻塞）形成归因对照。
                    _queueEmptyWaitMsAccum += System.Diagnostics.Stopwatch.GetElapsedTime(emptyWaitStart).TotalMilliseconds;
                    continue;
                }

                // 拷贝 PTS 到局部变量：Wait 期间若发生 定位复位，队头帧可能经 FrameQueue.Clear 归还池
                // 后被复用并改写 Timestamp；使用进入本循环时拷贝的值，避免读到回收后的垃圾时间戳
                // （同时消除旧 Wait 分支在帧所有权已转移后越界读 frame.Timestamp 的隐患）。
                var headTimestamp = head.Timestamp;
                var action = _synchronizer.CheckVideoFrame(head);

                // EOS（流末已到）覆盖：原实现在 eos 下把 Wait/Drop 一律改为 Present，
                // 导致末段缓冲超前的帧（TargetDepth≈6 帧 + DRAIN 尾 GOP）被瞬间整批呈现 →
                // "帧直接推进 / 画面突然变快"（用户痛点：末秒帧爆发式推进后骤停）。
                // 修正策略（仍保末帧绝不丢，但消除爆发）：
                //   · eos && Drop（音频时钟已越界）→ 改 Present（绝不丢末帧）；
                //   · eos && Wait（末段缓冲超前的帧）→ 保持 Wait，按各自 PTS 平滑收口；
                //   · eos && Present → 保持 Present。
                // 尾冻已由 DecodeLoop 的 EOS DRAIN 解决（末段 GOP 已入队），此处不再依赖强制呈现；
                // 正常播放末段帧按各自节奏呈现，爆发消失，末帧仍常驻屏直到音频结束。
                bool eos = _eosReached || _frameQueue.IsCompleted || _decodeDone;
                if (eos && action == SyncAction.Drop)
                    action = SyncAction.Present;

                if (action == SyncAction.Wait)
                {
                    // 帧留队头等待，绝不轮转。下一轮重新 Peek 同一帧再判定。
                    _pipelinePhase = PipelinePhaseWait;
                    if (PacingDiagnostics.Enabled)
                    {
                        PacingDiagnostics.Present.OnWait();
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        WaitUntilDue(headTimestamp, _cts.Token);
                        PacingDiagnostics.Present.OnSleepMeasured(
                            System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                        LogPacingIfDue();
                    }
                    else
                    {
                        WaitUntilDue(headTimestamp, _cts.Token);
                    }
                    // 主时钟停摆降级：WaitUntilDue 已按宽限期（50ms）限速返回，此时若继续重判
                    // 只会空转到时钟恢复（画面冻结）。落入下方 Present 路径按宽限节拍出帧——
                    // 宁可短暂不同步也绝不冻结；主时钟一旦恢复推进，WaitUntilDue 内部复位标志，
                    // 自然回到正常同步等待。
                    if (!_masterClockStalled)
                        continue;
                }

                if (action == SyncAction.Drop)
                {
                    // 队首帧已严重落后：取走并归还（不可留队头，否则永远卡在 Drop 分支）。
                    if (_frameQueue.TryDequeue(out var dropped) && dropped != null)
                    {
                        // 首段丢帧诊断：帧 PTS 与 master 直接对照——PTS 恒 0/停滞而 master 正常推进
                        // 即上游时间戳退化（解码/回落链），非同步逻辑问题。
                        if (_dropDiagCount > 0)
                        {
                            --_dropDiagCount;
                            _logger.LogWarning(
                                "[DROP-DIAG] 丢帧#{Seq}: 帧PTS={Pts:g} master={Master:g} —— 帧被判严重落后；持续同模式=上游时间戳退化或时钟源异常",
                                3 - _dropDiagCount, headTimestamp, _synchronizer.GetCurrentMasterTime());
                        }
                        ReturnFrame(dropped);
                        // 计数只在真的取走并丢弃一帧时自增：队列空/取到 null 时没有帧被丢弃，
                        // 计入会让 VideoDroppedFrames 偏大，违反「文件视频帧数 − 实际呈现帧数」的口径。
                        Interlocked.Increment(ref _droppedFrames);
                        // 节拍诊断与丢帧计数同源：只有真的丢掉一帧才记一次 Drop。
                        if (PacingDiagnostics.Enabled)
                            PacingDiagnostics.Present.OnDrop();
                    }
                    if (PacingDiagnostics.Enabled)
                        LogPacingIfDue();
                    continue;
                }

                // Present：队头帧判定可呈现，正式取走并呈现。
                if (_frameQueue.TryDequeue(out var frame) && frame != null)
                {
                    ProcessFrame(frame);
                }
                else
                {
                    // 队头刚被判为「可呈现」，取帧却失败：该帧已被并发清空（Flush/Stop），不会再被呈现，
                    // 按「文件视频帧数 − 实际呈现帧数」的口径属真丢帧。
                    Interlocked.Increment(ref _droppedFrames);
                    if (PacingDiagnostics.Enabled)
                        PacingDiagnostics.Present.OnDrop();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出（Stop 调用了 cts.Cancel）
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "视频管线异常");
        }
        finally
        {
            // 已复位到新一轮：旧循环直接退场，既不报完成也不改动新一轮的运行状态。
            if (Volatile.Read(ref _runId) == runId)
            {
                // 仅当流末自然耗尽时触发 Completed（取消退出不触发，避免 Stop/Dispose 误报 Ended）。
                if (_completedNaturally)
                    Completed?.Invoke(this, EventArgs.Empty);
                _isRunning = false;
            }
        }
    }
}

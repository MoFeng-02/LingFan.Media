using System;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

// AudioPipeline 的消费循环段（部分类，由 AudioPipeline.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Start/Pause/Stop/Dispose）在主文件；音频主时钟供血循环与供给诊断。
public sealed partial class AudioPipeline
{
    // 诊断（LINGFAN_AUDIO_DIAG=1）：打点音频循环各相位时长，定位 headful 断音成因。
    // 纯诊断：仅在 env 置 1 时开启，生产路径恒为 false，不改变任何控制流/时序。
    private static readonly bool AudioDiagEnabled =
        string.Equals(Environment.GetEnvironmentVariable("LINGFAN_AUDIO_DIAG"), "1", StringComparison.Ordinal);

    // EOS 时序诊断（LINGFAN_EOS_DIAG=1）：记录自然完成瞬间的「主时钟位置」，定位偶发提前结束成因。
    private static readonly bool EosDiagEnabled =
        string.Equals(Environment.GetEnvironmentVariable("LINGFAN_EOS_DIAG"), "1", StringComparison.Ordinal);

    // 喂帧缺口定位（始终在线、稀疏触发）：两次成功提交间隔 > 120ms 且非暂停时输出一条 Warning，
    // 附上一迭代的相位分解（读包等待/解码/提交耗时）——真机定位「喂帧被饿」的卡点阶段：
    // 读包等待大 = 上游包通道饿；解码大 = 音频解码慢；提交大 = 设备背压异常；均小而缺口大 = 线程池续体延迟。
    // 健康节奏提交间隔 ≈ 小量子（40ms），零输出。1s 节流防饥饿期刷屏。
    private double _diagReadWaitMs;     // 最近一次迭代：ReadAsync 等待时长（ms）
    private double _diagDecodeMs;       // 最近一次迭代：DecodeAndEnqueue 累计时长（ms）
    private double _diagSubmitMs;       // 最近一次迭代：TrySubmitChunk 累计时长（ms，含设备背压等待）
    private long _lastGapWarnQpc;       // 缺口 Warning 节流锚点（1s）

    // 供给速率仪表：均匀的吞吐亏损（如线程调度延迟摊在每个提交周期）不产生 >120ms 离散缺口，
    // 唯有按「窗口内提交采样数 / 墙钟」计算实时百分比才能显形。速率 ≈100% 而设备仍欠载 → 消费/
    // 记账侧；速率 <100% → 本循环上游（解码/包供给）不足。窗口 5s，回调式输出（与 OPENSLES-pace 对表）。
    private long _supplyWindowStartQpc;     // 0=未开窗
    private long _supplySubmittedSamples;   // 窗口内提交采样数
    private int _supplyDecodedPackets;      // 窗口内解码包数
    private int _supplyQueueMin = int.MaxValue; // 窗口内采样队列最小水位
    private int _supplyQueueMax;            // 窗口内采样队列最大水位
    private int _supplySampleRate;          // 首帧采样率（窗口速率分母；懒初始化于首次提交）
    private int _diagPacketDepthMin = int.MaxValue; // 窗口内音频包通道最小深度：≈0 → demux 供给不足；
                                                    // 有积压而产出仍不足 → 解码消费慢（二选一定位）

    /// <summary>
    /// 采样一次音频包通道的瞬时深度，并把它并入窗口内的最小值。
    /// </summary>
    /// <remarks>
    /// 采样点必须覆盖所有「会在这一轮里取包或本可取包」的地方：解码阶段取首包之前、前瞻预取每取走
    /// 一个包之后、以及提交阶段的每一轮次。只在解码阶段采一次会让该指标只高不低——前瞻连续取包与
    /// 提交轮次的空档都不计入，读数被抬到「通道有积压」的假象上。
    /// 判读：窗口内最小 ≈0 → 包通道被抽干，瓶颈在上游（demux/网络供给）；明显大于 0 而产出仍不足
    /// → 包在通道里积压，瓶颈在解码或提交侧。
    /// 开销是一次 <c>ChannelReader.Count</c>（O(1)）与一次比较，无锁、无分配、无日志。
    /// </remarks>
    private void SamplePacketDepth()
    {
        int depth = _packetQueue.Reader.Count;
        if (depth < _diagPacketDepthMin)
            _diagPacketDepthMin = depth;
    }

    private async Task PipelineLoop()
    {
        // 本轮循环的运行代号：定位复位后旧循环可能仍在收尾，其收尾动作与完成上报都必须
        // 只对当轮生效，否则会把新一轮的状态改坏。
        int runId = Volatile.Read(ref _runId);
        // 本轮令牌在入口固化：定位复位会换掉 _cts 实例，若每次都读字段，旧循环会改用新令牌
        // 继续存活，取消就不再对它生效。
        var ct = _cts.Token;
        try
        {
            // 音频是主时钟的唯一供血者：喂帧线程被挤 → BufferQueue 排空 → 主时钟停走 →
            // 音频欠载 + 视频按慢钟拖呈（真机实测强切会话时钟比 0.8、在途归零全程）。
            // LongRunning 专用线程上提升 AboveNormal（全平台——主时钟欠载与平台无关）；
            // async 续体在读包真阻塞时偶尔换线程属已知边界，提交热路径（同步段）全程生效。
            try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal; }
            catch { }
            _supplyWindowStartQpc = 0; _supplySubmittedSamples = 0; _supplyDecodedPackets = 0;
            _supplyQueueMin = int.MaxValue; _supplyQueueMax = 0;
            while (!ct.IsCancellationRequested)
            {
                // 已复位到新一轮：旧循环立即退场，不再取包或提交，避免与新一轮争抢单读者队列。
                if (Volatile.Read(ref _runId) != runId)
                    break;

                // 喂帧缺口定位：两次成功提交间隔 > 120ms 且非暂停 → 喂帧饥饿（设备水库被抽干的主因），
                // 输出上一迭代相位分解定位卡点。暂停间隙不计（恢复后刷新锚点）。
                long iterStart = Stopwatch.GetTimestamp();
                if (!_isPaused && _lastSubmitEndTs != 0)
                {
                    double gapMs = Stopwatch.GetElapsedTime(_lastSubmitEndTs, iterStart).TotalMilliseconds;
                    if (gapMs > 120)
                    {
                        long warnNow = Stopwatch.GetTimestamp();
                        if (_lastGapWarnQpc == 0 ||
                            Stopwatch.GetElapsedTime(_lastGapWarnQpc, warnNow).TotalMilliseconds >= 1000)
                        {
                            _lastGapWarnQpc = warnNow;
                            _logger.LogWarning(
                                "[AUDIO-FEED] 提交缺口 {Gap:F0}ms：读包等待={Read:F1}ms 解码={Dec:F1}ms 提交={Sub:F1}ms 采样队列={Sq}",
                                gapMs, _diagReadWaitMs, _diagDecodeMs, _diagSubmitMs, _sampleQueue.Count);
                        }
                    }
                }
                _diagReadWaitMs = 0; _diagDecodeMs = 0; _diagSubmitMs = 0;

                if (_isPaused)
                {
                    _pauseAckTcs?.TrySetResult(true);
                    await Task.Delay(10, ct);
                    _lastSubmitEndTs = Stopwatch.GetTimestamp(); // 暂停间隙不计入喂帧缺口
                    continue;
                }

                // 1. 提交阶段：小量子连续提交（(b)(1) 架构补短）
                //    每次最多提交 ~MaxSubmitChunkMs 的音频，使渲染线程的 submittedSamples 连续推进
                //    （解决诊断仪"假 stall"），并收窄设备前置缓冲到 ~缓冲时长（更贴近实时、抗 decode 抖动）。
                if (_sampleQueue.Count > 0)
                {
                    // 提交轮次同样要采样：采样队列有货而包通道已空，正是上游供给不足的典型形态，
                    // 只在解码阶段采样会让这一类窗口整体缺席。
                    SamplePacketDepth();
                    var subStart = Stopwatch.GetTimestamp();
                    if (!TrySubmitChunk(ct))
                        break;
                    _diagSubmitMs += Stopwatch.GetElapsedTime(subStart).TotalMilliseconds;
                    continue;
                }

                // 2. 解码阶段：队列空，读包解码；并预解码若干包填满前瞻窗口，
                //    使提交阶段能成批提交（把解码与提交解耦，避免逐帧阻塞）。
                // 解码阶段入口：记录自上次提交以来的总间隙
                var phaseStart = Stopwatch.GetTimestamp();

                MediaPacket? packet;
                try
                {
                    // 取包前的通道深度：TryRead 失败即说明通道已空，因此这一采样同时覆盖阻塞读的起点。
                    SamplePacketDepth();
                    if (!_packetQueue.Reader.TryRead(out packet))
                    {
                        var readStart = Stopwatch.GetTimestamp();
                        packet = await _packetQueue.Reader.ReadAsync(ct);
                        _diagReadWaitMs = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                        if (AudioDiagEnabled && _diagReadWaitMs > 80)
                            _logger.LogWarning("[AUDIO-DIAG] ReadAsync 阻塞 {Ms}ms（上游包未及时到达 → 提交中断 → 静音）", _diagReadWaitMs);
                    }
                }
                catch (ChannelClosedException)
                {
                    // 通道更换（定位复位重建队列）不等于流末：旧循环直接退场，交给新一轮循环。
                    if (Volatile.Read(ref _runId) != runId)
                        break;

                    // 流结束：所有采样已提交，标记自然完成并退出，finally 中触发 Completed 事件。
                    if (EosDiagEnabled)
                        _logger.LogInformation("[AUDIO-EOS] 自然完成 masterTime={Master:g}",
                            _synchronizer.GetCurrentMasterTime());
                    _sampleQueue.Complete();
                    _completedNaturally = true;
                    break;
                }

                _supplyDecodedPackets++;
                if (AudioDiagEnabled)
                {
                    var decStart = Stopwatch.GetTimestamp();
                    await DecodeAndEnqueueAsync(packet, ct);
                    _diagDecodeMs += Stopwatch.GetElapsedTime(decStart).TotalMilliseconds;
                    var decMs = _diagDecodeMs;
                    if (decMs > 80)
                        _logger.LogWarning("[AUDIO-DIAG] Decode+Enqueue 阻塞 {Ms}ms（解码慢）", decMs);
                }
                else
                {
                    var decStart = Stopwatch.GetTimestamp();
                    await DecodeAndEnqueueAsync(packet, ct);
                    _diagDecodeMs += Stopwatch.GetElapsedTime(decStart).TotalMilliseconds;
                }

                // 前瞻：采样队列未填满且仍有包立即可用时，连续解码（不 await），把解码与提交解耦。
                // 【关键】前瞻中每解码一帧即穿插一次提交——设备 BufferQueue（8×46.4ms≈372ms 水库）
                // 由本线程独占供血，解码阶段若零提交，水库被 DAC 定速抽干 → 欠载（音频咔哒 +
                // 主时钟停走 → 视频帧卡顿）。穿插提交使在途水位全程 ≥6 缓冲，解码慢/抖不再欠载。
                while (_sampleQueue.Count < PrerollFrames && _packetQueue.Reader.TryRead(out var next))
                {
                    // 每预取走一个包都要计入：这一轮 while 常连续取走多个包，漏采会把最小值抬高。
                    SamplePacketDepth();
                    _supplyDecodedPackets++;
                    var pdStart = Stopwatch.GetTimestamp();
                    await DecodeAndEnqueueAsync(next, ct);
                    _diagDecodeMs += Stopwatch.GetElapsedTime(pdStart).TotalMilliseconds;
                    var pdMs = Stopwatch.GetElapsedTime(pdStart).TotalMilliseconds;
                    if (AudioDiagEnabled && pdMs > 80)
                        _logger.LogWarning("[AUDIO-DIAG] 前瞻 Decode 阻塞 {Ms}ms（解码慢）", pdMs);

                    if (_sampleQueue.Count > 0)
                    {
                        var subStart = Stopwatch.GetTimestamp();
                        if (!TrySubmitChunk(ct))
                            break;
                        _diagSubmitMs += Stopwatch.GetElapsedTime(subStart).TotalMilliseconds;
                    }
                }

                // 解码阶段总间隙诊断（线程池续体延迟：读/解码均快但整段仍慢）
                if (AudioDiagEnabled && _lastSubmitEndTs != 0)
                {
                    var gapMs = Stopwatch.GetElapsedTime(_lastSubmitEndTs).TotalMilliseconds;
                    var totalPhaseMs = Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
                    if (gapMs > 120 && totalPhaseMs < gapMs - 20)
                        _logger.LogWarning("[AUDIO-DIAG] 解码阶段间隙 {Gap}ms 但读/解码均快 → 疑似线程池续体延迟", gapMs);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "音频管线异常");
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

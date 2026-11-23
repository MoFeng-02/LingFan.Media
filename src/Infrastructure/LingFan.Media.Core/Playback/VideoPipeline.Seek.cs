using System;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

/// <summary>
/// VideoPipeline 的定位复位职责分部：ResetForSeekAsync（两阶段复位）与 ResumeAfterSeek（恢复呈现）。
/// 字段与生命周期编排在主文件 VideoPipeline.cs。
/// </summary>
public sealed partial class VideoPipeline
{
    /// <summary>
    /// 定位复位：把管线从「可能已自然结束、可能残留完成标志」的状态带回可继续消费新包的状态。
    /// 定位是唯一需要复位的场景；复位后管线保持暂停，待调用方重新灌入数据后再调用
    /// <see cref="ResumeAfterSeek"/> 恢复呈现。
    /// </summary>
    /// <remarks>
    /// <para>为什么必须清完成标志：解码循环退出时会把 <c>_decodeDone</c> 置位，呈现循环看到它即判定
    /// 「流自然结束」并触发 Completed；若不复位，定位后新包尚未到达，管线就抢先自我收尾。</para>
    /// <para>复位顺序：先暂停并等待确认，再停掉两条消费循环并等待其退出，然后递增运行代号作废
    /// 复位前启动的循环，最后取解码锁清帧队列、复位解码器与有状态处理器并清完成标志。</para>
    /// <para>必须先停循环再复位解码器：旧解码循环可能正在 DecodeAsync，也可能在复位之后把
    /// 「通道关闭」当作流末而写入完成标志。</para>
    /// <para>队列残留帧在复位时归还帧池而非 Dispose，避免多次定位耗尽帧池。</para>
    /// </remarks>
    internal async Task ResetForSeekAsync()
    {
        if (_isRunning)
        {
            _pauseAckTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _isPaused = true;

            // 阶段1: 等待暂停确认（快速路径，50ms 超时），让呈现循环尽快离开呈现分支。
            try
            {
                await _pauseAckTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(50));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("视频管线暂停确认超时（50ms），继续停止消费循环");
            }
        }

        // 阶段2: 停掉两条消费循环。定位期间不允许旧循环继续消费包或呈现帧：
        // 旧解码器可能吃掉定位后的关键帧，旧循环也可能在复位后写入完成标志。
        _isRunning = false;
        _isPaused = true;
        _cts.Cancel();

        // 代号在等待循环退出之前递增：旧循环的收尾若落在等待窗口内，也会因代号不匹配
        // 而不写完成标志、不改存活标志，复位结果不会被上一轮的迟到收尾改写。
        Interlocked.Increment(ref _runId);

        var pipelineTask = _pipelineTask;
        if (pipelineTask != null)
        {
            try
            {
                await pipelineTask.WaitAsync(LoopStopTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("视频呈现循环停止超时（{Timeout}ms），继续复位", LoopStopTimeout.TotalMilliseconds);
            }
        }

        var decodeTask = _decodeTask;
        if (decodeTask != null)
        {
            try
            {
                await decodeTask.WaitAsync(LoopStopTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("视频解码循环停止超时（{Timeout}ms），继续复位", LoopStopTimeout.TotalMilliseconds);
            }
        }

        // 阶段3: 落下存活标志。运行代号在取消后已递增，复位前启动的循环即使仍在收尾也已作废：
        // 它们不再消费包、不再写完成标志、也不再触发 Completed。
        // 同时在途的解码重建也必须作废：它稍后若照常启动解码循环，就会与本轮复位后的重启并行。
        _decodeLoopAlive = false;
        Interlocked.Increment(ref _decodeRestartId);
        _decodeRestarting = false;

        // 阶段4: 清帧队列、复位解码器与有状态处理器。解码锁仍要取：超时未退出的旧解码循环
        // 可能正持锁执行 DecodeAsync。
        if (await _decodeLock.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            try
            {
                _frameQueue.Reset(_framePool);
                _decoder.Reset();
                _processorReset?.Invoke(); // 重置有状态处理器（释放 _held 等）
                _pendingDecoderReset = false;
                _pendingProcessorReset = false;
            }
            finally
            {
                _decodeLock.Release();
            }
        }
        else
        {
            _logger.LogError("视频管线解码锁获取超时（2s），标记延迟 Reset 防止竞态崩溃");
            _frameQueue.Reset(_framePool); // Channel 线程安全，仍然清空
            _pendingDecoderReset = true;   // 锁超时未做 Reset，待管线线程下次进入锁时补做，确保解码器状态必然复位
            _pendingProcessorReset = true;   // 同上：有状态处理器延迟重置，避免与 Process 并发
        }

        // 完成标志统一在复位动作之后清除：先清会被仍在收尾的上一轮循环重新置位。
        _decodeDone = false;
        _eosReached = false;
        _completedNaturally = false;
        _isPaused = true;
        _isRunning = false;
    }

    /// <summary>
    /// 定位后恢复呈现。<see cref="ResetForSeekAsync"/> 已把两条消费循环停掉，因此这里统一拉起
    /// 新一轮循环；新一轮已在运行时重复调用只解除暂停，不会启动第二条。
    /// </summary>
    internal void ResumeAfterSeek()
    {
        // 解码循环单独退出而呈现循环仍在空转时，_isRunning 仍为 true：必须同时看存活标志，
        // 否则只会解除暂停而不重拉解码，帧队列永远不再被填充。
        if (_isRunning && _decodeLoopAlive)
        {
            _isPaused = false;
            return;
        }

        // 保留启动门控状态：定位不是冷启动，画面无需重新等待音频就绪与首帧信号。
        StartCore(resetStartupGates: false);
    }

    /// <summary>
    /// 只补拉已经退出的解码消费循环，呈现循环保持原样继续运行。
    /// </summary>
    /// <remarks>
    /// 不重建呈现循环：帧队列以单读模式创建，第二条呈现线程会破坏该契约。
    /// 上一轮解码循环的收尾可能稍后才执行，因此新一轮启动前先等它结束再清除标志，
    /// 否则新循环会被旧循环的收尾改写成「已结束」。
    /// 该等待有上限：上一轮可能卡在不可取消的原生解码，不能让重建无限期悬停。
    /// </remarks>
    private void RestartDecodeLoopOnly()
    {
        if (_decodeRestarting)
        {
            // 重建链已在跑：这里只把运行态恢复成「未暂停」，不再起第二条重建链。
            // 新解码循环会在有界等待之后自行启动，因此此刻不必也无法提前置存活标志。
            _isPaused = false;
            return;
        }

        _decodeRestarting = true;
        Interlocked.Increment(ref _decodeRestartId);
        _isPaused = false;
        _decodeDone = false;
        _eosReached = false;
        _decodeTask = RestartDecodeLoopAsync(_decodeTask, Volatile.Read(ref _decodeRestartId));
        StartFreezeWatchdog();
    }

    /// <summary>
    /// 等上一轮解码循环收尾（有上限）后启动新一轮解码循环。
    /// </summary>
    /// <param name="previous">上一轮解码任务；可能仍停在不可取消的原生解码里。</param>
    /// <param name="restartId">本次重建的序号，用于收尾时判断自己是否仍是最新一次重建。</param>
    private async Task RestartDecodeLoopAsync(Task? previous, int restartId)
    {
        try
        {
            if (previous is not null && !previous.IsCompleted)
            {
                try
                {
                    // 有界等待：上一轮若卡在原生解码而永不收尾，新一轮也必须照常启动，
                    // 否则「已在重建」会变成永久静默的零解码状态。
                    await previous.WaitAsync(LoopStopTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("上一轮解码循环未在 {Timeout}ms 内退出，继续启动新一轮；其收尾动作由运行代号阻断",
                        LoopStopTimeout.TotalMilliseconds);
                }
                catch (OperationCanceledException)
                {
                    // 旧循环因取消退出属正常路径。
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "上一轮解码循环收尾异常，继续重建");
                }
            }

            // 本次重建可能已被定位复位或后来的一次重建取代：此时不得再启动解码循环，
            // 否则会出现同一轮两条解码线程。
            if (Volatile.Read(ref _decodeRestartId) != restartId)
                return;

            // 旧循环的收尾可能刚把这两个标志写回，启动新一轮前重新清除。
            _decodeDone = false;
            _eosReached = false;
            // 到这里才真正有解码循环在跑：存活标志延迟到启动前后置位，避免重建窗口内自述健康。
            _decodeLoopAlive = true;
            _decodeRestarting = false;
            await Task.Run(DecodeLoop).ConfigureAwait(false);
        }
        finally
        {
            // 只由最新一次重建落标记：旧链走到这里时新一轮可能已在重建中。
            if (Volatile.Read(ref _decodeRestartId) == restartId)
                _decodeRestarting = false;
        }
    }
}

using System;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

/// <summary>
/// AudioPipeline 的定位复位职责分部：ResetForSeekAsync（两阶段复位）与 ResumeAfterSeekAsync（恢复消费）。
/// 字段与生命周期编排在主文件 AudioPipeline.cs。
/// </summary>
public sealed partial class AudioPipeline
{
    /// <summary>
    /// 定位复位：把管线带回「可继续消费新包」的状态，是 seek 的唯一复位入口。
    /// </summary>
    /// <remarks>
    /// <para>顺序：暂停确认（50ms）→ 递增运行代号并终止旧消费循环（<see cref="LoopStopTimeout"/> 有界等待）
    /// → 解码锁内复位（2s）。任一步超时只记日志并继续，不会阻塞调用方。</para>
    /// <para>必须在缓冲重填之前调用：旧解码器若还在消费，会先吃掉定位后的关键帧与首段样本。</para>
    /// <para>返回后保持暂停且 <see cref="IsRunning"/> 为 false，完成标志已清除；旧循环的完成上报与
    /// 运行状态写入都被运行代号阻断。调用方须在缓冲重填完成后调用 <see cref="ResumeAfterSeekAsync"/>
    /// 重新拉起循环。</para>
    /// </remarks>
    internal async Task ResetForSeekAsync()
    {
        // 阶段1: 等待暂停确认（快速路径，50ms 超时）。循环不在运行时没有暂停可确认，直接跳过。
        if (_isRunning)
        {
            _pauseAckTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _isPaused = true;

            try
            {
                await _pauseAckTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(50));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("音频管线暂停确认超时（50ms），等待解码锁确保安全");
            }
        }

        // 阶段2: 作废并终止上一轮消费循环。运行中与已退出两条路径走同一段逻辑：复位本身是幂等的，
        // 而「循环是否在运行」并不是可靠的判据——复位上一次超时返回后，旧循环可能仍在收尾。
        // 先递增运行代号并置「已作废」，旧循环的收尾（完成上报与运行状态写入）从这一刻起就失去资格，
        // 不必依赖「先清完成标志」这种存在并发窗口的时序；随后取消它并有界等待退出。
        Interlocked.Increment(ref _runId);
        _loopSuperseded = true;
        _completedNaturally = false;
        _isPaused = true;
        _isRunning = false;
        _cts.Cancel();

        // 有界等待：旧循环可能阻塞在包通道读取或设备提交上。超时只记录，绝不让定位挂在它身上；
        // 其后的收尾动作已由运行代号阻断。等待本身有上限，因此不需要再判断调用方是否就是循环线程。
        var loopTask = _pipelineTask;
        if (loopTask != null)
        {
            try
            {
                await loopTask.WaitAsync(LoopStopTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("音频管线消费循环未在 {Timeout}ms 内退出，继续复位；其收尾动作由运行代号阻断",
                    LoopStopTimeout.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "音频管线消费循环退出异常，继续复位");
            }
        }

        // 阶段3: 获取解码锁（慢速路径，确保无 DecodeAsync 在执行）。
        // 循环已退出时锁必然空闲，因此不必为「未运行」另开一条免锁分支——复活旧循环或与异常收尾并发
        // 时，免锁 Reset 会让解码器状态与队列在别人手里被改写。
        if (await _decodeLock.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            try
            {
                _sampleQueue.Reset(_framePool);
                _decoder.Reset();
                _effectReset?.Invoke(); // 重置有状态音频效果（清除延迟线/包络/滤波器历史，防 Seek 后瞬态）
                _output.Flush();
            }
            finally
            {
                _decodeLock.Release();
            }
        }
        else
        {
            _logger.LogError("音频管线解码锁获取超时（2s），标记延迟 Reset 防止竞态崩溃");
            _sampleQueue.Reset(_framePool);
            _output.Flush();
            _pendingDecoderReset = true;   // 锁超时未做 Reset，待管线线程下次进入锁时补做，确保解码器状态必然复位
            _pendingEffectReset = true;    // 同上：有状态效果延迟重置，避免与 Process 并发
        }

        // 复位后一律保持暂停且不处于运行态，由 ResumeAfterSeekAsync 在缓冲重填后重新拉起循环。
        _isPaused = true;
        _completedNaturally = false;
        _isRunning = false;
    }

    /// <summary>
    /// 定位后恢复播放。管线仍在运行时只解除暂停；已退出（消费循环因通道更换或异常收尾）时
    /// 重新拉起消费循环。对仍在运行的管线重复调用不会启动第二条循环。
    /// </summary>
    internal async Task ResumeAfterSeekAsync()
    {
        // 判据是「消费循环是否还活着且未被取代」，不是 _isRunning：复位与异常收尾都可能留下
        // 标志为 true 而循环已退出的窗口；复位超时返回后旧循环更可能既未完成又已被作废，
        // 此时只解除暂停会得到一条永不喂帧的管线。
        if (!_loopSuperseded && _pipelineTask is { IsCompleted: false })
        {
            _isPaused = false;
            _output.Resume();
            return;
        }

        // 定位不是冷启动：复用已建立的输出流，走恢复路径而不是重新 BeginStreaming。
        _isResuming = true;
        await StartAsync();
    }
}

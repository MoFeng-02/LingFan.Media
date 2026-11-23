using Microsoft.Extensions.Logging;
using LingFan.Media.Core.Platform;

namespace LingFan.Media.Core;

/// <summary>
/// MediaPlayer 的 Seek 职责段（部分类，由 MediaPlayer.cs 按职责拆分，编译期等价）。
/// 字段与核心编排（ctor/OpenAsync/PlayAsync/PauseAsync/StopAsync/SeekAsync/DisposeAsync/Dispose）在主文件；
/// Session 级对象在 OpenAsync 延迟创建，DisposeAsync 分步释放、每步独立 try-catch。
/// </summary>
public sealed partial class MediaPlayer
{
    /// <summary>
    /// 定位失败后的收敛：先尝试从解封装器当前（未移动的）位置重建喂包通道；重建不了则把状态收敛到
    /// <see cref="MediaState.Stopped"/>。
    /// </summary>
    /// <remarks>
    /// <para>定位流程先停读线程、清空缓冲，再让解封装器定位。定位失败时解封装器位置并未移动，
    /// 因此重建喂包即可从原位置继续，播放不中断，调用方仍会收到定位失败的通知。</para>
    /// <para>不变量：本方法返回后若状态仍是 <see cref="MediaState.Playing"/>，两条管线的包通道必须仍有
    /// 活着的写入者。重建失败或本次调用已取消时喂包无法恢复，此时宁可收敛到 <see cref="MediaState.Stopped"/>
    /// 报停，也不能让状态标签与管线真实状况相反。</para>
    /// </remarks>
    private async Task RecoverAfterFailedSeekAsync(CancellationToken ct)
    {
        try
        {
            if (_bufferManager != null && !ct.IsCancellationRequested)
            {
                // 播放中定位时通道仍可写，PrepareForSeek 幂等；流已结束时经 ResetQueues 重建新通道，
                // 故必须把管线内部的包队列重新指向新实例，否则解码循环会在旧通道上瞬间 EOF。
                _bufferManager.PrepareForSeek();
                await _bufferManager.StartAsync(ct);
                _videoPipeline?.SetPacketQueue(_bufferManager.VideoPacketQueue);
                _audioPipeline?.SetPacketQueue(_bufferManager.AudioPacketQueue);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "定位失败后重建喂包通道失败，改判为停止");
        }

        try
        {
            _pipelineHost?.Stop();
            // 只有播放类状态才需要收敛：Idle/Stopped 下本就没有写入者的义务，
            // 强行转换会撞上状态机的非法转换表并产生无意义告警。
            if (_controller.CurrentState is MediaState.Playing or MediaState.Paused)
                TransitionState(MediaState.Stopped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "定位失败后收敛到停止状态失败");
        }
    }

    /// <summary>
    /// 开启高精度系统定时器（若已开启则幂等），供视频帧精确等待消抖。
    /// </summary>
    private void EnsureHighPrecisionTimer()
    {
        if (!ClockTuning.HighPrecisionTimer || _hpTimerActive)
            return;
        // winmm 返回非 0 = 失败（不抛异常），须按返回值判定，不能把失败当成功置位。
        uint result = WinMm.TimeBeginPeriod(1);
        if (result != 0)
        {
            _logger.LogWarning("开启高精度定时器失败（winmm 返回 {Result}），帧等待将退回默认 15.6ms 分辨率", result);
            return;
        }
        _hpTimerActive = true;
    }

    /// <summary>
    /// 关闭高精度系统定时器（与 <see cref="EnsureHighPrecisionTimer"/> 配对，幂等）。
    /// </summary>
    private void ReleaseHighPrecisionTimer()
    {
        if (!_hpTimerActive)
            return;
        // winmm 返回非 0 = 失败（不抛异常）；无论成败都复位标志，避免后续重复配对。
        uint result = WinMm.TimeEndPeriod(1);
        if (result != 0)
            _logger.LogWarning("关闭高精度定时器失败（winmm 返回 {Result}）", result);
        _hpTimerActive = false;
    }
}

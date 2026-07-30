namespace LingFan.Media.Core;

/// <summary>
/// 音视频同步协调器。协调 <see cref="IMediaClock"/> 与 VideoPipeline / AudioPipeline，
/// 决定视频帧是否呈现/等待/丢弃。
/// </summary>
/// <remarks>
/// <para>不持有管线引用——Synchronizer 只做决策，管线轮询 <see cref="CheckVideoFrame"/> 获取决策。</para>
/// <para>线程安全：<see cref="CheckVideoFrame"/> 从视频管线线程调用，</para>
/// <para><see cref="OnAudioFrameSubmitted"/> 从音频线程调用，两者通过 Clock 的锁间接同步。</para>
/// </remarks>
public sealed class Synchronizer
{
    private readonly IMediaClock _clock;
    private readonly TimeSpan _audioLatency;
    private bool _realTimeSync = true;

    /// <summary>
    /// 初始化 <see cref="Synchronizer"/> 的新实例。
    /// </summary>
    /// <param name="clock">媒体时钟。</param>
    /// <param name="audioLatency">音频输出延迟（来自 IAudioOutput.Latency 或播放配置，默认 Zero）。</param>
    public Synchronizer(IMediaClock clock, TimeSpan audioLatency = default)
    {
        _clock = clock;
        _audioLatency = audioLatency;
    }

    /// <summary>
    /// 是否启用音视频实时同步（默认 true）。设 false（最快模式）时
    /// <see cref="CheckVideoFrame"/> 直接放行所有视频帧，不做 Wait / Drop 决策。
    /// </summary>
    public bool RealTimeSync
    {
        get => _realTimeSync;
        set => _realTimeSync = value;
    }

    /// <summary>
    /// 音频帧提交通知，更新主时钟。
    /// </summary>
    /// <param name="frame">已提交的音频帧。</param>
    /// <remarks>
    /// 音频是主时钟来源。等价于以音频输出播放位置为基准：
    /// clock.Position = audioOutput.PlaybackPosition - audioOutput.Latency。
    /// </remarks>
    public void OnAudioFrameSubmitted(AudioFrame frame)
    {
        // 以音频帧的结束时间戳减去输出延迟，校准到"实际听到"的时间点
        _clock.SyncTo(frame.Timestamp + frame.Duration - _audioLatency);
    }

    /// <summary>
    /// 检查视频帧是否该呈现。
    /// </summary>
    /// <param name="frame">待检查的视频帧。</param>
    /// <returns>同步动作决策。</returns>
    public SyncAction CheckVideoFrame(VideoFrame frame)
    {
        // 最快模式：关掉音视频同步，所有视频帧直接放行（不判 Wait/Drop），
        // 适用于无头批量处理（转码 / 离线 ML），避免帧被实时时钟卡住。
        if (!_realTimeSync)
            return SyncAction.Present;

        var videoTime = frame.Timestamp;
        var clockTime = _clock.Position;
        var delta = videoTime - clockTime;

        if (delta > _clock.SyncThreshold)
        {
            // 视频超前时钟 → 等待
            return SyncAction.Wait;
        }

        if (delta < -_clock.DropThreshold)
        {
            // 视频严重落后 → 丢帧
            return SyncAction.Drop;
        }

        // 在阈值内 → 立即呈现
        return SyncAction.Present;
    }

    /// <summary>
    /// Seek 协调。跳转时钟并标记需要 flush 管线。
    /// </summary>
    /// <param name="position">目标位置。</param>
    /// <remarks>
    /// Synchronizer 只跳转时钟，管线 flush 由调用方（MediaPlayer.SeekAsync）负责。
    /// </remarks>
    public void OnSeek(TimeSpan position)
    {
        _clock.SeekTo(position);
    }
}

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

    // 🔴 音画同步根治（2026-08-04）：视频帧「提前多少调用 Present」= 渲染后端真实「Present→上屏」延迟，
    // 由 VideoPipeline 按 IVideoRenderer.PresentationLatency 注入（D3D11=刷新周期，无头=0）。
    // 不再用时钟的 SyncThreshold(50ms) 作呈现偏移——那只是同步门限，错当提前量会让视频系统性提前 ~25ms。
    private TimeSpan _presentationLatency = TimeSpan.FromMilliseconds(1000.0 / 60.0);

    /// <summary>
    /// 主时钟位置源（可选）。若设置，则 <see cref="CheckVideoFrame"/> 直接以该源返回的位置为准，
    /// 不再读取 <see cref="_clock"/>，且 <see cref="OnAudioFrameSubmitted"/> 不再硬跳时钟
    /// （避免批内逐帧 SyncTo 的突发锯齿 —— 见 <see cref="ClockTuning"/> 说明）。
    /// <para>典型用法：塞入 <c>() => audioOutput.GetPlaybackPositionDirect()</c>，
    /// 即设备真实播放游标，平滑且随缓冲耗尽自然停摆。</para>
    /// </summary>
    private Func<TimeSpan>? _masterClockProvider;

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
    /// 视频帧「提前多少调用 Present」的偏移量 = 渲染后端真实「Present→上屏」延迟
    /// （详见 <see cref="IVideoRenderer.PresentationLatency"/>）。默认一个刷新周期(60Hz)，由视频管线注入真实值。
    /// <para><b>这是音画对齐的真正依据</b>：帧在 audioClock 到达 PTS 前「本延迟」时刻调用 Present，
    /// 像素恰在 PTS 时可见。与 <see cref="MediaClock.SyncThreshold"/>（同步门限）无关。</para>
    /// </summary>
    public TimeSpan PresentationLatency
    {
        get => _presentationLatency;
        set => _presentationLatency = value >= TimeSpan.Zero ? value : TimeSpan.Zero;
    }

    /// <summary>
    /// 设置主时钟位置源（真实播放位置驱动）。传入 <c>null</c> 恢复为 <see cref="_clock"/> 驱动。
    /// </summary>
    /// <param name="provider">返回当前主时钟位置（通常为音频设备真实播放游标）。</param>
    public void SetMasterClockProvider(Func<TimeSpan>? provider)
    {
        _masterClockProvider = provider;
        // 诊断提示：音频真实播放游标接管主时钟后，OnAudioFrameSubmitted 会旁路批内 SyncTo，
        // 故 ClockJumpRecorder 不再收到样本（_count==0）。标记此模式，使 [CLOCK] 快照如实打印
        // "音频时钟已驱动"，而非误导性的"音频未驱动时钟"。
        if (provider != null)
            PacingDiagnostics.Clock.MarkAudioDriven();
    }

    /// <summary>
    /// 音频帧提交通知，更新主时钟。
    /// </summary>
    /// <param name="frame">已提交的音频帧。</param>
    /// <remarks>
    /// 音频是主时钟来源。等价于以音频输出播放位置为基准：
    /// clock.Position = audioOutput.PlaybackPosition - audioOutput.Latency。
    /// <para>若已切到 <see cref="_masterClockProvider"/> 驱动（真实播放位置），本方法直接返回 ——
    /// 不再由"已提交末端时间"在批内逐帧硬跳时钟，从根本上消除锯齿波。</para>
    /// </remarks>
    public void OnAudioFrameSubmitted(AudioFrame frame)
    {
        // 已切到真实播放位置主时钟：批提交内的逐帧硬跳是锯齿根因，直接跳过。
        if (_masterClockProvider != null)
            return;

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
        var clockTime = _masterClockProvider != null
            ? _masterClockProvider() - _audioLatency   // 真实播放游标（latency 实际为 0，此处仅保持设计对称）
            : _clock.Position;
        var delta = videoTime - clockTime;

        if (delta > _presentationLatency)
        {
            // 视频超前时钟超过「真实上屏延迟」→ 等待（帧留队头，待时钟追近到 PTS−上屏延迟再呈现）
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
    /// 当前主时钟时间（与 <see cref="CheckVideoFrame"/> 内部计算**完全一致**）。
    /// 供视频管线做帧精确等待时复用同一时间源，避免判据分裂。
    /// </summary>
    internal TimeSpan GetCurrentMasterTime()
    {
        return _masterClockProvider != null
            ? _masterClockProvider() - _audioLatency
            : _clock.Position;
    }

    /// <summary>
    /// 同步阈值：视频超前主时钟超过此值才判 <see cref="SyncAction.Wait"/>。
    /// </summary>
    internal TimeSpan SyncThreshold => _clock.SyncThreshold;

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

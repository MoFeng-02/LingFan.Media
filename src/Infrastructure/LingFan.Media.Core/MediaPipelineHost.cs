using System;

namespace LingFan.Media.Core;

/// <summary>
/// 管线生命周期管理器。VideoPipeline / AudioPipeline 的薄封装，
/// 协调启动/停止/暂停/刷新。
/// </summary>
/// <remarks>
/// <para>Attach/Pause/Stop/Detach 为同步 void（纯内存操作）；
/// StartAsync/FlushAsync/ResumeAfterSeekAsync 为真异步 Task（含启动编排与复位等待）。</para>
/// <para>StartAsync() 同时处理首次启动和恢复暂停（各管线内部区分首启与恢复分支）。</para>
/// <para>Stop() 只调 cts.Cancel，是 O(1) 非阻塞的。线程 join（5s 超时）在 MediaPlayer.DisposeAsync 中处理。</para>
/// <para>管线创建不在 MediaPipelineHost 中——管线由 MediaPlayer 创建（需要 Decoder/Renderer 等 Session 级对象）。</para>
/// <para>MediaPipelineHost 只负责生命周期管理。</para>
/// </remarks>
public sealed class MediaPipelineHost
{
    private VideoPipeline? _videoPipeline;
    private AudioPipeline? _audioPipeline;
    private SubtitleProcessor? _subtitleProcessor;

    /// <summary>播放自然完成事件：所有存在的 A/V 管线均耗尽流末后触发。MediaPlayer 据此转 <see cref="MediaState.Ended"/>。</summary>
    public event EventHandler? PlaybackCompleted;

    /// <summary>累计视频丢帧数（诊断/可观测性，只读转发到视频管线）。</summary>
    public long VideoDroppedFrames => _videoPipeline?.DroppedFrames ?? 0;

    // 完成门控：仅聚合 video/audio 两条管线（字幕是显示层、由时钟驱动、不参与完成判定）。
    // 任一管线为 null（纯音频/纯视频媒体）即视为该侧已完成；两侧均完成且未触发过 → 发 PlaybackCompleted。
    private readonly object _completionLock = new();
    private bool _videoCompleted;
    private bool _audioCompleted;
    private bool _completionRaised;

    // 时长收尾门控（仅实时模式的有限时长媒体启用）：主时钟到达时长后，给解码侧一个限期收尾窗口，
    // 到期强制视频管线按自然完成收尾——避免超实时解码能力的素材把 Ended 拖延数倍时长。
    private TimeSpan _mediaDuration;
    private bool _durationFinaleEnabled;
    private Func<TimeSpan>? _masterClockReader;
    private Timer? _durationFinaleTimer;
    private long _finaleArmQpc;
    private bool _finaleForced;

    /// <summary>时长收尾宽限：主时钟越过时长后等待解码自然收尾的最长时间，到期强制收尾。</summary>
    private static readonly TimeSpan DurationFinaleGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 绑定管线（由 MediaPlayer 在创建管线后调用）。
    /// </summary>
    /// <param name="video">视频管线（可为 null，纯音频媒体）。</param>
    /// <param name="audio">音频管线（可为 null，纯视频媒体）。</param>
    /// <param name="subtitle">字幕处理器（可为 null）。</param>
    public void Attach(VideoPipeline? video, AudioPipeline? audio, SubtitleProcessor? subtitle = null)
    {
        _videoPipeline = video;
        _audioPipeline = audio;
        _subtitleProcessor = subtitle;

        if (video != null) video.Completed += OnPipelineCompleted;
        if (audio != null) audio.Completed += OnPipelineCompleted;
        // 字幕无 Completed 事件且不参与完成门控，故不订阅。
    }

    /// <summary>
    /// 配置时长收尾门控（由 MediaPlayer 在 OpenAsync 装配后调用）。
    /// 实时模式下，若媒体有确定时长（非直播），主时钟到达时长后启动限期收尾窗口：
    /// 窗口内解码自然收尾则走正常完成聚合；到期仍未收尾（典型为超实时解码能力的软解素材，
    /// 解码吞吐远低于播放速率）则强制视频管线按自然完成收尾，使播放按时结束而非拖延数倍时长。
    /// </summary>
    /// <param name="duration">媒体时长；<see cref="TimeSpan.Zero"/> 或负值表示未知/直播，禁用门控。</param>
    /// <param name="masterClockReader">主时钟位置读取委托（与同步器同源）；null 时禁用门控。</param>
    /// <param name="enabled">是否启用（仅实时处理模式应为 true；最快模式不判丢帧、无收尾拖延问题）。</param>
    public void ConfigureDurationFinale(TimeSpan duration, Func<TimeSpan>? masterClockReader, bool enabled)
    {
        lock (_completionLock)
        {
            _mediaDuration = duration;
            _masterClockReader = masterClockReader;
            _durationFinaleEnabled = enabled && duration > TimeSpan.Zero && masterClockReader != null;
        }
    }

    /// <summary>
    /// 启动两条管线（异步编排：视频先起 → 预滚动 → 放行首帧门控并等首帧上屏 → 再启动音频，见方法体注释）。
    /// 同时重置完成门控标志，使重播（Ended → Playing）能再次正确触发 PlaybackCompleted。
    /// </summary>
    public async Task StartAsync()
    {
        lock (_completionLock)
        {
            _videoCompleted = false;
            _audioCompleted = false;
            _completionRaised = false;
            _finaleForced = false;
            _finaleArmQpc = 0;
        }
        // 时长收尾巡检（见 EnsureDurationFinaleTimer）只处理「音频已完成、视频解码追赶不及」。
        // 无音频轨媒体没有音频完成信号，由视频管线在呈现追平时长时自行收尾（见 PresentLoop）。
        if (_videoPipeline == null)
            return;
        EnsureDurationFinaleTimer();

        // 启动编排（A/V 启动顺序）：
        //   (1) 视频管线先 Start —— 呈现循环被「首帧门控」挡住，**一帧都不上屏**，
        //      仅让解码生产者提前把帧队列暖起来；
        //   (2) await 视频预滚动（≥2 帧或超时）——重播时此等待吸收 demuxer 重定位 + 解码器 Reset 的产出延迟；
        //   (3) **先放行视频门控**（此刻音频设备即将/刚刚启动，主时钟处于「起转瞬态」≈0）；
        //   (4) 再 await 音频管线启动 —— 主时钟随音频设备从 0 起跑。
        //
        // 关键时序：WASAPI 主时钟在 audio.StartAsync 的 preroll +
        // 校准锁定期间会从 0 爬到一定值（瞬态期返回 ≈0，之后锁定引擎领先偏移
        // 才跳到可闻位置）。若把 SignalAudioReady 放在 (4) 之后（preroll 跑完、校准已锁定），
        // 门控放行时主时钟已明显大于 0 → 开头一小段 PTS 靠前的帧被 DropThreshold 全判掉，
        // 首帧落到较晚位置。
        // 修正：把放行提前到 (3) —— 让首帧 PTS=0 在「主时钟≈0 的瞬态期」就同刻呈现，完全复刻首播
        // （首帧 delta≈0）的无缝行为；重播衔接处从「跳到较晚位置」变为「从 0.0 续上」。
        // GetPlaybackPositionDirect 在设备未开/瞬态期返回 0（不抛），故提前放行安全。
        _videoPipeline?.Start();
        if (_videoPipeline != null)
            await _videoPipeline.WaitForPrerollAsync();

        // (3) 在主时钟≈0（音频设备即将启动的瞬态期）先行放开视频门控。
        _videoPipeline?.SignalAudioReady();

        // (3).5 等视频首帧真正上屏后再启动音频：视频首帧经 D3D11 上传 + vsync 上屏
        // 比音频 WASAPI preroll 出声慢；若不在 (4) 前等待，音频会早于视频首帧出声（用户感知「声音比视频先出」）。
        // 等待期间视频首帧(PTS≈0)在「主时钟≈0 瞬态期」已先行呈现，(4) 启动音频时主时钟仍≈0 → 音画同源对齐。
        // 带超时兜底(1.5s)，绝不阻塞播放（超时则照常启动音频）。
        if (_videoPipeline != null)
            await _videoPipeline.WaitForFirstFramePresentedAsync(TimeSpan.FromMilliseconds(1500));

        // (4) 启动音频设备；主时钟随其从 0 起跑。音频失败也保留已放行的视频门控（画面仍能播）。
        if (_audioPipeline != null)
            await _audioPipeline.StartAsync();

        _subtitleProcessor?.Start();
    }

    /// <summary>
    /// 聚合各 A/V 管线的自然完成信号。锁内判定"所有存在的管线均已完成"后，
    /// 单次触发 <see cref="PlaybackCompleted"/>（_completionRaised 去重，防止重复触发）。
    /// </summary>
    private void OnPipelineCompleted(object? sender, EventArgs e)
    {
        lock (_completionLock)
        {
            if (ReferenceEquals(sender, _videoPipeline))
                _videoCompleted = true;
            else if (ReferenceEquals(sender, _audioPipeline))
                _audioCompleted = true;
            else
                return; // 未知发送方（不应发生），忽略以防误判

            bool allDone =
                (_videoPipeline == null || _videoCompleted) &&
                (_audioPipeline == null || _audioCompleted);

            if (allDone && !_completionRaised)
            {
                _completionRaised = true;
                PlaybackCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// 确保时长收尾巡检定时器在运行（幂等；未启用门控时不创建）。
    /// 巡检周期 250ms：主时钟越过时长即记录武装时刻，宽限期内解码自然收尾则无事发生；
    /// 宽限期到期仍未收尾则强制视频管线收尾。纯内存判定 + 线程池回调，不占用管线线程。
    /// </summary>
    private void EnsureDurationFinaleTimer()
    {
        lock (_completionLock)
        {
            if (!_durationFinaleEnabled || _durationFinaleTimer != null)
                return;
            _durationFinaleTimer = new Timer(OnDurationFinaleTick, null,
                TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        }
    }

    /// <summary>
    /// 时长收尾巡检（线程池线程）。主时钟到达时长后启动宽限窗口；到期强制视频收尾。
    /// 时钟回退（用户 seek 回跳）时解除武装，等待下一次自然到达时长。
    /// </summary>
    private void OnDurationFinaleTick(object? state)
    {
        VideoPipeline? videoToFinish = null;
        lock (_completionLock)
        {
            if (!_durationFinaleEnabled || _completionRaised || _finaleForced)
                return;
            // 音频侧尚未自然收尾时不武装：时长收尾只解决「音频已完、视频解码仍在追赶」的拖延。
            if (_audioPipeline != null && !_audioCompleted)
                return;

            TimeSpan master;
            try { master = _masterClockReader!(); }
            catch { return; }

            if (master < _mediaDuration)
            {
                // 时钟未达时长（含 seek 回跳）：解除武装。
                _finaleArmQpc = 0;
                return;
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_finaleArmQpc == 0)
            {
                _finaleArmQpc = now;
                return;
            }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_finaleArmQpc) < DurationFinaleGrace)
                return;

            // 宽限期到期：视频仍未自然收尾（超实时能力软解的典型场景），强制按自然完成收尾。
            _finaleForced = true;
            videoToFinish = _videoPipeline;
        }

        // 锁外调用：FinishAtDuration 内部会停解码循环并触发 Completed → OnPipelineCompleted 重入本锁。
        videoToFinish?.FinishAtDuration();
    }

    /// <summary>
    /// 暂停两条管线（纯内存：设标志位）。
    /// </summary>
    public void Pause()
    {
        _videoPipeline?.Pause();
        _audioPipeline?.Pause();
        _subtitleProcessor?.Pause();
    }

    /// <summary>
    /// 停止两条管线（只调 cts.Cancel，不等待线程退出）。
    /// </summary>
    public void Stop()
    {
        lock (_completionLock)
        {
            _finaleArmQpc = 0;
            _durationFinaleTimer?.Dispose();
            _durationFinaleTimer = null;
        }
        _videoPipeline?.Stop();
        _audioPipeline?.Stop();
        _subtitleProcessor?.Stop();
    }

    /// <summary>
    /// 定位复位：把两条管线与字幕处理器带回「可继续消费新包」的状态，是 seek 的唯一复位入口。
    /// </summary>
    /// <remarks>
    /// <para>先清完成门控再复位管线：否则上一次播放残留的完成态会在定位后立刻判定播放结束。</para>
    /// <para>各管线复位内部两阶段保证——暂停确认（快速路径，TaskCompletionSource）+ 解码锁（慢速路径，确保 Reset 不与 DecodeAsync 并发）。</para>
    /// <para>最坏情况总耗时：50ms+50ms+150ms（暂停确认）+ 最多 2s+2s+2s（解码锁），
    /// 正常情况约 50ms+50ms+150ms（管线空闲，锁立即获取）。</para>
    /// <para>复位后管线保持暂停，须在包缓冲重填完成后再调用 <see cref="ResumeAfterSeekAsync"/>。</para>
    /// </remarks>
    public async Task FlushAsync()
    {
        lock (_completionLock)
        {
            _videoCompleted = false;
            _audioCompleted = false;
            _completionRaised = false;
            _finaleForced = false;
            _finaleArmQpc = 0;
        }

        if (_videoPipeline != null)
            await _videoPipeline.ResetForSeekAsync();
        if (_audioPipeline != null)
            await _audioPipeline.ResetForSeekAsync();
        if (_subtitleProcessor != null)
            await _subtitleProcessor.ClearAsync();
    }

    /// <summary>
    /// 定位后恢复播放。对仍在运行的管线只解除暂停，不重复启动消费循环；
    /// 管线已在定位复位前退出时，由各管线自行拉起新的消费循环。
    /// </summary>
    public async Task ResumeAfterSeekAsync()
    {
        _videoPipeline?.ResumeAfterSeek();
        if (_audioPipeline != null)
            await _audioPipeline.ResumeAfterSeekAsync();
    }

    /// <summary>
    /// 分离管线（释放引用）。
    /// </summary>
    public void Detach()
    {
        lock (_completionLock)
        {
            _durationFinaleTimer?.Dispose();
            _durationFinaleTimer = null;
            _durationFinaleEnabled = false;
        }
        _videoPipeline = null;
        _audioPipeline = null;
        _subtitleProcessor = null;
    }
}

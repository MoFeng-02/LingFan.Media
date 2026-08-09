using System;

namespace LingFan.Media.Core;

/// <summary>
/// 管线生命周期管理器。VideoPipeline / AudioPipeline 的薄封装，
/// 协调启动/停止/暂停/刷新。
/// </summary>
/// <remarks>
/// <para>所有方法均为同步 void（无 Task 返回，无 Resume）。</para>
/// <para>Start() 同时处理首次启动和恢复暂停。</para>
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
    /// 启动两条管线（纯内存：设标志位 + fire-and-forget）。
    /// 同时重置完成门控标志，使重播（Ended → Playing）能再次正确触发 PlaybackCompleted。
    /// </summary>
    public async Task StartAsync()
    {
        lock (_completionLock)
        {
            _videoCompleted = false;
            _audioCompleted = false;
            _completionRaised = false;
        }

        // 🔴 启动编排（2026-08-06 §33，顺序已据复验 1.txt/2.txt 修正）：
        //   ① 视频管线先 Start —— 呈现循环被「首帧门控」挡住，**一帧都不上屏**，
        //      仅让解码生产者提前把帧队列暖起来；
        //   ② await 视频预滚动（≥2 帧或超时）——重播时此等待吸收 demuxer 重定位 + 解码器 Reset 的产出延迟；
        //   ③ **先放行视频门控**（此刻音频设备即将/刚刚启动，主时钟处于「起转瞬态」≈0）；
        //   ④ 再 await 音频管线启动 —— 主时钟随音频设备从 0 起跑。
        //
        // 关键时序（2026-08-06 复验暴露的真因）：WASAPI 主时钟在 audio.StartAsync 的 ~600ms preroll +
        // 校准锁定期间会从 0 爬到 ~0.5s（wallElapsed<0.3s 的瞬态期返回 ≈0，之后锁定引擎领先偏移
        // ~0.5s 才跳到可闻位置）。§33 初版把 SignalAudioReady 放在 ④ 之后（preroll 跑完、校准已锁定），
        // 门控放行时主时钟已 ≈0.5s → 0.0~0.33s 的帧被 DropThreshold(200ms) 全判掉，首帧落到 0.33/0.36
        // （实测软解 0.366@-184ms、硬解 0.333@-200ms，且首播也被拖成 0.3@-197ms，与「首播正常」相悖）。
        // 修正：把放行提前到 ③ —— 让首帧 PTS=0 在「主时钟≈0 的瞬态期」就同刻呈现，完全复刻首播
        // （首帧 delta≈0）的无缝行为；重播衔接处从「跳到 0.33/0.36」变为「从 0.0 续上」。
        // GetPlaybackPositionDirect 在设备未开/瞬态期返回 0（不抛），故提前放行安全。
        _videoPipeline?.Start();
        if (_videoPipeline != null)
            await _videoPipeline.WaitForPrerollAsync();

        // ③ 在主时钟≈0（音频设备即将启动的瞬态期）先行放开视频门控。
        _videoPipeline?.SignalAudioReady();

        // ③.5 🔴 等视频首帧真正上屏后再启动音频（§33 补强）：视频首帧经 D3D11 上传 + vsync 上屏
        // 比音频 WASAPI preroll 出声慢；若不在 ④ 前等待，音频会早于视频首帧出声（用户感知「声音比视频先出」）。
        // 等待期间视频首帧(PTS≈0)在「主时钟≈0 瞬态期」已先行呈现，④ 启动音频时主时钟仍≈0 → 音画同源对齐。
        // 带超时兜底(1.5s)，绝不阻塞播放（超时则照常启动音频）。
        if (_videoPipeline != null)
            await _videoPipeline.WaitForFirstFramePresentedAsync(TimeSpan.FromMilliseconds(1500));

        // ④ 启动音频设备；主时钟随其从 0 起跑。音频失败也保留已放行的视频门控（画面仍能播）。
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
        _videoPipeline?.Stop();
        _audioPipeline?.Stop();
        _subtitleProcessor?.Stop();
    }

    /// <summary>
    /// Seek 后刷新两条管线和字幕处理器。同步版本，用于无法 await 的场景。
    /// 各管线 Flush 内部两阶段保证——
    /// 暂停确认（快速路径）+ 解码锁（慢速路径，确保 Reset 不与 DecodeAsync 并发）。
    /// </summary>
    public void Flush()
    {
        _videoPipeline?.Flush();
        _audioPipeline?.Flush();
        _subtitleProcessor?.Clear();
    }

    /// <summary>
    /// Seek 后刷新两条管线和字幕处理器。异步版本，优先使用。
    /// 各管线 FlushAsync 内部两阶段保证——
    /// 暂停确认（快速路径，TaskCompletionSource）+ 解码锁（慢速路径，确保 Reset 不与 DecodeAsync 并发）。
    /// </summary>
    /// <remarks>
    /// <para>顺序 await 各管线（与同步版一致）。</para>
    /// <para>最坏情况总耗时：50ms+50ms+150ms（暂停确认）+ 最多 2s+2s+2s（解码锁），
    /// 正常情况约 50ms+50ms+150ms（管线空闲，锁立即获取）。</para>
    /// </remarks>
    public async Task FlushAsync()
    {
        if (_videoPipeline != null)
            await _videoPipeline.FlushAsync();
        if (_audioPipeline != null)
            await _audioPipeline.FlushAsync();
        if (_subtitleProcessor != null)
            await _subtitleProcessor.ClearAsync();
    }

    /// <summary>
    /// 分离管线（释放引用）。
    /// </summary>
    public void Detach()
    {
        _videoPipeline = null;
        _audioPipeline = null;
        _subtitleProcessor = null;
    }
}

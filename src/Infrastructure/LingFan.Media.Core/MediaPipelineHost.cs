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
    }

    /// <summary>
    /// 启动两条管线（纯内存：设标志位 + fire-and-forget）。
    /// </summary>
    public void Start()
    {
        _videoPipeline?.Start();
        _audioPipeline?.Start();
        _subtitleProcessor?.Start();
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
    /// V2 修复（L2）：各管线 Flush 内部两阶段保证——
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
    /// V2 修复（L2）：各管线 FlushAsync 内部两阶段保证——
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

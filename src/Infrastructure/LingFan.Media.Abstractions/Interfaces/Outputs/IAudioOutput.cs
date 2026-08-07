using System.Threading;
using System.Threading.Tasks;

namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频输出接口。
/// </summary>
/// <remarks>
/// <para>线程模型：Submit 在音频线程调用。</para>
/// <para>Submit 所有权语义（V2 变更）：Submit 不接管帧所有权，不 Dispose 帧。</para>
/// <para>实现仅同步拷贝 PCM 数据到输出缓冲；调用方（AudioPipeline）负责 Submit 后将帧 Return 到 FramePool 或 Dispose。</para>
/// <para>IFrameResource 非线程安全，需在单线程内使用。</para>
/// </remarks>
public interface IAudioOutput : IMediaComponent
{
    /// <summary>参数化配置：设置采样率和声道数。</summary>
    void Initialize(int sampleRate, int channels);

    /// <summary>
    /// 提交音频帧。不接管帧所有权（V2）；仅同步拷贝 PCM 数据，
    /// 调用方负责 Submit 后释放帧（Return 到池或 Dispose）。
    /// 音频线程调用，缓冲满时阻塞（COM 背压，同步阻塞背压是正常机制，非伪异步）。
    /// </summary>
    void Submit(AudioFrame frame);

    /// <summary>暂停播放。</summary>
    void Pause();

    /// <summary>恢复播放。</summary>
    void Resume();

    /// <summary>
    /// 开始流式输出，但在缓冲区预填足够真实 PCM 后再启动设备时钟（preroll 语义），
    /// 返回时音频已开始流动。默认实现直接恢复播放（无预填需求的后端，如无声/无设备输出）；
    /// WASAPI 重写为根治起播静默窗（引擎抓取的是真实数据而非静音）。
    /// </summary>
    ValueTask BeginStreamingAsync(CancellationToken ct)
    {
        Resume();
        return ValueTask.CompletedTask;
    }

    /// <summary>清空输出缓冲。</summary>
    void Flush();

    /// <summary>获取已播放位置（用于时钟同步）。</summary>
    TimeSpan GetPlaybackPosition();

    /// <summary>
    /// 高频、线程安全的播放位置读取（可选优化）。默认实现回落到 <see cref="GetPlaybackPosition"/>。
    /// <para><b>用途</b>：视频主时钟需要以视频帧率（~30Hz）轮询播放位置。某些后端
    /// （WASAPI）的 <see cref="GetPlaybackPosition"/> 走跨线程封送，高频调用有成本；
    /// 这类后端应重写本方法，直接读取设备时钟（<c>IAudioClock::GetPosition</c> 可由任意线程调用，无需跨线程），
    /// 既平滑又零封送开销。</para>
    /// </summary>
    TimeSpan GetPlaybackPositionDirect() => GetPlaybackPosition();

    /// <summary>
    /// 重播（Ended→Playing）主时钟归零：音频尚未为本遍播放 Start 前，<see cref="GetPlaybackPositionDirect"/>
    /// 应返回 0，避免沿用上一遍播放的时钟锚点导致重播首帧被错判为过期而丢弃。
    /// 默认空实现（无主时钟后端无需处理）；WASAPI 重写为真正的去武装逻辑。
    /// </summary>
    void ResetPlaybackClock() { }

    /// <summary>当前输出延迟。</summary>
    TimeSpan Latency { get; }

    /// <summary>输出音量 (0.0~1.0)。</summary>
    float Volume { get; set; }
}

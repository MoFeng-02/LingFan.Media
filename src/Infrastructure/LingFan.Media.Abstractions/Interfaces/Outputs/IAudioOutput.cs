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

    /// <summary>当前输出延迟。</summary>
    TimeSpan Latency { get; }

    /// <summary>输出音量 (0.0~1.0)。</summary>
    float Volume { get; set; }
}

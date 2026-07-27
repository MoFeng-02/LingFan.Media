namespace LingFan.Media.Abstractions;

/// <summary>
/// 最高层媒体播放器接口。
/// </summary>
/// <remarks>
/// <para>线程安全：公共方法线程安全，可在任意线程调用。</para>
/// <para>CancellationToken 传播策略：</para>
/// <list type="bullet">
/// <item>需要 CT 的方法：OpenAsync / StopAsync / SeekAsync</item>
/// <item>不需要 CT 的方法：PlayAsync / PauseAsync（纯内存状态转换）</item>
/// </list>
/// </remarks>
public interface IMediaPlayer : IDisposable, IAsyncDisposable
{
    /// <summary>打开媒体源。支持取消。</summary>
    Task OpenAsync(IMediaSource source, CancellationToken ct = default);

    /// <summary>开始播放（纯内存状态转换，无 CT）。</summary>
    Task PlayAsync();

    /// <summary>暂停播放（纯内存状态转换，无 CT）。</summary>
    Task PauseAsync();

    /// <summary>停止播放。支持取消（接口契约层，实现可返回 Task.CompletedTask）。</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>定位到指定位置。支持取消。</summary>
    Task SeekAsync(TimeSpan position, CancellationToken ct = default);

    /// <summary>当前播放状态。</summary>
    MediaState State { get; }

    /// <summary>当前播放位置（从 Clock 读取）。</summary>
    TimeSpan Position { get; }

    /// <summary>媒体总时长。</summary>
    TimeSpan Duration { get; }

    /// <summary>音量 (0.0~1.0)。</summary>
    float Volume { get; set; }

    /// <summary>是否静音。</summary>
    bool IsMuted { get; set; }

    /// <summary>播放速率 (1.0=正常)。</summary>
    float PlaybackRate { get; set; }

    /// <summary>当前播放会话（未打开时为 null）。</summary>
    IMediaSession? Session { get; }

    /// <summary>状态变更事件。</summary>
    event EventHandler<MediaStateChangedEventArgs> StateChanged;

    /// <summary>错误事件。</summary>
    event EventHandler<MediaErrorEventArgs> ErrorOccurred;

    /// <summary>位置变更事件。</summary>
    event EventHandler<TimeSpan> PositionChanged;

    /// <summary>字幕帧到达事件（null = 无活动字幕，UI 清除显示）。</summary>
    event EventHandler<SubtitleFrame?> SubtitleReceived;

    /// <summary>
    /// 音频采样数据到达事件（供 AudioVisualizer 等可视化器消费）。
    /// 由音频管线在每帧提交给输出前<b>同步触发</b>（音频管线线程），
    /// 订阅方<b>仅可只读借用</b>传入的 <see cref="AudioFrame"/>：须在其回调内同步拷贝所需数据
    /// （PCM 字节 / 采样格式 / 采样率 / 声道数），<b>不得 Dispose、不得跨线程持有该帧引用</b>。
    /// 未订阅时不触发，保持 V1 兼容（零额外开销）。
    /// </summary>
    /// <remarks>事件参数为契约层类型 <see cref="AudioFrame"/>，零外部引用合规。</remarks>
    event Action<AudioFrame>? AudioDataAvailable;

    /// <summary>
    /// 视频帧到达事件（供 Skia 软渲染 Presenter 等消费）。
    /// 由视频管线在 <see cref="SyncAction.Present"/> 分支、且 UI 已订阅（Skia 模式）时<b>同步触发</b>
    /// （视频管线线程），订阅方<b>仅可只读借用</b>传入的 <see cref="VideoFrame"/>：须在其回调内同步拷贝所需数据
    /// （像素缓冲 / 宽高 / 像素格式），<b>不得 Dispose、不得跨线程持有该帧引用</b>。
    /// 未订阅（D3D11 原生 GPU 模式）时管线直接 Present 到已 Attach 的共享 SwapChain，不触发此事件，保持零额外开销。
    /// </summary>
    /// <remarks>事件参数为契约层类型 <see cref="VideoFrame"/>，零外部引用合规。</remarks>
    event Action<VideoFrame>? VideoFrameAvailable;

    // IDisposable.Dispose() — 同步快速释放兜底
    // IAsyncDisposable.DisposeAsync() — 异步完整释放（推荐）
}

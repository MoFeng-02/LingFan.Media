namespace LingFan.Media.Abstractions;

/// <summary>
/// 最高层媒体播放器接口。
/// </summary>
/// <remarks>
/// <para>线程安全：公共方法线程安全，可在任意线程调用。</para>
/// <para>CancellationToken 传播策略：</para>
/// <list type="bullet">
/// <item>需要 CT 的方法：OpenAsync / StopAsync / SeekAsync</item>
/// <item>不需要 CT 的方法：PlayAsync / PauseAsync——不接 CT 是有意为之：这两个方法签名上不提供取消，
/// 实现内部可能仍含等待（seek 复位、管线启停），失败经 <see cref="ErrorOccurred"/> 上报而非靠取消令牌</item>
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

    /// <summary>停止播放。不阻塞、不等待管线线程退出；<paramref name="ct"/> 当前不受理（保留位）。</summary>
    /// <remarks>
    /// <para><b>实现现状</b>：当前实现只下发停止请求并返回已完成的 Task，<b>完全忽略 <paramref name="ct"/></b>，
    /// 也不等待解码/渲染线程真正退出——停止是异步完成的。保留该参数是为了将来支持「等待式停止」时不破坏签名。</para>
    /// <para>因此调用方不得把本方法返回视为「管线线程已停」的凭据；需要该保证时应自行等待或重新
    /// <see cref="OpenAsync"/>。</para>
    /// </remarks>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>定位到指定位置。支持取消。</summary>
    /// <remarks>
    /// <para><b>失败语义</b>：定位失败必须<b>抛异常</b>而不是静默返回——当前实现先触发
    /// <see cref="ErrorOccurred"/>（<c>SeekFailed</c>）再抛出原异常，调用方据此可只处理异常、
    /// 也可只订阅事件。</para>
    /// <para><b>前置条件</b>：调用方不得在未打开媒体源或仍在打开中时调用——此时没有可定位的解封装器，
    /// 定位无从执行。应有语义是抛 <see cref="InvalidOperationException"/>，不得静默成功。</para>
    /// <para><b>实现现状</b>：当前实现<b>尚未设置该状态守卫</b>。未打开时内部解封装器为空，方法会跳过
    /// 定位而继续推进时钟、刷新管线并重填缓冲，表现为「静默成功但不移动」。因此在实现补齐守卫之前，
    /// 调用方<b>必须自行确认媒体源已成功打开</b>（<see cref="State"/> 不处于 <c>Opening</c> / <c>Error</c>
    /// 等未就绪态），不得依赖本方法抛异常来发现误用。</para>
    /// <para><b>精度</b>：上层只承诺「已下发定位请求」，落点精度由解封装器决定
    /// （见 <c>IMediaDemuxer.SeekAsync</c> 的返回值语义）。</para>
    /// </remarks>
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

    /// <summary>
    /// 无头 / 服务端处理模式（默认 <see cref="ProcessingMode.RealTime"/>，可切 <see cref="ProcessingMode.Fastest"/>）。
    /// 设 <see cref="ProcessingMode.Fastest"/> 时关掉音视频同步、无头音频输出不实时节流，
    /// 所有视频帧直接放行，适用于转码 / 离线 ML 等越快越好的批量处理场景。
    /// 可在 <see cref="OpenAsync"/> 前后设置；Open 后设置即时生效（同步内存操作，无 I/O）。
    /// </summary>
    ProcessingMode Mode { get; set; }

    /// <summary>当前播放会话（未打开时为 null）。</summary>
    IMediaSession? Session { get; }

    /// <summary>状态变更事件。</summary>
    /// <remarks>只通报状态迁移，不承载错误细节；非法/被忽略的迁移不会触发本事件。</remarks>
    event EventHandler<MediaStateChangedEventArgs> StateChanged;

    /// <summary>错误事件。</summary>
    /// <remarks>
    /// <para>只通报错误，<b>不隐式改变状态</b>：订阅方收到后不得假定 <see cref="State"/> 已变为
    /// <c>Error</c>。</para>
    /// <para>致命错误另有通道——<see cref="OpenAsync"/> 会抛出异常并由实现把状态置为 <c>Error</c>，
    /// 此时本事件与 <see cref="StateChanged"/> 都会出现，但<b>两者先后顺序不作保证</b>，
    /// 订阅方须各自幂等处理，不得依赖次序推断因果。</para>
    /// </remarks>
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
    /// 未订阅时不触发（零额外开销）。
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

    /// <summary>累计视频丢帧数（诊断/可观测性，只读）。正常播放到结尾时若此值 ≈ 文件视频帧数 − 实际呈现帧数，说明末段尾帧被同步器判 Drop 而非呈现。</summary>
    long VideoDroppedFrames { get; }

    // IDisposable.Dispose() — 同步快速释放兜底
    // IAsyncDisposable.DisposeAsync() — 异步完整释放（推荐）
}

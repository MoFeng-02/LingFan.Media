using Microsoft.Extensions.Logging;
using LingFan.Media.Core.Platform;

namespace LingFan.Media.Core;

/// <summary>
/// 媒体播放器实现。整个媒体系统的最高层组件，协调所有子组件完成播放。
/// </summary>
/// <remarks>
/// <para>线程安全：公共方法线程安全，可在任意线程调用。</para>
/// <para>Session 级对象（Clock/BufferManager/Synchronizer/Pipeline/Decoder/Renderer/Output）
/// 在 OpenAsync 中延迟创建，不在构造函数中。</para>
/// <para>DisposeAsync 11 步释放，每步独立 try-catch 不中断。</para>
/// <para>同步 Dispose 兜底做自己的同步清理路径，禁止 DisposeAsync().GetResult()（伪异步）。</para>
/// </remarks>
public sealed class MediaPlayer : IMediaPlayer
{
    private readonly IMediaStreamFactory _streamFactory;
    private readonly IMediaDemuxerFactory _demuxerFactory;
    private readonly IVideoDecoderFactory _videoDecoderFactory;
    private readonly IAudioDecoderFactory _audioDecoderFactory;
    private readonly ISubtitleDecoderFactory? _subtitleDecoderFactory;
    private readonly IVideoRendererFactory _videoRendererFactory;
    private readonly IAudioOutputFactory _audioOutputFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MediaPlayer> _logger;
    private readonly MediaPlayerOptions _options;

    // Session 级对象（OpenAsync 中延迟创建）
    private IMediaStream? _stream;
    private IMediaDemuxer? _demuxer;
    private MediaSession? _session;
    private MediaClock? _clock;
    private BufferManager? _bufferManager;
    private Synchronizer? _synchronizer;
    private FrameQueue? _frameQueue;
    private SampleQueue? _sampleQueue;
    private IVideoDecoder? _videoDecoder;
    private IAudioDecoder? _audioDecoder;
    private ISubtitleDecoder? _subtitleDecoder;
    private IVideoRenderer? _videoRenderer;
    private IAudioOutput? _audioOutput;
    private VideoPipeline? _videoPipeline;
    private AudioPipeline? _audioPipeline;
    private SubtitleProcessor? _subtitleProcessor;
    private MediaPipelineHost? _pipelineHost;

    /// <summary>累计视频丢帧数（诊断/可观测性，只读转发到管线宿主）。</summary>
    public long VideoDroppedFrames => _pipelineHost?.VideoDroppedFrames ?? 0;

    /// <summary>高精度系统定时器是否已开启（配对 timeBeginPeriod / timeEndPeriod，避免泄漏）。</summary>
    private bool _hpTimerActive;
    private readonly PlaybackController _controller = new();

    // V2 帧对象池（Session 级）
    private FramePool<VideoFrame>? _videoFramePool;
    private FramePool<AudioFrame>? _audioFramePool;

    // 播放控制（音量/静音/速率为本地字段；状态机交由 PlaybackController，V2-06 C1）
    private float _volume = 1.0f;
    private ProcessingMode _mode = ProcessingMode.RealTime;

    // V2-06 C5/C6: 后处理变换链（中立 BCL 委托）。由 DI/Extensions 从 Video/Audio 模块的具体
    // 处理器/音量/混音转换而来；Core 不依赖 Video/Audio 模块，保持分层倒置避免。
    private readonly IReadOnlyList<Func<VideoFrame, VideoFrame?>>? _videoTransforms;
    private readonly IReadOnlyList<Func<AudioFrame, AudioFrame>>? _audioTransforms;
    private readonly Action? _videoTransformsReset;
    private readonly Action? _audioTransformsReset;
    private Action<AudioFrame>? _audioDataSink;
    private Action<VideoFrame>? _videoFrameSink;
    private bool _isMuted;
    private float _playbackRate = 1.0f;
    private Timer? _positionTimer;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="MediaPlayer"/> 的新实例。
    /// </summary>
    /// <param name="streamFactory">媒体流工厂。</param>
    /// <param name="demuxerFactory">解封装器工厂。</param>
    /// <param name="videoDecoderFactory">视频解码器工厂。</param>
    /// <param name="audioDecoderFactory">音频解码器工厂。</param>
    /// <param name="subtitleDecoderFactory">字幕解码器工厂（可为 null）。</param>
    /// <param name="videoRendererFactory">视频渲染器工厂。</param>
    /// <param name="audioOutputFactory">音频输出工厂。</param>
    /// <param name="loggerFactory">日志工厂（用于创建子组件 logger）。</param>
    /// <param name="logger">播放器日志器。</param>
    /// <param name="audioTransformsReset">音频效果状态重置委托（V2-08.1，中立委托，可为 null）。由 Audio 模块把各 <c>IAudioEffect.Reset</c> 合并而来，Core 不依赖 Audio 模块。</param>
    public MediaPlayer(
        IMediaStreamFactory streamFactory,
        IMediaDemuxerFactory demuxerFactory,
        IVideoDecoderFactory videoDecoderFactory,
        IAudioDecoderFactory audioDecoderFactory,
        ISubtitleDecoderFactory? subtitleDecoderFactory,
        IVideoRendererFactory videoRendererFactory,
        IAudioOutputFactory audioOutputFactory,
        ILoggerFactory loggerFactory,
        ILogger<MediaPlayer> logger,
        IReadOnlyList<Func<VideoFrame, VideoFrame?>>? videoTransforms = null,
        IReadOnlyList<Func<AudioFrame, AudioFrame>>? audioTransforms = null,
        Action? videoTransformsReset = null,
        Action? audioTransformsReset = null,
        MediaPlayerOptions? options = null)
    {
        _streamFactory = streamFactory;
        _demuxerFactory = demuxerFactory;
        _videoDecoderFactory = videoDecoderFactory;
        _audioDecoderFactory = audioDecoderFactory;
        _subtitleDecoderFactory = subtitleDecoderFactory;
        _videoRendererFactory = videoRendererFactory;
        _audioOutputFactory = audioOutputFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _videoTransforms = videoTransforms;
        _audioTransforms = audioTransforms;
        _videoTransformsReset = videoTransformsReset;
        _audioTransformsReset = audioTransformsReset;
        _options = options ?? new MediaPlayerOptions();
    }

    /// <inheritdoc />
    public MediaState State => _controller.CurrentState;

    /// <inheritdoc />
    public TimeSpan Position => _clock?.Position ?? TimeSpan.Zero;

    /// <inheritdoc />
    public TimeSpan Duration => _session?.Duration ?? TimeSpan.Zero;

    /// <inheritdoc />
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_audioOutput != null)
                _audioOutput.Volume = _isMuted ? 0f : _volume;
        }
    }

    /// <inheritdoc />
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            if (_audioOutput != null)
                _audioOutput.Volume = _isMuted ? 0f : _volume;
        }
    }

    /// <inheritdoc />
    public float PlaybackRate
    {
        get => _playbackRate;
        set
        {
            _playbackRate = value;
            if (_clock != null)
                _clock.Speed = value;
        }
    }

    /// <inheritdoc />
    public ProcessingMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            ApplyMode();
        }
    }

    /// <summary>
    /// 将当前 <see cref="ProcessingMode"/> 应用到同步器与音频输出。
    /// 最快模式：同步器放行所有视频帧、无头音频输出不实时节流；实时模式反之。
    /// <see cref="OpenAsync"/> 中（同步器 / 音频输出已创建后）会再次调用，确保 Open 前设置的 Mode 生效。
    /// </summary>
    private void ApplyMode()
    {
        if (_synchronizer != null)
            _synchronizer.RealTimeSync = (_mode == ProcessingMode.RealTime);
        if (_audioOutput is IRealtimePacedOutput paced)
            paced.PaceRealTime = (_mode == ProcessingMode.RealTime);
    }

    /// <inheritdoc />
    public IMediaSession? Session => _session;

    /// <inheritdoc />
    public event EventHandler<MediaStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<MediaErrorEventArgs>? ErrorOccurred;

    /// <inheritdoc />
    public event EventHandler<TimeSpan>? PositionChanged;

    /// <inheritdoc />
    public event EventHandler<SubtitleFrame?>? SubtitleReceived;

    /// <inheritdoc />
    /// <remarks>
    /// 采用 <see cref="Action{AudioFrame}"/> 而非 <see cref="EventHandler{T}"/>，因为音频采样数据是
    /// 高频、纯内存、需订阅方同步借用的事件（见 AudioPipeline 的 _audioDataSink 触发点）。
    /// 订阅/退订直接转发到内部字段 <c>_audioDataSink</c>，由音频管线线程在 Submit 之前同步触发。
    /// 该事件不引入任何异步或 I/O，订阅方须只读借用并在返回前拷贝所需数据，绝对不构成伪异步。
    /// </remarks>
    public event Action<AudioFrame>? AudioDataAvailable
    {
        add => _audioDataSink += value;
        remove => _audioDataSink -= value;
    }

    /// <inheritdoc />
    public event Action<VideoFrame>? VideoFrameAvailable
    {
        add => _videoFrameSink += value;
        remove => _videoFrameSink -= value;
    }

    /// <inheritdoc />
    public async Task OpenAsync(IMediaSource source, CancellationToken ct = default)
    {
        // V2-06 C1: 重置状态机（从 Error/Stopped 恢复到 Idle）后再进入 Opening
        _controller.Reset();
        TransitionState(MediaState.Opening);

        try
        {
            // 1. 创建流（异步优先：CreateAsync 为双版本接口首选，内部做 DNS 解析 + SSRF 校验，真实 I/O 必须 await）
            _stream = await _streamFactory.CreateAsync(source, ct);

            // 2. 创建并打开 Demuxer
            _demuxer = _demuxerFactory.Create(_stream);
            await _demuxer.OpenAsync(_stream, ct);

            // 3. 创建会话
            var duration = _demuxer.Metadata.Duration;
            var isLive = source.Type == MediaSourceType.Network;
            _session = new MediaSession(source, _demuxer.Tracks, _demuxer.Metadata, duration, isLive);

            // 4. 创建管线内部对象
            _clock = new MediaClock();
            _frameQueue = new FrameQueue();
            _sampleQueue = new SampleQueue();
            _synchronizer = new Synchronizer(_clock);
            // 根治方案（LINGFAN_CLOCK_AUDIO_POS=1）：用音频设备真实播放游标驱动视频主时钟，
            // 取代批提交内逐帧 SyncTo 的突发锯齿。闭包延后读取 _audioOutput（此时尚未 Create，调用时已就绪）。
            if (ClockTuning.UseAudioPlaybackClock)
                _synchronizer.SetMasterClockProvider(() => _audioOutput?.GetPlaybackPositionDirect() ?? TimeSpan.Zero);
            _bufferManager = new BufferManager(_demuxer, _loggerFactory.CreateLogger<BufferManager>());

            // 5. 创建解码器（延迟创建，需要 codec 信息）
            var videoTrack = _session.SelectedVideoTrack;
            var audioTrack = _session.SelectedAudioTrack;
            var subtitleTrack = _session.SelectedSubtitleTrack;

            if (videoTrack != null && videoTrack.VideoCodec.HasValue)
            {
                // 透传解封装器提取的编解码器私有配置（H264/H265 的 SPS+PPS）→ 解码器输入类型
                _videoDecoder = _videoDecoderFactory.Create(videoTrack.VideoCodec.Value, new VideoSettings
                {
                    CodecConfiguration = videoTrack.VideoInfo?.CodecConfiguration ?? default
                });
            }

            if (audioTrack != null && audioTrack.AudioCodec.HasValue)
            {
                // 从 MediaPlayerOptions 透传音频目标配置（V2-10 P1）：任一字段为 null 时
                // FFmpegAudioDecoder 回退到源媒体参数，B11 重采样仅在显式配置目标时触发。
                var audioSettings = new AudioSettings
                {
                    OutputSampleRate = _options.AudioOutputSampleRate,
                    OutputChannels = _options.AudioOutputChannels,
                    OutputSampleFormat = _options.AudioOutputSampleFormat,
                };
                _audioDecoder = _audioDecoderFactory.Create(audioTrack.AudioCodec.Value, audioSettings);

                // 直通型解码器（MediaFoundation：真正的解码由 SourceReader 内部完成，解码器只包装 PCM 字节）
                // 无从自知输出格式，须由此注入解封装层实测的 PCM 参数。
                // 关键性：下方 _audioOutput.Initialize 以 OutputSampleRate/OutputChannels 打开 WASAPI 设备，
                // 参数错误 → 音高/节奏错乱（早期 MF 解码器硬编码 44100/2，遇 48kHz 媒体即失真）。
                // FFmpeg 解码器不实现此接口（自带 codec context，参数自知），pattern matching 自动跳过。
                if (_audioDecoder is IAudioSourceFormatAware audioFormatAware
                    && audioTrack.AudioInfo is { SampleRate: > 0, Channels: > 0 } audioInfo)
                {
                    audioFormatAware.SetSourceFormat(
                        audioInfo.SampleRate,
                        audioInfo.Channels,
                        audioInfo.BitsPerSample switch
                        {
                            32 => SampleFormat.S32,
                            _ => SampleFormat.S16
                        });
                }
            }

            // V2: 创建帧对象池并注入解码器（Session 级）
            _videoFramePool = new FramePool<VideoFrame>(
                factory: static () => new VideoFrame(),
                reset: static frame => frame.Reset(0, 0, default, null, default, default, false),
                maxSize: 16);
            _audioFramePool = new FramePool<AudioFrame>(
                factory: static () => new AudioFrame(),
                // V2-05: 零拷贝 AudioFrame 持有原生引用计数所有者，Return 时必须经
                // Reset 释放旧所有者（原生引用计数减一），否则池内滞留帧会泄漏原生内存。
                reset: static frame => frame.Reset(default, 0, 0, SampleFormat.S16, default, default, 0),
                maxSize: 16);

            if (_videoDecoder is IFramePoolAware<VideoFrame> videoPoolAware)
                videoPoolAware.SetFramePool(_videoFramePool);
            if (_audioDecoder is IFramePoolAware<AudioFrame> audioPoolAware)
                audioPoolAware.SetFramePool(_audioFramePool);

            // 6. 创建渲染器和输出
            _videoRenderer = _videoRendererFactory.Create();
            _audioOutput = _audioOutputFactory.Create();

            // P0 修复：音频输出设备必须显式初始化。WASAPI 以固定采样率打开设备，
            // Submit 不校验采样率——设备率必须与解码器实际输出率一致，否则节奏/音高错乱。
            // Initialize 为同步 COM 原生边界，保持 sync void（非伪异步），不引入 await。
            if (_audioDecoder != null)
            {
                // 契约对称（InitializeAsync 必须先于 Initialize，WASAPI 强制——设备枚举/COM 准备前置）。
                // 无 I/O 的实现返回 Task.CompletedTask（非伪异步）。
                await _audioOutput.InitializeAsync(ct);
                _audioOutput.Initialize(_audioDecoder.OutputSampleRate, _audioDecoder.OutputChannels);
            }

            // 7. 设置轨道索引
            _bufferManager.SetTrackIndices(videoTrack?.Index ?? -1, audioTrack?.Index ?? -1);

            // 8. 创建管线
            if (_videoDecoder != null && _videoRenderer != null && videoTrack != null)
            {
                _videoPipeline = new VideoPipeline(
                    _bufferManager.VideoPacketQueue, _videoDecoder, _videoRenderer,
                    _frameQueue, _synchronizer, _clock,
                    _loggerFactory.CreateLogger<VideoPipeline>(),
                _videoFramePool,
                _videoTransforms,
                _videoTransformsReset,
                videoFrameSink: frame =>
                {
                    // 视频帧路由（订阅时机无关）：lambda 在管线触发时读取当前 _videoFrameSink 字段——
                    // UI 已订阅（Skia 软渲染）→ 投递 sink；未订阅（D3D11 原生 GPU）→ 直接 Present 到共享 SwapChain。
                    // 注意：必须显式路由，不能仅转发——否则 D3D11 模式（无订阅方）会因 lambda 非空而
                    // 永不调用渲染器，导致视频不显示（T19 回归修复）。
                    if (_videoFrameSink != null)
                        _videoFrameSink(frame);              // Skia 软渲染（UI 已订阅 VideoFrameAvailable）
                    else
                        _videoRenderer?.Present(frame);     // D3D11 原生 GPU（管线线程直接呈现到共享 SwapChain）
                });
            }

            if (_audioDecoder != null && _audioOutput != null && audioTrack != null)
            {
                _audioPipeline = new AudioPipeline(
                    _bufferManager.AudioPacketQueue, _audioDecoder, _audioOutput,
                    _sampleQueue, _synchronizer, _clock,
                    _loggerFactory.CreateLogger<AudioPipeline>(),
                    _audioFramePool,
                    _audioTransforms,
                    _audioTransformsReset,
                    audioDataSink: frame => _audioDataSink?.Invoke(frame));
            }

            // 9. 字幕轨道（条件创建，仅有字幕轨道时）
            if (subtitleTrack != null && _subtitleDecoderFactory != null)
            {
                _subtitleDecoder = _subtitleDecoderFactory.Create(subtitleTrack);
                _subtitleProcessor = new SubtitleProcessor(
                    _subtitleDecoder, _bufferManager.SubtitlePacketQueue, _clock,
                    _loggerFactory.CreateLogger<SubtitleProcessor>());
                _subtitleProcessor.SubtitleReceived += OnSubtitleReceived;
            }

            // 10. 管线宿主
            _pipelineHost = new MediaPipelineHost();
            _pipelineHost.Attach(_videoPipeline, _audioPipeline, _subtitleProcessor);
            _pipelineHost.PlaybackCompleted += OnPlaybackCompleted;

            // 11. 配置网络流缓冲
            if (isLive)
                _bufferManager.ConfigureForNetworkStream();

            // 12. 启动缓冲
            TransitionState(MediaState.Buffering);
            await _bufferManager.StartAsync(ct);

            // 13. 就绪
            TransitionState(MediaState.Idle);

            // 13.5 应用处理模式（同步器 / 音频输出已就绪；确保 Open 前设置的 Mode 生效）
            ApplyMode();

            // 14. 启动位置定时器（33ms = ~30fps）
            _positionTimer = new Timer(OnPositionTimer, null,
                TimeSpan.FromMilliseconds(33), TimeSpan.FromMilliseconds(33));
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync();
            TransitionState(MediaState.Idle);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开媒体源失败");
            await CleanupAsync();
            var errorArgs = new MediaErrorEventArgs(
                MediaErrorCode.SourceOpenFailed, "打开媒体源失败", ex, isFatal: true);
            _controller.OnError(errorArgs); // V2-06 C1
            TransitionState(MediaState.Error);
            ErrorOccurred?.Invoke(this, errorArgs);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task PlayAsync()
    {
        try
        {
            if (_controller.CurrentState == MediaState.Ended)
            {
                // 🔴 重播（Ended → Playing 无缝从头）：流已自然排干，解码/呈现双管线循环已退出
                // （PipelineLoop/DecodeLoop break，_isRunning=false）。重播需：
                //   1) SeekAsync(0) 回绕 demuxer 到起点、重填缓冲通道、重置解码器/时钟基准；
                //   2) 时钟先归零并启动（用归零后的基准做同步判定，避免首帧被判「过去太久」被 Drop）；
                //   3) _pipelineHost.Start() 因 _isRunning==false 会重建 CTS 并重启双管线循环
                //      （VideoPipeline/AudioPipeline 的 Start 已支持从排干态重启）。
                await SeekAsync(TimeSpan.Zero);
                _clock?.Reset();
                _clock?.Start();
                _pipelineHost?.Start();
            }
            else
            {
                _clock?.Start();
                _pipelineHost?.Start();
            }
            EnsureHighPrecisionTimer();
            TransitionState(MediaState.Playing);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "播放失败");
            ErrorOccurred?.Invoke(this, new MediaErrorEventArgs(
                MediaErrorCode.Unknown, "播放失败", ex));
        }
    }

    /// <inheritdoc />
    public Task PauseAsync()
    {
        try
        {
            _clock?.Pause();
            _pipelineHost?.Pause();
            TransitionState(MediaState.Paused);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "暂停失败");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default)
    {
        try
        {
            _clock?.Reset();
            _pipelineHost?.Stop();
            ReleaseHighPrecisionTimer();
            _bufferManager?.Clear();
            TransitionState(MediaState.Stopped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止失败");
        }

        // 纯内存操作，返回 Task.CompletedTask（接口契约层）
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        try
        {
            // 1. 先停 BufferManager 读取线程（防止与 Demuxer.SeekAsync 竞争）
            _bufferManager?.Stop();
            if (_bufferManager?.ReaderTask != null)
            {
                try { await _bufferManager.ReaderTask; } catch { }
            }

            // 2. 清空缓冲队列（V2: 移到 Flush 之前，确保管线恢复后包队列已空，不会处理旧包）
            _bufferManager?.Clear();

            // 3. 时钟跳转
            if (_clock != null)
                _synchronizer?.OnSeek(position);

            // 4. Demuxer 定位（此时读取线程已退出，无竞争）
            if (_demuxer != null)
                await _demuxer.SeekAsync(position, ct);

            // 5. 管线刷新（V2 修复 L2: 异步等待管线暂停确认后再清空+重置，无 Thread.Sleep 阻塞。
            //    内部先暂停管线线程防止 DecodeAsync 与 Reset 竞争，清空帧队列+重置解码器，然后恢复）
            if (_pipelineHost != null)
                await _pipelineHost.FlushAsync();

            // 6. 重新填充缓冲（从新的 Demuxer 位置开始读取）
            if (_bufferManager != null)
            {
                await _bufferManager.StartAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "定位失败");
            ErrorOccurred?.Invoke(this, new MediaErrorEventArgs(
                MediaErrorCode.SeekFailed, "定位失败", ex));
            throw;
        }
    }

    /// <summary>
    /// 开启高精度系统定时器（若已开启则幂等），供视频帧精确等待消抖。
    /// </summary>
    private void EnsureHighPrecisionTimer()
    {
        if (!ClockTuning.HighPrecisionTimer || _hpTimerActive)
            return;
        try
        {
            WinMm.TimeBeginPeriod(1);
            _hpTimerActive = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "开启高精度定时器失败，帧等待将退回默认 15.6ms 分辨率");
        }
    }

    /// <summary>
    /// 关闭高精度系统定时器（与 <see cref="EnsureHighPrecisionTimer"/> 配对，幂等）。
    /// </summary>
    private void ReleaseHighPrecisionTimer()
    {
        if (!_hpTimerActive)
            return;
        try
        {
            WinMm.TimeEndPeriod(1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭高精度定时器失败");
        }
        finally
        {
            _hpTimerActive = false;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _positionTimer?.Dispose();

        // 11 步释放，每步独立 try-catch 不中断

        // 1. 停管线线程 (cts.Cancel + join 5s 超时)
        await Step_StopPipelinesAsync();

        // 2. 清空帧队列 (V2: 归还到 FramePool)
        Step_ClearFrameQueues();

        // 3. 刷新解码器 (FlushAsync 取剩余帧并 Dispose)
        await Step_FlushDecodersAsync();

        // 4. 释放解码器
        Step_DisposeDecoders();

        // 5. 释放渲染器 (Detach + 释放 SwapChain + GPU Flush)
        Step_DisposeRenderer();

        // 6. 释放音频输出
        Step_DisposeAudioOutput();

        // 7. 释放帧对象池（所有帧已归还）
        Step_DisposeFramePools();

        // 8. 清空 BufferManager
        Step_ClearBufferManager();

        // 9. 关闭 Demuxer
        await Step_CloseDemuxerAsync();

        // 10. 关闭 MediaStream
        Step_CloseStream();

        // 11. 重置 Clock
        Step_ResetClock();

        // 12. 关闭 Session
        await Step_CloseSessionAsync();

        // 13. 归还高精度定时器（与 PlayAsync 配对，避免整机定时器泄漏）
        ReleaseHighPrecisionTimer();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 同步快速释放兜底——做自己的同步清理路径
        // 铁律：禁止调用 DisposeAsync().GetAwaiter().GetResult()（伪异步）
        try
        {
            _positionTimer?.Dispose();

            // 同步停止管线（发 cts.Cancel）
            try { _bufferManager?.Stop(); } catch { }
            try { _videoPipeline?.Stop(); } catch { }
            try { _audioPipeline?.Stop(); } catch { }
            try { _subtitleProcessor?.Stop(); } catch { }

            // 等待管线线程退出（同步，2s 超时）
            // 确保 SemaphoreSlim.Dispose 不与正在进行的 WaitAsync/Release 并发
            try
            {
                var tasks = new List<Task>();
                if (_videoPipeline?.PipelineTask != null)
                    tasks.Add(_videoPipeline.PipelineTask);
                if (_audioPipeline?.PipelineTask != null)
                    tasks.Add(_audioPipeline.PipelineTask);
                if (_subtitleProcessor?.ProcessTask != null)
                    tasks.Add(_subtitleProcessor.ProcessTask);
                // M1：补齐 BufferManager 读取线程——缺口 P1，原先不等它直接释放 Demuxer（跨线程 UAF 隐患）。
                if (_bufferManager?.ReaderTask != null)
                    tasks.Add(_bufferManager.ReaderTask);
                if (tasks.Count > 0)
                    Task.WaitAll(tasks.ToArray(), MediaPipelineTimeouts.PipelineTaskWait);
            }
            catch { } // AggregateException（线程异常）或 TimeoutException 均忽略

            // 线程已退出（或超时），安全释放管线内部资源（解码锁和 CTS）
            try { _videoPipeline?.Dispose(); } catch { }
            try { _audioPipeline?.Dispose(); } catch { }
            try { _subtitleProcessor?.Dispose(); } catch { }

            // V2: 同步清空帧队列（归还到池）
            try { _frameQueue?.Clear(_videoFramePool); } catch { }
            try { _sampleQueue?.Clear(_audioFramePool); } catch { }

            // 同步释放原生资源（每步独立 try-catch）
            try { _videoDecoder?.Dispose(); } catch { }
            try { _audioDecoder?.Dispose(); } catch { }
            try { _subtitleDecoder?.Dispose(); } catch { }
            // 注意：共享单例渲染器（D3D11RendererFactory 缓存单例）不在此处置——
            // 其生命周期归工厂，SwapChain 由 UI Presenter.Detach 释放（方案 A）。
            try { _audioOutput?.Dispose(); } catch { }
            try { _demuxer?.Close(); } catch { }
            try { _demuxer?.Dispose(); } catch { }
            try { _stream?.Close(); } catch { }
            try { _bufferManager?.Clear(); } catch { }

            // 归还高精度定时器（与 PlayAsync 配对，避免整机定时器泄漏）
            try { ReleaseHighPrecisionTimer(); } catch { }

            // V2: 释放帧对象池
            try { _videoFramePool?.Dispose(); } catch { }
            try { _audioFramePool?.Dispose(); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步 Dispose 兜底异常");
        }
    }

    private void TransitionState(MediaState newState)
    {
        // V2-06 C1: 委托给 PlaybackController 管理状态转换的合法性与原子性
        var oldState = _controller.CurrentState;
        if (_controller.TransitionTo(newState))
        {
            if (oldState != newState)
            {
                StateChanged?.Invoke(this, new MediaStateChangedEventArgs(oldState, newState));
            }
        }
        else
        {
            _logger.LogWarning("忽略非法播放状态转换：{From} -> {To}", oldState, newState);
        }
    }

    private void OnPositionTimer(object? state)
    {
        if (_clock != null)
        {
            PositionChanged?.Invoke(this, _clock.Position);
        }
    }

    /// <summary>
    /// 播放自然完成回调（由 <see cref="MediaPipelineHost.PlaybackCompleted"/> 在 video/audio 两管线
    /// 均耗尽流末后于管线线程触发）。冻结主时钟（Position 停在末尾，避免音频设备时钟继续推进致
    /// Position 越过时长）、归还高精度定时器、转 <see cref="MediaState.Ended"/>。
    /// 已提交的音频尾部缓冲仍由 WASAPI 设备自然放完，不被中断。
    /// </summary>
    /// <remarks>
    /// 此方法运行于管线线程，所有被调用成员（MediaClock.Pause / WinMm / PlaybackController）均线程安全。
    /// 若当前状态非 Playing（如用户在末尾刚好 Paused），状态机会忽略该转换（Paused→Ended 非法），属可接受的边界情形。
    /// </remarks>
    private void OnPlaybackCompleted(object? sender, EventArgs e)
    {
        try { _clock?.Pause(); } catch { }
        ReleaseHighPrecisionTimer();
        TransitionState(MediaState.Ended);
    }

    private void OnSubtitleReceived(object? sender, SubtitleFrame? frame)
    {
        SubtitleReceived?.Invoke(this, frame);
    }

    // === DisposeAsync 11 步实现 ===

    private async Task Step_StopPipelinesAsync()
    {
        try
        {
            // 先停 BufferManager 读取线程
            _bufferManager?.Stop();

            _videoPipeline?.Stop();
            _audioPipeline?.Stop();
            _subtitleProcessor?.Stop();

            // 等待管线线程退出（5s 超时）
            var timeout = MediaPipelineTimeouts.PipelineJoin;
            var tasks = new List<Task>();

            if (_bufferManager?.ReaderTask != null)
                tasks.Add(_bufferManager.ReaderTask);

            if (_videoPipeline?.PipelineTask != null)
                tasks.Add(_videoPipeline.PipelineTask);

            if (_audioPipeline?.PipelineTask != null)
                tasks.Add(_audioPipeline.PipelineTask);

            if (_subtitleProcessor?.ProcessTask != null)
                tasks.Add(_subtitleProcessor.ProcessTask);

            if (tasks.Count > 0)
            {
                var allTasks = Task.WhenAll(tasks);
                try
                {
                    await allTasks.WaitAsync(timeout);
                }
                catch (TimeoutException)
                {
                    // §12 诊断纪律：精确记录是哪个任务没退出，避免盲猜（未来复现可直接定位）。
                    var pending = new List<string>();
                    if (_bufferManager?.ReaderTask is { } bt && !bt.IsCompleted) pending.Add("BufferManager.ReaderTask");
                    if (_videoPipeline?.PipelineTask is { } vt && !vt.IsCompleted) pending.Add("VideoPipeline.PipelineTask");
                    if (_audioPipeline?.PipelineTask is { } at && !at.IsCompleted) pending.Add("AudioPipeline.PipelineTask");
                    if (_subtitleProcessor?.ProcessTask is { } st && !st.IsCompleted) pending.Add("SubtitleProcessor.ProcessTask");
                    var which = pending.Count > 0 ? string.Join(", ", pending) : "未知（可能已超时但状态竞态）";
                    _logger.LogWarning("管线线程退出超时（{TimeoutMs}ms），未完成任务 = {Pending}；继续释放，原生指针释放由后端 gate 保护，不会 UAF",
                        (int)timeout.TotalMilliseconds, which);
                }
            }

            // 线程已退出（或超时），安全释放管线内部资源（解码锁和 CTS）
            try { _videoPipeline?.Dispose(); } catch { }
            try { _audioPipeline?.Dispose(); } catch { }
            try { _subtitleProcessor?.Dispose(); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisposeAsync 步骤1: 停止管线异常");
        }
    }

    private void Step_ClearFrameQueues()
    {
        try
        {
            _frameQueue?.Clear(_videoFramePool);
            _sampleQueue?.Clear(_audioFramePool);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisposeAsync 步骤2: 清空帧队列异常");
        }
    }

    private async ValueTask Step_FlushDecodersAsync()
    {
        try
        {
            if (_videoDecoder != null)
            {
                var frame = await _videoDecoder.FlushAsync();
                if (frame != null)
                {
                    if (_videoFramePool != null)
                        _videoFramePool.Return(frame);
                    else
                        frame.Dispose();
                }
            }

            if (_audioDecoder != null)
            {
                var frame = await _audioDecoder.FlushAsync();
                if (frame != null)
                {
                    if (_audioFramePool != null)
                        _audioFramePool.Return(frame);
                    else
                        frame.Dispose();
                }
            }

            if (_subtitleDecoder != null)
            {
                _ = await _subtitleDecoder.FlushAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisposeAsync 步骤3: 刷新解码器异常");
        }
    }

    private void Step_DisposeDecoders()
    {
        try { _videoDecoder?.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "释放视频解码器异常"); }
        try { _audioDecoder?.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "释放音频解码器异常"); }
        try { _subtitleDecoder?.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "释放字幕解码器异常"); }
    }

    // 方案 A（P0 修复）共享单例渲染器生命周期说明：
    // _videoRenderer 是 D3D11RendererFactory 的缓存单例，Core 视频管线与 UI 层 D3D11GpuPresenter
    // 通过同一工厂解析到同一 D3D11Renderer 实例（R1==R2）。其生命周期由工厂（DI Singleton）持有，
    // SwapChain 与 HWND 的绑定由 UI Presenter.Detach 管理。
    // 因此 MediaPlayer 绝不能 Detach/Dispose 共享单例——否则会释放 UI 层正在使用（或待重开复用）的
    // 同一实例，导致重开后管线 Present 命中未附加/已释放的渲染器（D3D11 不显示）。
    // 此处刻意不处置渲染器：共享单例由工厂在容器释放时 Dispose，SwapChain 由 UI Presenter 释放。
    private void Step_DisposeRenderer()
    {
    }

    private void Step_DisposeAudioOutput()
    {
        try { _audioOutput?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤6: 释放音频输出异常"); }
    }

    /// <summary>
    /// V2: 释放帧对象池（步骤7，所有帧已归还后调用）。
    /// </summary>
    private void Step_DisposeFramePools()
    {
        try { _videoFramePool?.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤7: 释放视频帧池异常"); }
        try { _audioFramePool?.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤7: 释放音频帧池异常"); }
    }

    private void Step_ClearBufferManager()
    {
        try { _bufferManager?.Clear(); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤7: 清空缓冲异常"); }
    }

    private async ValueTask Step_CloseDemuxerAsync()
    {
        try { if (_demuxer != null) await _demuxer.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤9: 关闭 Demuxer 异常"); }
    }

    private void Step_CloseStream()
    {
        try { _stream?.Close(); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤9: 关闭流异常"); }
    }

    private void Step_ResetClock()
    {
        try { _clock?.Reset(); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤10: 重置时钟异常"); }
    }

    private async ValueTask Step_CloseSessionAsync()
    {
        try
        {
            if (_session != null)
                await _session.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisposeAsync 步骤11: 关闭会话异常");
        }
    }

    private async Task CleanupAsync()
    {
        _positionTimer?.Dispose();
        try { _bufferManager?.Stop(); } catch { }
        try { _videoPipeline?.Stop(); } catch { }
        try { _audioPipeline?.Stop(); } catch { }
        try { _subtitleProcessor?.Stop(); } catch { }

        // 等待管线线程退出（异步，2s 超时）
        // OpenAsync 失败时管线通常未 Start（PipelineTask==null），但防御性处理
        var tasks = new List<Task>();
        if (_videoPipeline?.PipelineTask != null)
            tasks.Add(_videoPipeline.PipelineTask);
        if (_audioPipeline?.PipelineTask != null)
            tasks.Add(_audioPipeline.PipelineTask);
        if (_subtitleProcessor?.ProcessTask != null)
            tasks.Add(_subtitleProcessor.ProcessTask);
        // M2：补齐 BufferManager 读取线程（与 M1 对称）。
        if (_bufferManager?.ReaderTask != null)
            tasks.Add(_bufferManager.ReaderTask);
        if (tasks.Count > 0)
        {
            try { await Task.WhenAll(tasks).WaitAsync(MediaPipelineTimeouts.PipelineTaskWait); }
            catch { }
        }

        // 线程已退出（或超时），安全释放管线内部资源
        try { _videoPipeline?.Dispose(); } catch { }
        try { _audioPipeline?.Dispose(); } catch { }
        try { _subtitleProcessor?.Dispose(); } catch { }
        try { _frameQueue?.Clear(_videoFramePool); } catch { }
        try { _sampleQueue?.Clear(_audioFramePool); } catch { }
        try { _videoDecoder?.Dispose(); } catch { }
        try { _audioDecoder?.Dispose(); } catch { }
        try { _subtitleDecoder?.Dispose(); } catch { }
        // 共享单例渲染器不在此处置（生命周期归工厂，SwapChain 由 UI Presenter.Detach）——方案 A。
        try { _audioOutput?.Dispose(); } catch { }
        try { _videoFramePool?.Dispose(); } catch { }
        try { _audioFramePool?.Dispose(); } catch { }
        try { _demuxer?.Close(); } catch { }
        try { _demuxer?.Dispose(); } catch { }
        try { _stream?.Close(); } catch { }
        try { _bufferManager?.Clear(); } catch { }
    }
}

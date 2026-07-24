using Microsoft.Extensions.Logging;

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
    private PlaybackController? _controller;

    // 播放控制
    private readonly object _stateLock = new();
    private MediaState _state = MediaState.Idle;
    private float _volume = 1.0f;
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
    public MediaPlayer(
        IMediaStreamFactory streamFactory,
        IMediaDemuxerFactory demuxerFactory,
        IVideoDecoderFactory videoDecoderFactory,
        IAudioDecoderFactory audioDecoderFactory,
        ISubtitleDecoderFactory? subtitleDecoderFactory,
        IVideoRendererFactory videoRendererFactory,
        IAudioOutputFactory audioOutputFactory,
        ILoggerFactory loggerFactory,
        ILogger<MediaPlayer> logger)
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
    }

    /// <inheritdoc />
    public MediaState State
    {
        get { lock (_stateLock) return _state; }
    }

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
    public async Task OpenAsync(IMediaSource source, CancellationToken ct = default)
    {
        TransitionState(MediaState.Opening);

        try
        {
            // 1. 创建流
            _stream = _streamFactory.Create(source);

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
            _bufferManager = new BufferManager(_demuxer, _loggerFactory.CreateLogger<BufferManager>());
            _controller = new PlaybackController();

            // 5. 创建解码器（延迟创建，需要 codec 信息）
            var videoTrack = _session.SelectedVideoTrack;
            var audioTrack = _session.SelectedAudioTrack;
            var subtitleTrack = _session.SelectedSubtitleTrack;

            if (videoTrack != null && videoTrack.VideoCodec.HasValue)
            {
                _videoDecoder = _videoDecoderFactory.Create(videoTrack.VideoCodec.Value, new VideoSettings());
            }

            if (audioTrack != null && audioTrack.AudioCodec.HasValue)
            {
                _audioDecoder = _audioDecoderFactory.Create(audioTrack.AudioCodec.Value, new AudioSettings());
            }

            // 6. 创建渲染器和输出
            _videoRenderer = _videoRendererFactory.Create();
            _audioOutput = _audioOutputFactory.Create();

            // 7. 设置轨道索引
            _bufferManager.SetTrackIndices(videoTrack?.Index ?? -1, audioTrack?.Index ?? -1);

            // 8. 创建管线
            if (_videoDecoder != null && _videoRenderer != null && videoTrack != null)
            {
                _videoPipeline = new VideoPipeline(
                    _bufferManager.VideoPacketQueue, _videoDecoder, _videoRenderer,
                    _frameQueue, _synchronizer, _clock,
                    _loggerFactory.CreateLogger<VideoPipeline>());
            }

            if (_audioDecoder != null && _audioOutput != null && audioTrack != null)
            {
                _audioPipeline = new AudioPipeline(
                    _bufferManager.AudioPacketQueue, _audioDecoder, _audioOutput,
                    _sampleQueue, _synchronizer, _clock,
                    _loggerFactory.CreateLogger<AudioPipeline>());
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

            // 11. 配置网络流缓冲
            if (isLive)
                _bufferManager.ConfigureForNetworkStream();

            // 12. 启动缓冲
            TransitionState(MediaState.Buffering);
            await _bufferManager.StartAsync(ct);

            // 13. 就绪
            TransitionState(MediaState.Idle);

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
            TransitionState(MediaState.Error);
            ErrorOccurred?.Invoke(this, new MediaErrorEventArgs(
                MediaErrorCode.SourceOpenFailed, "打开媒体源失败", ex, isFatal: true));
            throw;
        }
    }

    /// <inheritdoc />
    public Task PlayAsync()
    {
        try
        {
            _clock?.Start();
            _pipelineHost?.Start();
            TransitionState(MediaState.Playing);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "播放失败");
            ErrorOccurred?.Invoke(this, new MediaErrorEventArgs(
                MediaErrorCode.Unknown, "播放失败", ex));
        }

        // 纯内存操作，返回 Task.CompletedTask（接口契约层）
        return Task.CompletedTask;
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

            // 2. 时钟跳转
            if (_clock != null)
                _synchronizer?.OnSeek(position);

            // 3. Demuxer 定位（此时读取线程已退出，无竞争）
            if (_demuxer != null)
                await _demuxer.SeekAsync(position, ct);

            // 4. 解码器重置
            _videoDecoder?.Reset();
            _audioDecoder?.Reset();
            _subtitleDecoder?.Reset();

            // 5. 管线刷新（清帧队列 + decoder.Reset）
            _pipelineHost?.Flush();

            // 6. 清空并重新填充缓冲
            if (_bufferManager != null)
            {
                _bufferManager.Clear();
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _positionTimer?.Dispose();

        // 11 步释放，每步独立 try-catch 不中断

        // 1. 停管线线程 (cts.Cancel + join 5s 超时)
        await Step_StopPipelinesAsync();

        // 2. 清空帧队列 (Dispose 所有帧)
        Step_ClearFrameQueues();

        // 3. 刷新解码器 (FlushAsync 取剩余帧并 Dispose)
        await Step_FlushDecodersAsync();

        // 4. 释放解码器
        Step_DisposeDecoders();

        // 5. 释放渲染器 (Detach + 释放 SwapChain + GPU Flush)
        Step_DisposeRenderer();

        // 6. 释放音频输出
        Step_DisposeAudioOutput();

        // 7. 清空 BufferManager
        Step_ClearBufferManager();

        // 8. 关闭 Demuxer
        Step_CloseDemuxer();

        // 9. 关闭 MediaStream
        Step_CloseStream();

        // 10. 重置 Clock
        Step_ResetClock();

        // 11. 关闭 Session
        await Step_CloseSessionAsync();
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

            // 同步停止管线（只发 cts.Cancel，不等待线程退出）
            try { _bufferManager?.Stop(); } catch { }
            try { _videoPipeline?.Stop(); } catch { }
            try { _audioPipeline?.Stop(); } catch { }
            try { _subtitleProcessor?.Stop(); } catch { }

            // 同步清空帧队列
            try { _frameQueue?.Clear(); } catch { }
            try { _sampleQueue?.Clear(); } catch { }

            // 同步释放原生资源（每步独立 try-catch）
            try { _videoDecoder?.Dispose(); } catch { }
            try { _audioDecoder?.Dispose(); } catch { }
            try { _subtitleDecoder?.Dispose(); } catch { }
            try { _videoRenderer?.Dispose(); } catch { }
            try { _audioOutput?.Dispose(); } catch { }
            try { _demuxer?.Close(); } catch { }
            try { _demuxer?.Dispose(); } catch { }
            try { _stream?.Close(); } catch { }
            try { _bufferManager?.Clear(); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步 Dispose 兜底异常");
        }
    }

    private void TransitionState(MediaState newState)
    {
        MediaState oldState;
        lock (_stateLock)
        {
            oldState = _state;
            _state = newState;
        }

        if (oldState != newState)
        {
            StateChanged?.Invoke(this, new MediaStateChangedEventArgs(oldState, newState));
        }
    }

    private void OnPositionTimer(object? state)
    {
        if (_clock != null)
        {
            PositionChanged?.Invoke(this, _clock.Position);
        }
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
            var timeout = TimeSpan.FromSeconds(5);
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
                    _logger.LogWarning("管线线程退出超时（5s），继续释放");
                }
            }
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
            _frameQueue?.Clear();
            _sampleQueue?.Clear();
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
                frame?.Dispose();
            }

            if (_audioDecoder != null)
            {
                var frame = await _audioDecoder.FlushAsync();
                frame?.Dispose();
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

    private void Step_DisposeRenderer()
    {
        try
        {
            _videoRenderer?.Detach();
            _videoRenderer?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisposeAsync 步骤5: 释放渲染器异常");
        }
    }

    private void Step_DisposeAudioOutput()
    {
        try { _audioOutput?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤6: 释放音频输出异常"); }
    }

    private void Step_ClearBufferManager()
    {
        try { _bufferManager?.Clear(); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤7: 清空缓冲异常"); }
    }

    private void Step_CloseDemuxer()
    {
        try { _demuxer?.Close(); _demuxer?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤8: 关闭 Demuxer 异常"); }
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
        try { _frameQueue?.Clear(); } catch { }
        try { _sampleQueue?.Clear(); } catch { }
        try { _videoDecoder?.Dispose(); } catch { }
        try { _audioDecoder?.Dispose(); } catch { }
        try { _subtitleDecoder?.Dispose(); } catch { }
        try { _videoRenderer?.Dispose(); } catch { }
        try { _audioOutput?.Dispose(); } catch { }
        try { _demuxer?.Close(); } catch { }
        try { _demuxer?.Dispose(); } catch { }
        try { _stream?.Close(); } catch { }
        try { _bufferManager?.Clear(); } catch { }
        await Task.CompletedTask;
    }
}

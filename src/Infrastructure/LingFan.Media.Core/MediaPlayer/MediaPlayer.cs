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
public sealed partial class MediaPlayer : IMediaPlayer
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

    // 帧对象池（Session 级）
    private FramePool<VideoFrame>? _videoFramePool;
    private FramePool<AudioFrame>? _audioFramePool;

    // 播放控制（音量/静音/速率为本地字段；状态机交由 PlaybackController）
    private float _volume = 1.0f;
    private ProcessingMode _mode = ProcessingMode.RealTime;

    // 后处理变换链（中立 BCL 委托）。由 DI/Extensions 从 Video/Audio 模块的具体
    // 处理器/音量/混音转换而来；Core 不依赖 Video/Audio 模块，保持分层倒置避免。
    private readonly IReadOnlyList<Func<VideoFrame, VideoFrame?>>? _videoTransforms;
    private readonly IReadOnlyList<Func<AudioFrame, AudioFrame>>? _audioTransforms;
    private readonly Action? _videoTransformsReset;
    private readonly Action? _audioTransformsReset;
    private Action<AudioFrame>? _audioDataSink;
    private readonly FrameChannel _frameChannel = new();
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
    /// <param name="audioTransformsReset">音频效果状态重置委托（中立委托，可为 null）。由 Audio 模块把各 <c>IAudioEffect.Reset</c> 合并而来，Core 不依赖 Audio 模块。</param>
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
        add
        {
            if (value is not null) _frameChannel.Subscribe(new DelegateFrameSink(value));
        }
        remove
        {
            if (value is not null) _frameChannel.Unsubscribe(new DelegateFrameSink(value));
        }
    }

    /// <inheritdoc />
    public async Task PlayAsync()
    {
        // 状态前置守卫（先于任何管线/时钟动作）：
        //   Playing → 幂等早退（重复 Play 不重启管线、不重走启动编排）；
        //   Opening/Buffering/Error/未打开 → 抛 InvalidOperationException，不得静默启动管线
        //   造成「状态标签与管线真实状况相反」的半态。
        var state = _controller.CurrentState;
        if (state == MediaState.Playing)
            return;
        if (!_controller.CanPlay(_demuxer != null))
            throw new InvalidOperationException($"当前状态 {state} 不允许播放（须先成功打开媒体源，且处于就绪/暂停/停止/结束态）。");

        try
        {
            if (_controller.CurrentState == MediaState.Ended)
            {
                // 重播（Ended → Playing 无缝从头）：流已自然排干，解码/呈现双管线循环已退出
                // （PipelineLoop/DecodeLoop break，_isRunning=false）。重播需：
                //   1) SeekAsync(0) 回绕 demuxer 到起点、重填缓冲通道、重置解码器/时钟基准；
                //   2) 时钟先归零并启动（用归零后的基准做同步判定，避免首帧被判「过去太久」被 Drop）；
                //   3) _pipelineHost.Start() 因 _isRunning==false 会重建 CTS 并重启双管线循环
                //      （VideoPipeline/AudioPipeline 的 Start 已支持从排干态重启）。
                await SeekAsync(TimeSpan.Zero);
                // 重播（Ended→Playing）主时钟武装归零：自然 Ended 时 WASAPI 客户端仍 Running（尾音由设备自然放完），
                // _startStopwatch 持续累计（数值不随重播归零）；而重播视频门控（MediaPipelineHost.StartAsync 第(3)步 SignalAudioReady）
                // 早于音频 Start（第(5)步），若不在此解除武装，预滚动/门控窗口内同步器读到的主时钟是上一遍的陈旧值，
                // 会把 PTS=0 重播首帧判为「落后过多 → Drop」，画面冻结较长时间才切入。此处解除武装后，该窗口主时钟恒为 0，
                // 首帧立即呈现；音频 Start（CaptureStartAnchor）会重新武装。
                _audioOutput?.ResetPlaybackClock();
                // 归零必须先于 StartAsync：新启动编排会在 StartAsync 内部等视频预滚动，
                // 门控放行瞬间就已有帧可判定。若此时 _clock 仍停在上一次播放的结束位置，
                // 无音频轨场景（主时钟回退到 _clock.Position）会把 PTS=0 的首帧判成落后过多 → 全部 Drop。
                // SeekAsync 内 OnSeek→SeekTo(0) 已归零，此处为显式不变量加固（幂等）。
                _clock?.Reset();
                if (_pipelineHost != null) await _pipelineHost.StartAsync();   // 视频预滚动 → 启动音频 → 放行呈现
                _clock?.Start();                     // 时钟在音频就绪后启动，避免起播前空走导致首帧被判过期 Drop
            }
            else
            {
                if (_pipelineHost != null) await _pipelineHost.StartAsync();
                _clock?.Start();
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
        // 状态前置守卫（先于停读线程/清缓冲等任何动作）：未打开或 Opening/Error 态没有可定位的
        // 解封装器，定位无从执行——抛 InvalidOperationException，不得静默推进时钟/重填缓冲。
        if (!_controller.CanSeek(_demuxer != null))
            throw new InvalidOperationException($"当前状态 {_controller.CurrentState} 不允许定位（须先成功打开媒体源）。");

        try
        {
            // 1. 先停 BufferManager 读取线程（防止与 Demuxer.SeekAsync 竞争）
            _bufferManager?.Stop();
            if (_bufferManager?.ReaderTask != null)
            {
                // 取消是预期退出；其余异常说明读线程已因真实 I/O 故障死亡——留痕供诊断，
                // 定位编排继续（demuxer 位置未动，后续步骤自会暴露或收敛）。
                try { await _bufferManager.ReaderTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.LogWarning(ex, "定位前读取线程非取消退出，故障详情见 ReaderFaulted 链路"); }
            }

            // 2. 清空缓冲队列（移到 Flush 之前，确保管线恢复后包队列已空，不会处理旧包）
            _bufferManager?.Clear();

            // 3. Demuxer 定位（此时读取线程已退出，无竞争）
            //    失败必须中止：解封装器留在原位置，若照常推进主时钟并按新位置重填缓冲，
            //    后续包时间戳会整体落后主时钟，表现为成批丢帧与「点了定位但没反应」的静默失效。
            if (_demuxer != null && !await _demuxer.SeekAsync(position, ct))
                throw new InvalidOperationException($"解封装器定位失败（目标 {position}）。");

            // 4. 时钟跳转（仅在定位成功后推进，避免失败路径上主时钟跑到数据前面）
            if (_clock != null)
                _synchronizer?.OnSeek(position);

            // 5. 管线刷新（异步等待管线暂停确认后再清空+重置，无 Thread.Sleep 阻塞。
            //    内部先暂停管线线程防止 DecodeAsync 与 Reset 竞争，清空帧队列+重置解码器，然后恢复）
            if (_pipelineHost != null)
                await _pipelineHost.FlushAsync();

            // 6. 重新填充缓冲（从新的 Demuxer 位置开始读取）
            if (_bufferManager != null)
            {
                // 定位前显式登记状态：播放中定位时通道仍然可写，无需重建；
                // 只有流已结束（通道曾关闭）时才重建，避免把仍在消费的管线连到已关闭的通道上。
                _bufferManager.PrepareForSeek();
                await _bufferManager.StartAsync(ct);

                // 重播（Ended→Playing）修复：流末 EOF 后 BufferManager.StartAsync 会
                // 经 ResetQueues() 重建全新的包通道实例（旧通道已 Complete 不可重开）。两条管线在 OpenAsync
                // 构造时按值捕获了旧通道引用，此处必须把其内部 _packetQueue 重新指向新通道——
                // 否则重播的解码循环会从已 Complete 的旧通道瞬间 EOF，排空 0 帧，二次播放呈现 0 帧。
                // 非 EOF 重播（播放中 Seek）下 ResetQueues 不触发，此处重指向同一实例，幂等无害。
                _videoPipeline?.SetPacketQueue(_bufferManager.VideoPacketQueue);
                _audioPipeline?.SetPacketQueue(_bufferManager.AudioPacketQueue);
            }

            // 7. 恢复消费：管线在定位复位后保持暂停，必须在包缓冲重填完成后才恢复；
            //    若管线在复位时已经退出，由管线自身拉起新的消费循环（幂等，不重复启动）。
            if (_pipelineHost != null && _controller.CurrentState == MediaState.Playing)
                await _pipelineHost.ResumeAfterSeekAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "定位失败");

            // 不变量：本方法任何返回/抛出路径结束后，若 State == Playing，两条管线的包通道必须仍有活着的写入者。
            // 定位发生在「读线程已停、缓冲已清空」之后，若直接抛出，包通道既无写入者也不会被 Complete，
            // 播放器会停在「状态说在播、画面永远不动」的半态——比定位本身失败更难发现，故先收敛再抛出。
            await RecoverAfterFailedSeekAsync(ct);

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

        // 11 步释放，每步独立 try-catch 不中断。
        // 逐步耗时观测：退出缓慢（秒级）问题的定位手段——静默步骤（5~11）的耗时从此可见，
        // 任一步骤 >300ms 立即告警点名，收尾输出全明细（与 [SYNC]/[FREEZE] 诊断风格一致）。
        long disposeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var stepTimings = new List<string>(13);

        void LogStepTiming(string step, long startQpc)
        {
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(startQpc).TotalMilliseconds;
            stepTimings.Add($"{step}={ms:F0}ms");
            Console.Error.WriteLine($"[DISPOSE] ← {step} 完成 {ms:F0}ms");
            if (ms > 300)
                _logger.LogWarning(
                    "[DISPOSE] 步骤 {Step} 耗时 {Ms:F0}ms（>300ms，退出缓慢的定位线索）", step, ms);
        }

        // stderr 直写（不经日志队列）：步骤内若发生原生死亡，最后一条"→ 进入"行即崩溃步骤。
        void StepEnter(string step) =>
            Console.Error.WriteLine($"[DISPOSE] → {step}");

        // 1. 停管线线程 (cts.Cancel + join 5s 超时)
        long stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("停管线");
        await Step_StopPipelinesAsync();
        LogStepTiming("停管线", stepQpc);

        // 2. 清空帧队列 (归还到 FramePool)
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("清队列");
        Step_ClearFrameQueues();
        LogStepTiming("清队列", stepQpc);

        // 3. 刷新解码器 (FlushAsync 取剩余帧并 Dispose)
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("刷解码器");
        await Step_FlushDecodersAsync();
        LogStepTiming("刷解码器", stepQpc);

        // 4. 释放解码器
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("释放解码器");
        Step_DisposeDecoders();
        LogStepTiming("释放解码器", stepQpc);

        // 5. 释放渲染器 (Detach + 释放 SwapChain + GPU Flush)
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("渲染器");
        Step_DisposeRenderer();
        LogStepTiming("渲染器", stepQpc);

        // 6. 释放音频输出
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("音频输出");
        Step_DisposeAudioOutput();
        LogStepTiming("音频输出", stepQpc);

        // 7. 释放帧对象池（所有帧已归还）
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("帧池");
        Step_DisposeFramePools();
        LogStepTiming("帧池", stepQpc);

        // 8. 清空 BufferManager
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("缓冲");
        Step_ClearBufferManager();
        LogStepTiming("缓冲", stepQpc);

        // 9. 关闭 Demuxer
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("Demuxer");
        await Step_CloseDemuxerAsync();
        LogStepTiming("Demuxer", stepQpc);

        // 10. 关闭 MediaStream
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("流");
        Step_CloseStream();
        LogStepTiming("流", stepQpc);

        // 11. 重置 Clock
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("时钟");
        Step_ResetClock();
        LogStepTiming("时钟", stepQpc);

        // 12. 关闭 Session
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("会话");
        await Step_CloseSessionAsync();
        LogStepTiming("会话", stepQpc);

        // 13. 归还高精度定时器（与 PlayAsync 配对，避免整机定时器泄漏）
        ReleaseHighPrecisionTimer();

        var totalMs = System.Diagnostics.Stopwatch.GetElapsedTime(disposeStart).TotalMilliseconds;
        var detail = string.Join(" ", stepTimings);
        _logger.LogInformation("[DISPOSE] 释放完成：总 {Total:F0}ms ｜ {Detail}", totalMs, detail);
        Console.Error.WriteLine($"[DISPOSE] 释放完成：总 {totalMs:F0}ms ｜ {detail}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 同步快速释放兜底——做自己的同步清理路径
        // 硬性规定：禁止调用 DisposeAsync().GetAwaiter().GetResult()（伪异步）
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
                // 补齐 BufferManager 读取线程——原先不等它直接释放 Demuxer（跨线程 UAF 隐患）。
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

            // 同步清空帧队列（归还到池）
            try { _frameQueue?.Clear(_videoFramePool); } catch { }
            try { _sampleQueue?.Clear(_audioFramePool); } catch { }

            // 同步释放原生资源（每步独立 try-catch）
            try { _videoDecoder?.Dispose(); } catch { }
            try { _audioDecoder?.Dispose(); } catch { }
            try { _subtitleDecoder?.Dispose(); } catch { }
            // 注意：共享单例渲染器（D3D11RendererFactory 缓存单例）不在此处置——
            // 其生命周期归工厂，SwapChain 由 UI Presenter.Detach 释放（缓存单例模式）。
            try { _audioOutput?.Dispose(); } catch { }
            try { _demuxer?.Close(); } catch { }
            try { _demuxer?.Dispose(); } catch { }
            try { _stream?.Close(); } catch { }
            try { _bufferManager?.Clear(); } catch { }

            // 归还高精度定时器（与 PlayAsync 配对，避免整机定时器泄漏）
            try { ReleaseHighPrecisionTimer(); } catch { }

            // 释放帧对象池
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
        // 委托给 PlaybackController 管理状态转换的合法性与原子性
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

    // 共享单例渲染器生命周期说明：
    // _videoRenderer 是 D3D11RendererFactory 的缓存单例，Core 视频管线与 UI 层 D3D11GpuPresenter
    // 通过同一工厂解析到同一 D3D11Renderer 实例。其生命周期由工厂（DI Singleton）持有，
    // SwapChain 与 HWND 的绑定由 UI Presenter.Detach 管理。
    // 因此 MediaPlayer 绝不能 Detach/Dispose 共享单例——否则会释放 UI 层正在使用（或待重开复用）的
    // 同一实例，导致重开后管线 Present 命中未附加/已释放的渲染器（D3D11 不显示）。
    // 此处刻意不处置渲染器：共享单例由工厂在容器释放时 Dispose，SwapChain 由 UI Presenter 释放。

}

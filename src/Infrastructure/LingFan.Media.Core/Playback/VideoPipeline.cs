using System;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

/// <summary>
/// 视频处理管线。从 BufferManager 包队列读取视频包 → Decoder 解码 → FrameQueue →
/// Synchronizer 同步 → Renderer 呈现。
/// </summary>
/// <remarks>
/// <para>所有方法均为同步 void（无 Task 返回，无 Resume）。</para>
/// <para>Start() 同时处理首次启动和恢复暂停。Stop() 只调 cts.Cancel（fire-and-forget）。</para>
/// <para>线程 join（5s 超时）在 MediaPlayer.DisposeAsync 第1步中处理，不在 Stop() 中。</para>
/// <para>丢帧和 Present 后的帧通过 ReturnFrame 归还到 FramePool（V2）或 Dispose（V1 兼容）。</para>
/// </remarks>
public sealed class VideoPipeline : IAsyncDisposable, IDisposable
{
    private readonly Channel<MediaPacket> _packetQueue;
    private readonly IVideoDecoder _decoder;
    private readonly IVideoRenderer _renderer;
    private readonly FrameQueue _frameQueue;
    private readonly Synchronizer _synchronizer;
    private readonly IMediaClock _clock;
    private readonly ILogger<VideoPipeline> _logger;
    private readonly IFramePool<VideoFrame>? _framePool;
    private readonly IReadOnlyList<Func<VideoFrame, VideoFrame?>>? _processors;
    private readonly Action? _processorReset;
    private readonly Action<VideoFrame>? _videoFrameSink;
    private volatile bool _pendingProcessorReset;

    private CancellationTokenSource _cts = new();
    private Task? _pipelineTask;
    private Task? _decodeTask;
    private volatile bool _decodeDone;
    // 自然完成标志：仅当管线因"流末耗尽"正常退出（非 Stop/Dispose 取消）时才置位，
    // 用于区分 Ended（自然结束）与取消退出，避免停止/释放时误触发 Completed 事件。
    private bool _completedNaturally;

    /// <summary>
    /// 前向解码缓冲目标深度（帧数）。解码生产者（<see cref="DecodeLoop"/>）将帧队列维持在
    /// 此深度，使呈现消费者（<see cref="PipelineLoop"/>）永不被解码延迟阻塞 —— 这是消除
    /// 「卡/回退」残余抖动的关键：解码与呈现彻底解耦到两条线程。
    /// </summary>
    /// <remarks>可通过 <c>LINGFAN_VIDEO_AHEAD</c> 覆盖（1~30，默认 6 ≈ 250ms@24fps 余量）。</remarks>
    private static readonly int TargetDepth = ParseTargetDepth();
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    private volatile bool _pauseAcknowledged;
    private TaskCompletionSource<bool>? _pauseAckTcs;
    private long _droppedFrames;

    /// <summary>
    /// 解码锁：确保 DecodeAsync 与 Reset 不会并发执行。
    /// PipelineLoop 在解码+入队期间持有锁，Flush/FlushAsync 在 Clear+Reset 前获取锁。
    /// 即使暂停确认超时（管线线程卡在长解码中），锁也能确保安全。
    /// </summary>
    private readonly SemaphoreSlim _decodeLock = new(1, 1);
    private volatile bool _pendingDecoderReset;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="VideoPipeline"/> 的新实例。
    /// </summary>
    /// <param name="packetQueue">视频数据包队列（来自 BufferManager）。</param>
    /// <param name="decoder">视频解码器。</param>
    /// <param name="renderer">视频渲染器。</param>
    /// <param name="frameQueue">视频帧队列。</param>
    /// <param name="synchronizer">同步器。</param>
    /// <param name="clock">媒体时钟。</param>
    /// <param name="logger">日志器。</param>
    /// <param name="framePool">帧对象池（V2，可为 null = 无池化回退 V1 行为）。</param>
    /// <param name="processors">视频后处理链（V2-06 C5，可为 null = 透传）。
    /// 中立 BCL 委托，由 Video 模块把 <c>IVideoProcessor</c> 链转换而来，Core 不依赖 Video 模块。</param>
    public VideoPipeline(
        Channel<MediaPacket> packetQueue,
        IVideoDecoder decoder,
        IVideoRenderer renderer,
        FrameQueue frameQueue,
        Synchronizer synchronizer,
        IMediaClock clock,
        ILogger<VideoPipeline> logger,
        IFramePool<VideoFrame>? framePool = null,
        IReadOnlyList<Func<VideoFrame, VideoFrame?>>? processors = null,
        Action? processorReset = null,
        Action<VideoFrame>? videoFrameSink = null)
    {
        _packetQueue = packetQueue;
        _decoder = decoder;
        _renderer = renderer;
        _frameQueue = frameQueue;
        _synchronizer = synchronizer;
        _clock = clock;
        _logger = logger;
        _framePool = framePool;
        _processors = processors;
        _processorReset = processorReset;
        _videoFrameSink = videoFrameSink;
    }

    /// <summary>
    /// 归还帧到池（若池可用）或 Dispose（V1 兼容）。
    /// </summary>
    private void ReturnFrame(VideoFrame frame)
    {
        if (_framePool != null)
            _framePool.Return(frame);
        else
            frame.Dispose();
    }

    /// <summary>管线是否运行。</summary>
    public bool IsRunning => _isRunning;

    /// <summary>当前帧队列长度。</summary>
    public int FrameQueueSize => _frameQueue.Count;

    /// <summary>累计丢帧数。</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>内部管线任务（供 DisposeAsync join）。</summary>
    internal Task? PipelineTask => _pipelineTask;

    /// <summary>
    /// 自然播放完成事件。当管线因"流末耗尽"（包源关闭、所有帧已呈现）正常退出时触发；
    /// 由 <c>Stop</c>/<c>Dispose</c> 取消退出时不触发。
    /// <see cref="MediaPipelineHost"/> 聚合 video/audio 两条管线的 <see cref="Completed"/> 为
    /// <c>PlaybackCompleted</c>，最终驱动播放器转 <see cref="MediaState.Ended"/>。
    /// </summary>
    public event EventHandler? Completed;

    /// <summary>
    /// 启动或恢复管线。
    /// 首次播放时启动管线线程，恢复播放时解除暂停阻塞。
    /// </summary>
    public void Start()
    {
        if (_isRunning)
        {
            _isPaused = false;
            return;
        }

        _isRunning = true;
        _isPaused = false;
        _decodeDone = false;

        // 重新创建 CTS（如果旧的已取消）
        if (_cts.IsCancellationRequested)
        {
            _cts = new CancellationTokenSource();
        }

        // 双线程解耦：解码生产者 + 呈现消费者。两者经 FrameQueue 通信，解码延迟不阻塞呈现节拍。
        // 呈现消费者用 LongRunning 专用实时线程：避免 async 续体被线程池调度延迟（±50ms 抖动脉冲），
        // 且线程全程保持 Highest 优先级（不被 OS 抢占）。解码生产者保持 Task.Run（缓冲生产者，延迟不影响呈现节拍）。
        _pipelineTask = Task.Factory.StartNew(PipelineLoop, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _decodeTask = Task.Run(DecodeLoop);
    }

    /// <summary>
    /// 暂停管线（阻塞读取）。
    /// </summary>
    public void Pause()
    {
        _isPaused = true;
    }

    /// <summary>
    /// 停止管线并清空队列。
    /// 只调 cts.Cancel（fire-and-forget，不等待线程退出）。
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _isPaused = false;
        _cts.Cancel();
    }

    /// <summary>
    /// 清空队列和解码器缓冲（Seek 后调用）。同步版本，用于无法 await 的场景。
    /// V2 修复（L2）：先暂停管线线程，等待确认或获取解码锁后清空和重置，最后恢复运行。
    /// </summary>
    /// <remarks>
    /// <para>两阶段安全保证：</para>
    /// <list type="number">
    /// <item>暂停确认（50ms 超时）：快速路径，管线空闲时立即确认</item>
    /// <item>解码锁（2s 超时）：慢速路径，管线卡在长解码中时等待解码完成</item>
    /// </list>
    /// <para>即使暂停确认超时，解码锁也能确保 Reset 不与 DecodeAsync 并发。</para>
    /// <para>优先使用异步版本 <see cref="FlushAsync"/>（无 Thread.Sleep 阻塞）。</para>
    /// </remarks>
    public void Flush()
    {
        var shouldResume = _isRunning && !_isPaused;
        if (_isRunning)
        {
            _pauseAcknowledged = false;
            _isPaused = true;

            // 阶段1: 等待暂停确认（快速路径，50ms 超时）
            for (var i = 0; i < 50 && !_pauseAcknowledged; i++)
            {
                Thread.Sleep(1);
            }

            if (!_pauseAcknowledged)
            {
                _logger.LogWarning("视频管线暂停确认超时（50ms），等待解码锁确保安全");
            }

            // 阶段2: 获取解码锁（慢速路径，确保无 DecodeAsync 在执行）
            if (_decodeLock.Wait(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    _frameQueue.Clear(_framePool);
                    _decoder.Reset();
                    _processorReset?.Invoke(); // V2-06 二次审计修复：重置有状态处理器（释放 _held 等）
                }
                finally
                {
                    _decodeLock.Release();
                }
            }
            else
            {
                _logger.LogError("视频管线解码锁获取超时（2s），标记延迟 Reset 防止竞态崩溃");
                _frameQueue.Clear(_framePool); // Channel 线程安全，仍然清空
                _pendingDecoderReset = true;   // 锁超时未做 Reset，待管线线程下次进入锁时补做，确保解码器状态必然复位
                _pendingProcessorReset = true;   // 同上：有状态处理器延迟重置，避免与 Process 并发
            }
        }
        else
        {
            // 管线未运行，无需锁
            _frameQueue.Clear(_framePool);
            _decoder.Reset();
        }

        if (shouldResume)
        {
            _isPaused = false;
        }
    }

    /// <summary>
    /// 清空队列和解码器缓冲（Seek 后调用）。异步版本，优先使用。
    /// V2 修复（L2）：先暂停管线线程，等待确认或获取解码锁后清空和重置，最后恢复运行。
    /// </summary>
    /// <remarks>
    /// <para>两阶段安全保证：</para>
    /// <list type="number">
    /// <item>暂停确认（50ms 超时）：快速路径，使用 TaskCompletionSource 信号通知</item>
    /// <item>解码锁（2s 超时）：慢速路径，管线卡在长解码中时等待解码完成</item>
    /// </list>
    /// <para>即使暂停确认超时，解码锁也能确保 Reset 不与 DecodeAsync 并发。</para>
    /// <para>RunContinuationsAsynchronously 避免续体在管线线程执行。</para>
    /// </remarks>
    public async Task FlushAsync()
    {
        var shouldResume = _isRunning && !_isPaused;
        if (_isRunning)
        {
            _pauseAckTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pauseAcknowledged = false;
            _isPaused = true;

            // 阶段1: 等待暂停确认（快速路径，50ms 超时）
            try
            {
                await _pauseAckTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(50));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("视频管线暂停确认超时（50ms），等待解码锁确保安全");
            }

            // 阶段2: 获取解码锁（慢速路径，确保无 DecodeAsync 在执行）
            if (await _decodeLock.WaitAsync(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    _frameQueue.Clear(_framePool);
                    _decoder.Reset();
                    _processorReset?.Invoke(); // V2-06 二次审计修复：重置有状态处理器（释放 _held 等）
                }
                finally
                {
                    _decodeLock.Release();
                }
            }
            else
            {
                _logger.LogError("视频管线解码锁获取超时（2s），标记延迟 Reset 防止竞态崩溃");
                _frameQueue.Clear(_framePool); // Channel 线程安全，仍然清空
                _pendingDecoderReset = true;   // 锁超时未做 Reset，待管线线程下次进入锁时补做，确保解码器状态必然复位
                _pendingProcessorReset = true;   // 同上：有状态处理器延迟重置，避免与 Process 并发
            }
        }
        else
        {
            // 管线未运行，无需锁
            _frameQueue.Clear(_framePool);
            _decoder.Reset();
        }

        if (shouldResume)
        {
            _isPaused = false;
        }
    }

    /// <summary>
    /// 呈现消费者循环（单读线程，<see cref="FrameQueue"/> 以 <c>SingleReader=true</c> 创建，
    /// 本循环是唯一读者）。采用 **Peek-then-Dequeue**：队头帧不出队就先判定同步动作，
    /// 仅当判 <see cref="SyncAction.Present"/> 才真正取走呈现；判 <see cref="SyncAction.Wait"/>
    /// 时帧**留队头**等待时钟追近（<c>continue</c> 后下一轮重新判定），绝不把超前帧塞回队尾 ——
    /// 这消除了 R-1 缺陷（超前帧塞队尾→取到更超前帧→队列轮转、期间零呈现→「卡完突然向前」）。
    /// 解码延迟已被 <see cref="DecodeLoop"/> 完全隔离，呈现节拍不受解码抖动污染。
    /// </summary>
    private void PipelineLoop()
    {
        try
        {
#if WINDOWS
            // 视频呈现是实时循环：提升专用线程优先级到 Highest，配合 Windows 多媒体调度显著降低
            // OS/GC 抢占造成的帧间墙钟抖动（残余抖动根因）。LongRunning 专用线程上该优先级全程有效。
            try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest; }
            catch { }
#endif
            while (!_cts.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    _pauseAcknowledged = true;
                    _pauseAckTcs?.TrySetResult(true);
                    Thread.Sleep(10);
                    continue;
                }

                // 队头帧不出队即判定（Peek 在 SingleReader 下安全）。
                var head = _frameQueue.Peek();
                if (head is null)
                {
                    // 队列空：若生产者已结束（流尾）则收尾；否则短暂让出，等待生产者补帧。
                    if (_frameQueue.IsCompleted || _decodeDone)
                    {
                        // 流末自然耗尽（非取消）：标记后退出，finally 中触发 Completed 事件。
                        _completedNaturally = true;
                        break;
                    }
                    Thread.Sleep(1);
                    continue;
                }

                // 拷贝 PTS 到局部变量：Wait 期间若发生 Flush，队头帧可能经 FrameQueue.Clear 归还池
                // 后被复用并改写 Timestamp；使用进入本循环时拷贝的值，避免读到回收后的垃圾时间戳
                // （同时消除 R-2：旧 Wait 分支在帧所有权已转移后越界读 frame.Timestamp 的隐患）。
                var headTimestamp = head.Timestamp;
                var action = _synchronizer.CheckVideoFrame(head);

                if (action == SyncAction.Wait)
                {
                    // 帧留队头等待，绝不轮转。下一轮重新 Peek 同一帧再判定。
                    if (PacingDiagnostics.Enabled)
                    {
                        PacingDiagnostics.Present.OnWait();
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        WaitUntilDue(headTimestamp, _cts.Token);
                        PacingDiagnostics.Present.OnSleepMeasured(
                            System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                        LogPacingIfDue();
                    }
                    else
                    {
                        WaitUntilDue(headTimestamp, _cts.Token);
                    }
                    continue;
                }

                if (action == SyncAction.Drop)
                {
                    // 队首帧已严重落后：取走并归还（不可留队头，否则永远卡在 Drop 分支）。
                    if (_frameQueue.TryDequeue(out var dropped) && dropped != null)
                        ReturnFrame(dropped);
                    Interlocked.Increment(ref _droppedFrames);
                    if (PacingDiagnostics.Enabled)
                    {
                        PacingDiagnostics.Present.OnDrop();
                        LogPacingIfDue();
                    }
                    continue;
                }

                // Present：队头帧判定可呈现，正式取走并呈现。
                if (_frameQueue.TryDequeue(out var frame) && frame != null)
                    ProcessFrame(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出（Stop 调用了 cts.Cancel）
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "视频管线异常");
        }
        finally
        {
            // 仅当流末自然耗尽时触发 Completed（取消退出不触发，避免 Stop/Dispose 误报 Ended）。
            if (_completedNaturally)
                Completed?.Invoke(this, EventArgs.Empty);
            _isRunning = false;
        }
    }

    /// <summary>
    /// 解码生产者循环（独立于呈现消费者）。持续从包队列读取、解码、入队，将帧队列维持在
    /// <see cref="TargetDepth"/> 帧的前向缓冲；背压到位即让出，使呈现侧永远有帧可取，
    /// 从而把「解码延迟」与「呈现节拍」彻底解耦。
    /// </summary>
    private async Task DecodeLoop()
    {
        try
        {
#if WINDOWS
            // 解码生产者同样提升优先级，避免被 OS 调度抢占导致前向缓冲断供、呈现侧饿死。
            try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest; }
            catch { }
#endif
            while (!_cts.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    await Task.Delay(10, _cts.Token);
                    continue;
                }

                // 背压：维持固定前向缓冲，避免无限超前占用内存；同时确保呈现侧永不被饿死。
                if (_frameQueue.Count >= TargetDepth)
                {
                    await Task.Delay(1, _cts.Token);
                    continue;
                }

                MediaPacket? packet;
                try
                {
                    packet = await _packetQueue.Reader.ReadAsync(_cts.Token);
                }
                catch (ChannelClosedException)
                {
                    // 包源结束 ⇒ 通知消费者收尾
                    _frameQueue.Complete();
                    break;
                }

                // 解码 + 后处理（加锁防止与 Flush/Reset 竞态）
                await _decodeLock.WaitAsync(_cts.Token);
                VideoFrame? decodedFrame = null;
                try
                {
                    // 隐患B修复：解码锁获取超时期间 Flush 可能跳过 Reset，此处补做
                    if (_pendingDecoderReset)
                    {
                        _decoder.Reset();
                        _pendingDecoderReset = false;
                    }

                    // V2-06 二次审计修复延伸：解码锁超时期间 Flush 可能跳过处理器重置，此处补做
                    if (_pendingProcessorReset)
                    {
                        _processorReset?.Invoke();
                        _pendingProcessorReset = false;
                    }

                    // 双重检查：获取锁后确认未暂停（防止在等待锁期间被 Flush 暂停）
                    if (_isPaused)
                    {
                        packet.Dispose();
                        continue; // finally 会释放锁
                    }

                    try
                    {
                        decodedFrame = await _decoder.DecodeAsync(packet);
                    }
                    finally
                    {
                        packet.Dispose();
                    }

                    if (decodedFrame != null && _processors != null)
                    {
                        // V2-06 C5: 经过视频后处理链（所有权转移）
                        foreach (var processor in _processors)
                        {
                            decodedFrame = processor(decodedFrame);
                            if (decodedFrame == null)
                                break; // 处理器丢弃帧（已 Dispose 输入帧）
                        }
                    }
                }
                finally
                {
                    _decodeLock.Release();
                }

                // 入队在解码锁之外：避免持锁阻塞于满队列（与 Flush 死锁）；
                // 满时 BoundedChannelFullMode.Wait 自动背压。
                if (decodedFrame != null)
                {
                    try
                    {
                        await _frameQueue.EnqueueAsync(decodedFrame, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        ReturnFrame(decodedFrame);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出（Stop 调用了 cts.Cancel）
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "视频解码生产者异常");
        }
        finally
        {
            _decodeDone = true;
        }
    }

    private void ProcessFrame(VideoFrame frame)
    {
        // 仅处理「呈现」分支。同步决策（Wait / Drop）已上移到 PipelineLoop 的
        // Peek-then-Dequeue 逻辑：Wait 时帧留队头等待时钟追近（消除 R-1 轮转），
        // Drop 时取走并归还过期帧。此处被调用即表示队头帧已判定为 Present。
        if (PacingDiagnostics.Enabled)
        {
            // 呈现误差：相对"最早可呈现时刻"(frame.Timestamp - SyncThreshold)的偏移。
            // Peek 方案下理想值 ≈ 0~2ms（WaitUntilDue 自旋收口精度）；若该值显著 >0
            // 说明帧在队头等到了过期后才被取走呈现（仍被某处阻塞）。
            var thr = _synchronizer.SyncThreshold;
            var masterNow = _synchronizer.GetCurrentMasterTime();
            double errMs = (masterNow - (frame.Timestamp - thr)).TotalMilliseconds;
            string? report = PacingDiagnostics.Present.OnPresent(frame.Timestamp, _frameQueue.Count, errMs);
            if (report != null)
            {
                _logger.LogInformation("[PACING] {Report}", report);
                _logger.LogInformation("[CLOCK] {Snapshot}", PacingDiagnostics.Clock.Snapshot());
            }
        }

        try
        {
            // V2-12: _videoFrameSink 在生产路径中恒为非空 lambda（MediaPlayer 注入的转发+路由器），
            // lambda 内部按"UI 是否订阅 VideoFrameAvailable"路由——订阅（Skia 软渲染）→ 投递 sink；
            // 未订阅（D3D11 原生 GPU）→ 直接 Present 到已 Attach 的共享 SwapChain。二者互斥。
            // 此处 else 仅为 null-sink 调用方（测试/无 UI）兜底：直接 Present 到渲染器。
            // 注意：路由决策在 lambda 内完成，不可在此直接判 _videoFrameSink == null 来决定 D3D11——
            // 那样会让 D3D11 模式（无订阅方，但 lambda 非空）永不调用渲染器，导致视频不显示。
            if (_videoFrameSink != null)
                _videoFrameSink(frame);
            else
                _renderer.Present(frame);
        }
        finally { ReturnFrame(frame); } // V2: Present 后归还到池
    }

    /// <summary>
    /// 同步精确等待直到视频帧到达呈现时刻（<c>frameTimestamp - SyncThreshold</c>）。
    /// 仅在 LongRunning 专用实时线程上调用（热路径零 await，无续体调度延迟）。
    /// </summary>
    /// <remarks>
    /// <para>主体：<see cref="Thread.Sleep"/> 睡到「目标时刻前 ~1.5ms」，每轮只读一次平滑主时钟
    /// （A 修复的 QPC 插值，跨线程 COM 仅 2~3 次/帧）；尾部用<b>本地 QPC 自旋</b>精确收口，
    /// 彻底去掉原 WaitUntilDueAsync 每帧上千次跨线程 <c>IAudioClock::GetPosition</c> 的 CPU/缓存行争用。
    /// 专用线程上 Thread.Sleep 不阻塞任何线程池工作，且 Highest 优先级下不被 OS 抢占。</para>
    /// <para>与 <see cref="Synchronizer.CheckVideoFrame"/> 使用同一主时钟源与同一阈值，判据一致。</para>
    /// </remarks>
    private void WaitUntilDue(TimeSpan frameTimestamp, CancellationToken ct = default)
    {
        const double tailMs = 1.5;
        var threshold = _synchronizer.SyncThreshold;
        long targetQpc = 0;

        // 主体：睡到目标前 ~1.5ms，每轮一次平滑时钟读取。Stop() 取消时立即返回，
        // 避免专用实时线程在退出/暂停时仍按帧时睡眠（round-21 引入的回归：原 Thread.Sleep 不感知取消）。
        while (true)
        {
            if (ct.IsCancellationRequested) return;
            var remaining = (frameTimestamp - threshold - _synchronizer.GetCurrentMasterTime()).TotalMilliseconds;
            if (remaining <= tailMs)
            {
                // 音频时钟≈实时，故用本地高精度时钟反推目标 QPC，尾部纯自旋收口（零 COM 调用）。
                targetQpc = System.Diagnostics.Stopwatch.GetTimestamp()
                            + (long)(remaining * 10_000); // 100ns ticks
                break;
            }
            Thread.Sleep((int)Math.Ceiling(remaining - tailMs));
        }

        // 尾部：仅用本地 QPC 自旋精确收口（≤5ms 安全阀，防异常时钟停滞死自旋），无跨线程 COM。
        var sw = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetTimestamp() < targetQpc)
        {
            if (ct.IsCancellationRequested) return;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(sw).TotalMilliseconds > 5)
                break;
            System.Threading.Thread.SpinWait(10);
        }
    }

    /// <summary>
    /// 时间兜底输出节奏报告（仅诊断路径调用）。
    /// </summary>
    /// <remarks>
    /// 画面冻结时所有帧都走 Wait / Drop，<c>OnPresent</c> 不被调用 ⇒ 按次数触发的报告永不出现。
    /// 恰恰是这种时刻的现场最关键，故 Wait / Drop 分支轮询本方法强制出报告。
    /// </remarks>
    private void LogPacingIfDue()
    {
        string? report = PacingDiagnostics.Present.PollReport();
        if (report == null)
            return;

        _logger.LogInformation("[PACING] {Report}", report);
        _logger.LogInformation("[CLOCK] {Snapshot}", PacingDiagnostics.Clock.Snapshot());
    }

    /// <summary>
    /// 释放管线资源（解码锁和 CTS）。
    /// </summary>
    /// <remarks>
    /// <para>必须在管线线程退出后调用。DisposeAsync 路径在 Step_StopPipelinesAsync join 后调用。</para>
    /// <para>同步 Dispose 路径在 Stop() 后调用——若线程仍在运行，SemaphoreSlim.Dispose 可能
    /// 与正在进行的 WaitAsync/Release 并发，但不会导致未处理异常（管线 catch 已兜底）。</para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 隐患A修复：释放信号量前确保管线线程已退出，避免 SemaphoreSlim.Dispose 与并发 WaitAsync/Release 的未定义行为
        EnsureThreadStopped();

        _decodeLock.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// 隐患A修复：释放解码锁前停止并 join 管线线程。
    /// 仅当当前不在管线线程自身上调用时才等待，避免自死锁。
    /// 正常流程（MediaPlayer 已先 join）下任务已完成，Wait 立即返回，无阻塞。
    /// </summary>
    private void EnsureThreadStopped()
    {
        if (_pipelineTask is null && _decodeTask is null)
            return;
        // 防御：若在任一管线线程自身上调用则不等待（理论上不会发生）
        if (_pipelineTask != null && Task.CurrentId == _pipelineTask.Id) return;
        if (_decodeTask != null && Task.CurrentId == _decodeTask.Id) return;

        _isRunning = false;
        _isPaused = false;
        _cts.Cancel();

        var tasks = new List<Task>(2);
        if (_pipelineTask != null) tasks.Add(_pipelineTask);
        if (_decodeTask != null) tasks.Add(_decodeTask);
        try
        {
            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "视频管线线程 join 失败，仍继续释放资源");
        }
    }

    /// <summary>
    /// 异步释放管线资源。优先使用（MediaPlayer.DisposeAsync 在线程 join 后调用）。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 解析前向解码缓冲深度（<c>LINGFAN_VIDEO_AHEAD</c>），默认 6 帧。
    /// </summary>
    private static int ParseTargetDepth()
    {
        var raw = Environment.GetEnvironmentVariable("LINGFAN_VIDEO_AHEAD");
        return int.TryParse(raw, out var v) && v is >= 1 and <= 30 ? v : 6;
    }
}

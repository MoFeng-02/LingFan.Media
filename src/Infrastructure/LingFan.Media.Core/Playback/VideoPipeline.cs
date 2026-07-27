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

        // 重新创建 CTS（如果旧的已取消）
        if (_cts.IsCancellationRequested)
        {
            _cts = new CancellationTokenSource();
        }

        _pipelineTask = Task.Run(PipelineLoop);
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

    private async Task PipelineLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    _pauseAcknowledged = true;
                    _pauseAckTcs?.TrySetResult(true);
                    await Task.Delay(10, _cts.Token);
                    continue;
                }

                // 1. 从帧队列非阻塞出队
                if (_frameQueue.TryDequeue(out var frame) && frame != null)
                {
                    ProcessFrame(frame);
                    continue;
                }

                // 2. 队列空，从包队列读取并解码
                MediaPacket? packet;
                try
                {
                    packet = await _packetQueue.Reader.ReadAsync(_cts.Token);
                }
                catch (ChannelClosedException)
                {
                    // 流结束
                    _frameQueue.Complete();
                    break;
                }

                // 3. 解码 + 入队（加锁防止与 Flush/Reset 竞态）
                await _decodeLock.WaitAsync(_cts.Token);
                try
                {
                    // 隐患B修复：解码锁获取超时期间 Flush 可能跳过 Reset，此处补做，确保解码器内部状态必然复位
                    if (_pendingDecoderReset)
                    {
                        _decoder.Reset();
                        _pendingDecoderReset = false;
                    }

                    // V2-06 二次审计修复延伸：解码锁超时期间 Flush 可能跳过处理器重置，
                    // 此处补做，确保有状态处理器（如 FrameRateConverter 的 _held）必然复位
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

                    VideoFrame? decodedFrame;
                    try
                    {
                        decodedFrame = await _decoder.DecodeAsync(packet);
                    }
                    finally
                    {
                        packet.Dispose();
                    }

                    if (decodedFrame != null)
                    {
                        // V2-06 C5: 经过视频后处理链（所有权转移：处理器 Dispose 输入帧并返回新帧）
                        if (_processors != null)
                        {
                            foreach (var processor in _processors)
                            {
                                decodedFrame = processor(decodedFrame);
                                if (decodedFrame == null)
                                    break; // 处理器丢弃帧（已 Dispose 输入帧）
                            }
                        }

                        if (decodedFrame != null && !_frameQueue.TryEnqueue(decodedFrame))
                            ReturnFrame(decodedFrame); // V2: 队列满，归还帧到池
                    }
                }
                finally
                {
                    _decodeLock.Release();
                }
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
            _isRunning = false;
        }
    }

    private void ProcessFrame(VideoFrame frame)
    {
        var syncAction = _synchronizer.CheckVideoFrame(frame);

        switch (syncAction)
        {
            case SyncAction.Present:
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
                break;

            case SyncAction.Wait:
                // 视频超前，重新入队等待（暂停期间不重新入队，防 Flush 清空后残留旧帧）
                if (_isPaused || !_frameQueue.TryEnqueue(frame))
                    ReturnFrame(frame);
                Thread.Sleep(1); // 短暂等待
                break;

            case SyncAction.Drop:
                ReturnFrame(frame); // V2: 丢帧归还到池
                Interlocked.Increment(ref _droppedFrames);
                break;
        }
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
        if (_pipelineTask is null)
            return;
        if (Task.CurrentId == _pipelineTask.Id)
            return; // 防御：若在管线线程自身上调用则不等待（理论上不会发生）

        _isRunning = false;
        _isPaused = false;
        _cts.Cancel();
        try
        {
            _pipelineTask.Wait(TimeSpan.FromSeconds(5));
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
}

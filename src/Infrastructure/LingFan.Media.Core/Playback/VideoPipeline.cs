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
/// <para>丢帧必须 Dispose（释放 GPU 资源）。Present 后 Dispose 帧（同步消费约定）。</para>
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
    public VideoPipeline(
        Channel<MediaPacket> packetQueue,
        IVideoDecoder decoder,
        IVideoRenderer renderer,
        FrameQueue frameQueue,
        Synchronizer synchronizer,
        IMediaClock clock,
        ILogger<VideoPipeline> logger,
        IFramePool<VideoFrame>? framePool = null)
    {
        _packetQueue = packetQueue;
        _decoder = decoder;
        _renderer = renderer;
        _frameQueue = frameQueue;
        _synchronizer = synchronizer;
        _clock = clock;
        _logger = logger;
        _framePool = framePool;
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
                }
                finally
                {
                    _decodeLock.Release();
                }
            }
            else
            {
                _logger.LogError("视频管线解码锁获取超时（2s），跳过 Reset 防止竞态崩溃");
                _frameQueue.Clear(_framePool); // Channel 线程安全，仍然清空
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
                }
                finally
                {
                    _decodeLock.Release();
                }
            }
            else
            {
                _logger.LogError("视频管线解码锁获取超时（2s），跳过 Reset 防止竞态崩溃");
                _frameQueue.Clear(_framePool); // Channel 线程安全，仍然清空
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
                        if (!_frameQueue.TryEnqueue(decodedFrame))
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
                try { _renderer.Present(frame); }
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

        _decodeLock.Dispose();
        _cts.Dispose();
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

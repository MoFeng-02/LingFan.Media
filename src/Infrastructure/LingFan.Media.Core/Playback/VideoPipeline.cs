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
public sealed class VideoPipeline
{
    private readonly Channel<MediaPacket> _packetQueue;
    private readonly IVideoDecoder _decoder;
    private readonly IVideoRenderer _renderer;
    private readonly FrameQueue _frameQueue;
    private readonly Synchronizer _synchronizer;
    private readonly IMediaClock _clock;
    private readonly ILogger<VideoPipeline> _logger;

    private CancellationTokenSource _cts = new();
    private Task? _pipelineTask;
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    private long _droppedFrames;

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
    public VideoPipeline(
        Channel<MediaPacket> packetQueue,
        IVideoDecoder decoder,
        IVideoRenderer renderer,
        FrameQueue frameQueue,
        Synchronizer synchronizer,
        IMediaClock clock,
        ILogger<VideoPipeline> logger)
    {
        _packetQueue = packetQueue;
        _decoder = decoder;
        _renderer = renderer;
        _frameQueue = frameQueue;
        _synchronizer = synchronizer;
        _clock = clock;
        _logger = logger;
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
    /// 清空队列和解码器缓冲（Seek 后调用）。
    /// </summary>
    public void Flush()
    {
        _frameQueue.Clear();
        _decoder.Reset();
    }

    private async Task PipelineLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_isPaused)
                {
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

                // 3. 解码（无 CT，热路径）
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
                        decodedFrame.Dispose(); // 队列满，丢弃帧防泄漏
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
                finally { frame.Dispose(); } // Present 异常也必须释放帧
                break;

            case SyncAction.Wait:
                // 视频超前，重新入队等待
                if (!_frameQueue.TryEnqueue(frame))
                    frame.Dispose(); // 队列满，丢弃帧防泄漏
                Thread.Sleep(1); // 短暂等待
                break;

            case SyncAction.Drop:
                frame.Dispose();
                Interlocked.Increment(ref _droppedFrames);
                break;
        }
    }
}

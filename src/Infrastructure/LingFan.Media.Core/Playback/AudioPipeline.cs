using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

/// <summary>
/// 音频处理管线。从 BufferManager 包队列读取音频包 → Decoder 解码 → SampleQueue →
/// AudioOutput 播放。音频时钟作为主时钟。
/// </summary>
/// <remarks>
/// <para>所有方法均为同步 void（无 Task 返回，无 Resume）。</para>
/// <para>关键区别于 VideoPipeline：音频不丢帧（丢帧会导致声音断裂）。</para>
/// <para>每次音频帧提交后通过 Synchronizer.OnAudioFrameSubmitted 更新主时钟。</para>
/// </remarks>
public sealed class AudioPipeline
{
    private readonly Channel<MediaPacket> _packetQueue;
    private readonly IAudioDecoder _decoder;
    private readonly IAudioOutput _output;
    private readonly SampleQueue _sampleQueue;
    private readonly Synchronizer _synchronizer;
    private readonly IMediaClock _clock;
    private readonly ILogger<AudioPipeline> _logger;

    private CancellationTokenSource _cts = new();
    private Task? _pipelineTask;
    private volatile bool _isRunning;
    private volatile bool _isPaused;

    /// <summary>
    /// 初始化 <see cref="AudioPipeline"/> 的新实例。
    /// </summary>
    /// <param name="packetQueue">音频数据包队列（来自 BufferManager）。</param>
    /// <param name="decoder">音频解码器。</param>
    /// <param name="output">音频输出。</param>
    /// <param name="sampleQueue">音频帧队列。</param>
    /// <param name="synchronizer">同步器。</param>
    /// <param name="clock">媒体时钟。</param>
    /// <param name="logger">日志器。</param>
    public AudioPipeline(
        Channel<MediaPacket> packetQueue,
        IAudioDecoder decoder,
        IAudioOutput output,
        SampleQueue sampleQueue,
        Synchronizer synchronizer,
        IMediaClock clock,
        ILogger<AudioPipeline> logger)
    {
        _packetQueue = packetQueue;
        _decoder = decoder;
        _output = output;
        _sampleQueue = sampleQueue;
        _synchronizer = synchronizer;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>管线是否运行。</summary>
    public bool IsRunning => _isRunning;

    /// <summary>当前采样队列长度。</summary>
    public int SampleQueueSize => _sampleQueue.Count;

    /// <summary>输出延迟。</summary>
    public TimeSpan OutputLatency => _output.Latency;

    /// <summary>内部管线任务（供 DisposeAsync join）。</summary>
    internal Task? PipelineTask => _pipelineTask;

    /// <summary>
    /// 启动或恢复管线。
    /// Start() 同时处理首次启动和恢复（调用 audioOutput.Resume() 恢复输出）。
    /// </summary>
    public void Start()
    {
        if (_isRunning)
        {
            _isPaused = false;
            _output.Resume();
            return;
        }

        _isRunning = true;
        _isPaused = false;

        if (_cts.IsCancellationRequested)
        {
            _cts = new CancellationTokenSource();
        }

        _pipelineTask = Task.Run(PipelineLoop);
    }

    /// <summary>
    /// 暂停管线和音频输出。
    /// </summary>
    public void Pause()
    {
        _isPaused = true;
        _output.Pause();
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
        _sampleQueue.Clear();
        _decoder.Reset();
        _output.Flush();
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

                // 1. 从采样队列非阻塞出队
                if (_sampleQueue.TryDequeue(out var frame) && frame != null)
                {
                    SubmitFrame(frame);
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
                    _sampleQueue.Complete();
                    break;
                }

                // 3. 解码（无 CT，热路径）
                AudioFrame? decodedFrame;
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
                    if (!_sampleQueue.TryEnqueue(decodedFrame))
                        decodedFrame.Dispose(); // 队列满，丢弃帧防泄漏
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "音频管线异常");
        }
        finally
        {
            _isRunning = false;
        }
    }

    private void SubmitFrame(AudioFrame frame)
    {
        try
        {
            // 更新主时钟
            _synchronizer.OnAudioFrameSubmitted(frame);

            // 提交给输出（所有权转移给 Output，Output 内部处理后 Dispose）
            _output.Submit(frame);
        }
        catch (Exception ex)
        {
            // Submit 或 OnAudioFrameSubmitted 异常时帧未转移所有权，必须 Dispose 防泄漏
            frame.Dispose();
            _logger.LogError(ex, "音频帧提交失败");
        }
    }
}

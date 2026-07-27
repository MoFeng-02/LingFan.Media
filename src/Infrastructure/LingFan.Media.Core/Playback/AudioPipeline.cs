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
public sealed class AudioPipeline : IAsyncDisposable, IDisposable
{
    private readonly Channel<MediaPacket> _packetQueue;
    private readonly IAudioDecoder _decoder;
    private readonly IAudioOutput _output;
    private readonly SampleQueue _sampleQueue;
    private readonly Synchronizer _synchronizer;
    private readonly IMediaClock _clock;
    private readonly ILogger<AudioPipeline> _logger;
    private readonly IFramePool<AudioFrame>? _framePool;
    private readonly IReadOnlyList<Func<AudioFrame, AudioFrame>>? _transforms;
    private readonly Action? _effectReset;
    private readonly Action<AudioFrame>? _audioDataSink;
    private volatile bool _pendingEffectReset;

    private CancellationTokenSource _cts = new();
    private Task? _pipelineTask;
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    private volatile bool _pauseAcknowledged;
    private TaskCompletionSource<bool>? _pauseAckTcs;

    /// <summary>
    /// 解码锁：确保 DecodeAsync 与 Reset 不会并发执行。
    /// PipelineLoop 在解码+入队期间持有锁，Flush/FlushAsync 在 Clear+Reset 前获取锁。
    /// 即使暂停确认超时（管线线程卡在长解码中），锁也能确保安全。
    /// </summary>
    private readonly SemaphoreSlim _decodeLock = new(1, 1);
    private volatile bool _pendingDecoderReset;
    private bool _disposed;

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
    /// <param name="framePool">帧对象池（V2，可为 null = 无池化回退 V1 行为）。</param>
    /// <param name="transforms">音频变换链（V2-06 C4/C6，可为 null = 透传）。
    /// 中立 BCL 委托，由 Audio 模块把 <c>VolumeControl</c>/<c>IAudioEffect</c>/<c>AudioMixer</c> 转换而来，Core 不依赖 Audio 模块。</param>
    /// <param name="effectReset">音频效果状态重置委托（V2-08.1，可为 null = 无）。
    /// 中立 BCL 委托（<see cref="Action"/>），由 Audio 模块把各 <c>IAudioEffect.Reset</c> 合并而来，Core 不依赖 Audio 模块。
    /// 在 Seek/Flush 的解码锁内调用，清除有状态效果（均衡器 biquad / 混响延迟线 / 压缩器包络）的跨位置残留。</param>
    public AudioPipeline(
        Channel<MediaPacket> packetQueue,
        IAudioDecoder decoder,
        IAudioOutput output,
        SampleQueue sampleQueue,
        Synchronizer synchronizer,
        IMediaClock clock,
        ILogger<AudioPipeline> logger,
        IFramePool<AudioFrame>? framePool = null,
        IReadOnlyList<Func<AudioFrame, AudioFrame>>? transforms = null,
        Action? effectReset = null,
        Action<AudioFrame>? audioDataSink = null)
    {
        _packetQueue = packetQueue;
        _decoder = decoder;
        _output = output;
        _sampleQueue = sampleQueue;
        _synchronizer = synchronizer;
        _clock = clock;
        _logger = logger;
        _framePool = framePool;
        _transforms = transforms;
        _effectReset = effectReset;
        _audioDataSink = audioDataSink;
    }

    /// <summary>
    /// 归还帧到池（若池可用）或 Dispose（V1 兼容）。
    /// </summary>
    private void ReturnFrame(AudioFrame frame)
    {
        if (frame is null)
            return; // 变换链丢弃帧时（已 Dispose 输入帧）
        if (_framePool != null)
            _framePool.Return(frame);
        else
            frame.Dispose();
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
    /// 清空队列、解码器缓冲和音频输出（Seek 后调用）。同步版本，用于无法 await 的场景。
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
                _logger.LogWarning("音频管线暂停确认超时（50ms），等待解码锁确保安全");
            }

            // 阶段2: 获取解码锁（慢速路径，确保无 DecodeAsync 在执行）
            if (_decodeLock.Wait(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    _sampleQueue.Clear(_framePool);
                    _decoder.Reset();
                    _effectReset?.Invoke(); // V2-08.1: 重置有状态音频效果（清除延迟线/包络/滤波器历史，防 Seek 后瞬态）
                    _output.Flush();
                }
                finally
                {
                    _decodeLock.Release();
                }
            }
            else
            {
                _logger.LogError("音频管线解码锁获取超时（2s），标记延迟 Reset 防止竞态崩溃");
                _sampleQueue.Clear(_framePool);
                _output.Flush();
                _pendingDecoderReset = true;   // 锁超时未做 Reset，待管线线程下次进入锁时补做，确保解码器状态必然复位
                _pendingEffectReset = true;    // 同上：有状态效果延迟重置，避免与 Process 并发
            }
        }
        else
        {
            // 管线未运行，无需锁
            _sampleQueue.Clear(_framePool);
            _decoder.Reset();
            _output.Flush();
        }

        if (shouldResume)
        {
            _isPaused = false;
        }
    }

    /// <summary>
    /// 清空队列、解码器缓冲和音频输出（Seek 后调用）。异步版本，优先使用。
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
                _logger.LogWarning("音频管线暂停确认超时（50ms），等待解码锁确保安全");
            }

            // 阶段2: 获取解码锁（慢速路径，确保无 DecodeAsync 在执行）
            if (await _decodeLock.WaitAsync(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    _sampleQueue.Clear(_framePool);
                    _decoder.Reset();
                    _effectReset?.Invoke(); // V2-08.1: 重置有状态音频效果（清除延迟线/包络/滤波器历史，防 Seek 后瞬态）
                    _output.Flush();
                }
                finally
                {
                    _decodeLock.Release();
                }
            }
            else
            {
                _logger.LogError("音频管线解码锁获取超时（2s），标记延迟 Reset 防止竞态崩溃");
                _sampleQueue.Clear(_framePool);
                _output.Flush();
                _pendingDecoderReset = true;   // 锁超时未做 Reset，待管线线程下次进入锁时补做，确保解码器状态必然复位
                _pendingEffectReset = true;    // 同上：有状态效果延迟重置，避免与 Process 并发
            }
        }
        else
        {
            // 管线未运行，无需锁
            _sampleQueue.Clear(_framePool);
            _decoder.Reset();
            _output.Flush();
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

                    // V2-08.1: 解码锁超时期间 Flush 可能跳过效果重置，此处补做，
                    // 确保有状态效果（均衡器 biquad / 混响延迟线 / 压缩器包络）必然复位
                    if (_pendingEffectReset)
                    {
                        _effectReset?.Invoke();
                        _pendingEffectReset = false;
                    }

                    // 双重检查：获取锁后确认未暂停（防止在等待锁期间被 Flush 暂停）
                    if (_isPaused)
                    {
                        packet.Dispose();
                        continue; // finally 会释放锁
                    }

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
            // V2-06 C4/C6: 经过音频变换链（音量/效果/混音，所有权转移）
            if (_transforms != null)
            {
                foreach (var transform in _transforms)
                {
                    frame = transform(frame);
                    if (frame == null)
                    {
                        _logger.LogWarning("音频变换链丢弃帧（返回 null），跳过提交");
                        return; // 变换已 Dispose 输入帧
                    }
                }
            }

            // 更新主时钟
            _synchronizer.OnAudioFrameSubmitted(frame);

            // V2-09 U2: 在提交给输出前同步触发音频数据事件（供可视化器消费）。
            // 同步调用且位于 Submit 之前，frame 仍存活（ReturnFrame 在 finally 之后），
            // 订阅方（音频管线线程）须只读借用并同步拷贝所需数据。无 I/O、无 await，
            // 纯内存操作，绝对不构成伪异步。
            _audioDataSink?.Invoke(frame);

            // 提交给输出（V2: Output 不再 Dispose 帧，由管线归还到池）
            _output.Submit(frame);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "音频帧提交失败");
        }
        finally
        {
            // V2: Submit 后归还帧到池（无论成功或异常）
            ReturnFrame(frame);
        }
    }

    /// <summary>
    /// 释放管线资源（解码锁和 CTS）。
    /// </summary>
    /// <remarks>
    /// <para>必须在管线线程退出后调用。DisposeAsync 路径在 Step_StopPipelinesAsync join 后调用。</para>
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
            _logger.LogWarning(ex, "音频管线线程 join 失败，仍继续释放资源");
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

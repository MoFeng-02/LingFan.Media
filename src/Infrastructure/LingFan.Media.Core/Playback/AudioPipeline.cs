using System;
using System.Diagnostics;
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
    private volatile Channel<MediaPacket> _packetQueue;
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
    // 自然完成标志：仅当管线因"流末耗尽"（包源关闭）正常退出时置位，
    // 用于区分 Ended（自然结束）与取消退出，避免停止/释放时误触发 Completed 事件。
    private bool _completedNaturally;

    /// <summary>
    /// 解码锁：确保 DecodeAsync 与 Reset 不会并发执行。
    /// PipelineLoop 在解码+入队期间持有锁，Flush/FlushAsync 在 Clear+Reset 前获取锁。
    /// 即使暂停确认超时（管线线程卡在长解码中），锁也能确保安全。
    /// </summary>
    private readonly SemaphoreSlim _decodeLock = new(1, 1);
    private volatile bool _pendingDecoderReset;
    private bool _disposed;
    // 诊断用：上次 SubmitBatch 结束的时间戳（Stopwatch ticks），用于计算「解码阶段间隙」。
    private long _lastSubmitEndTs;

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
    /// 重播（Ended→Playing）修复：与 <see cref="VideoPipeline.SetPacketQueue"/> 同构。
    /// <see cref="BufferManager.ResetQueues"/> 在流末 EOF 后重建包通道实例，
    /// 本管线需把内部持有的包队列引用重指向新通道，避免重播时解码循环从已 Complete 的旧通道读到 EOF。
    /// </summary>
    internal void SetPacketQueue(Channel<MediaPacket> queue) => _packetQueue = queue;

    /// <summary>
    /// 自然播放完成事件。当管线因"流末耗尽"（包源关闭、所有采样已提交）正常退出时触发；
    /// 由 <c>Stop</c>/<c>Dispose</c> 取消退出时不触发。
    /// <see cref="MediaPipelineHost"/> 聚合 video/audio 两条管线的 <see cref="Completed"/> 为
    /// <c>PlaybackCompleted</c>，最终驱动播放器转 <see cref="MediaState.Ended"/>。
    /// </summary>
    public event EventHandler? Completed;

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
        // 🔴 重播正确性：若本次 Start 是从自然排干(Ended)态重启，必须清零自然完成标志，
        // 否则残留 true 会在后续 Stop/Dispose 的 finally 中误触发 Completed → 错误发出 Ended。
        _completedNaturally = false;

        if (_cts.IsCancellationRequested)
        {
            _cts = new CancellationTokenSource();
        }

        // 首次启动必须真正启动 WASAPI 客户端（IAudioClient.Start），否则设备永不拉取缓冲区、
        // 首帧写满缓冲后后续 Submit 全部超时丢帧 → 仅初始缓冲那 ~1 秒出声后静音。
        // 原实现只在"恢复播放"分支调 _output.Resume()，首次启动漏调，导致首次播放无音频渲染。
        _output.Resume();

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
                    _sampleQueue.Reset(_framePool);
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
                _sampleQueue.Reset(_framePool);
                _output.Flush();
                _pendingDecoderReset = true;   // 锁超时未做 Reset，待管线线程下次进入锁时补做，确保解码器状态必然复位
                _pendingEffectReset = true;    // 同上：有状态效果延迟重置，避免与 Process 并发
            }
        }
        else
        {
            // 管线未运行，无需锁
            _sampleQueue.Reset(_framePool);
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
                    _sampleQueue.Reset(_framePool);
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
                _sampleQueue.Reset(_framePool);
                _output.Flush();
                _pendingDecoderReset = true;   // 锁超时未做 Reset，待管线线程下次进入锁时补做，确保解码器状态必然复位
                _pendingEffectReset = true;    // 同上：有状态效果延迟重置，避免与 Process 并发
            }
        }
        else
        {
            // 管线未运行，无需锁
            _sampleQueue.Reset(_framePool);
            _decoder.Reset();
            _output.Flush();
        }

        if (shouldResume)
        {
            _isPaused = false;
        }
    }

    // 前瞻窗口（预解码帧数）：让提交阶段能成批提交，折叠逐帧 STA 跨线程往返的固定开销（修复听感卡顿/掉速）。
    private const int PrerollFrames = 16;

    // 🔴 诊断（LINGFAN_AUDIO_DIAG=1）：打点音频循环各相位时长，定位 headful 断音根因。
    // 纯诊断、零架构风险：仅在 env 置 1 时开启，生产路径恒为 false，不改变任何控制流/时序。
    private static readonly bool AudioDiagEnabled =
        string.Equals(Environment.GetEnvironmentVariable("LINGFAN_AUDIO_DIAG"), "1", StringComparison.Ordinal);

    // 🔴 EOS 时序诊断（LINGFAN_EOS_DIAG=1）：记录自然完成瞬间的「主时钟位置」，定位偶发提前结束根因。
    private static readonly bool EosDiagEnabled =
        string.Equals(Environment.GetEnvironmentVariable("LINGFAN_EOS_DIAG"), "1", StringComparison.Ordinal);

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

                // 1. 提交阶段：把当前可用解码帧整批提交（一次 STA 往返写多帧，是修复掉速的关键）
                if (_sampleQueue.Count > 0)
                {
                    // 关闭/停止：不再向渲染线程阻塞提交，直接归还剩余帧。
                    // 避免 Stop/Dispose 时 SubmitBatch 卡在 WaitForBufferSpace 2s 超时累加 → 退出挂起 5s。
                    if (_cts.IsCancellationRequested)
                    {
                        while (_sampleQueue.TryDequeue(out var f) && f != null)
                            ReturnFrame(f);
                        break;
                    }

                    var batch = new List<AudioFrame>(_sampleQueue.Count);
                    while (_sampleQueue.TryDequeue(out var f) && f != null)
                        batch.Add(f);
                    if (AudioDiagEnabled)
                    {
                        var subStart = Stopwatch.GetTimestamp();
                        SubmitBatch(batch, _cts.Token);
                        _lastSubmitEndTs = Stopwatch.GetTimestamp();
                        var subMs = Stopwatch.GetElapsedTime(subStart).TotalMilliseconds;
                        if (subMs > 80)
                            _logger.LogWarning("[AUDIO-DIAG] SubmitBatch 阻塞 {Ms}ms（WaitForBufferSpace 设备节奏，属正常）", subMs);
                    }
                    else
                    {
                        SubmitBatch(batch, _cts.Token);
                        _lastSubmitEndTs = Stopwatch.GetTimestamp();
                    }
                    continue;
                }

                // 2. 解码阶段：队列空，读包解码；并预解码若干包填满前瞻窗口，
                //    使提交阶段能成批提交（把解码与提交解耦，避免逐帧阻塞）。
                // 解码阶段入口：记录自上次提交以来的总间隙
                var phaseStart = Stopwatch.GetTimestamp();

                MediaPacket? packet;
                try
                {
                    if (!_packetQueue.Reader.TryRead(out packet))
                    {
                        if (AudioDiagEnabled)
                        {
                            var readStart = Stopwatch.GetTimestamp();
                            packet = await _packetQueue.Reader.ReadAsync(_cts.Token);
                            var readMs = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                            if (readMs > 80)
                                _logger.LogWarning("[AUDIO-DIAG] ReadAsync 阻塞 {Ms}ms（上游包未及时到达 → 提交中断 → 静音）", readMs);
                        }
                        else
                        {
                            packet = await _packetQueue.Reader.ReadAsync(_cts.Token);
                        }
                    }
                }
                catch (ChannelClosedException)
                {
                    // 流结束：所有采样已提交，标记自然完成并退出，finally 中触发 Completed 事件。
                    if (EosDiagEnabled)
                        _logger.LogInformation("[AUDIO-EOS] 自然完成 masterTime={Master:g}",
                            _synchronizer.GetCurrentMasterTime());
                    _sampleQueue.Complete();
                    _completedNaturally = true;
                    break;
                }

                if (AudioDiagEnabled)
                {
                    var decStart = Stopwatch.GetTimestamp();
                    await DecodeAndEnqueueAsync(packet);
                    var decMs = Stopwatch.GetElapsedTime(decStart).TotalMilliseconds;
                    if (decMs > 80)
                        _logger.LogWarning("[AUDIO-DIAG] Decode+Enqueue 阻塞 {Ms}ms（解码慢）", decMs);
                }
                else
                {
                    await DecodeAndEnqueueAsync(packet);
                }

                // 前瞻：采样队列未填满且仍有包立即可用时，连续解码（不 await），把解码与提交解耦
                while (_sampleQueue.Count < PrerollFrames && _packetQueue.Reader.TryRead(out var next))
                {
                    if (AudioDiagEnabled)
                    {
                        var pdStart = Stopwatch.GetTimestamp();
                        await DecodeAndEnqueueAsync(next);
                        var pdMs = Stopwatch.GetElapsedTime(pdStart).TotalMilliseconds;
                        if (pdMs > 80)
                            _logger.LogWarning("[AUDIO-DIAG] 前瞻 Decode 阻塞 {Ms}ms（解码慢）", pdMs);
                    }
                    else
                    {
                        await DecodeAndEnqueueAsync(next);
                    }
                }

                // 解码阶段总间隙诊断（线程池续体延迟：读/解码均快但整段仍慢）
                if (AudioDiagEnabled && _lastSubmitEndTs != 0)
                {
                    var gapMs = Stopwatch.GetElapsedTime(_lastSubmitEndTs).TotalMilliseconds;
                    var totalPhaseMs = Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
                    if (gapMs > 120 && totalPhaseMs < gapMs - 20)
                        _logger.LogWarning("[AUDIO-DIAG] 解码阶段间隙 {Gap}ms 但读/解码均快 → 疑似线程池续体延迟", gapMs);
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
            // 仅当流末自然耗尽时触发 Completed（取消退出不触发，避免 Stop/Dispose 误报 Ended）。
            if (_completedNaturally)
                Completed?.Invoke(this, EventArgs.Empty);
            _isRunning = false;
        }
    }

    /// <summary>
    /// 整批提交解码帧。先逐帧做变换链 + 主时钟 + 数据事件（与历史单帧 SubmitFrame 语义完全一致），
    /// 再一次性交给输出（支持 <see cref="IBatchAudioSubmit"/> 时折叠为单次 STA 往返，否则退回逐帧 Submit）。
    /// 帧所有权始终在本方法（pipeline 线程）内归还到池，绝不跨线程归还（帧池非线程安全）。
    /// </summary>
    private void SubmitBatch(List<AudioFrame> batch, CancellationToken ct = default)
    {
        if (batch.Count == 0) return;

        // 预处理：变换链 + 主时钟 + 数据事件（逐帧，与历史单帧语义一致）
        var prepared = new List<AudioFrame>(batch.Count);
        foreach (var frame in batch)
        {
            var f = frame;
            if (_transforms != null)
            {
                foreach (var transform in _transforms)
                {
                    f = transform(f);
                    if (f == null)
                    {
                        _logger.LogWarning("音频变换链丢弃帧（返回 null），跳过提交");
                        goto nextFrame; // 变换已 Dispose 输入帧，不归还（与历史行为一致）
                    }
                }
            }

            _synchronizer.OnAudioFrameSubmitted(f);
            _audioDataSink?.Invoke(f);
            prepared.Add(f);
        nextFrame: ;
        }

        try
        {
            if (_output is IBatchAudioSubmit batched)
                batched.SubmitBatch(prepared, ct);
            else
                foreach (var f in prepared)
                    _output.Submit(f);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 取消（Stop/Dispose）时 WasapiRenderLoop 已静默中断提交，正常路径不记错误（回归双保险）。
            _logger.LogError(ex, "音频批提交失败");
        }
        finally
        {
            // 帧所有权始终在 pipeline 线程归还（帧池非线程安全，严禁跨线程归还）
            foreach (var f in prepared)
                ReturnFrame(f);
        }
    }

    /// <summary>
    /// 解码单个包并入队（与 Flush/Reset 加锁，防止竞态）。供 PipelineLoop 主解码与前瞻预解码共用。
    /// </summary>
    private async Task DecodeAndEnqueueAsync(MediaPacket packet)
    {
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
                return; // finally 会释放锁
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

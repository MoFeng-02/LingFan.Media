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
/// <para>丢帧和 Present 后的帧通过 ReturnFrame 归还到 FramePool或 Dispose。</para>
/// </remarks>
public sealed class VideoPipeline : IAsyncDisposable, IDisposable
{
    private volatile Channel<MediaPacket> _packetQueue;
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
    private volatile bool _eosReached;     // EOS 意图（包源结束）已到达：让 Drop→Present 保护覆盖整个 DRAIN 窗口
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
    // EOS 时序诊断（LINGFAN_EOS_DIAG=1）：记录自然完成瞬间的「主时钟位置」，定位偶发提前结束成因。
    // 纯诊断：仅 env 置 1 时开启；生产路径恒为 false，不改变任何控制流/时序。
    private static readonly bool EosDiagEnabled =
        string.Equals(Environment.GetEnvironmentVariable("LINGFAN_EOS_DIAG"), "1", StringComparison.Ordinal);
    // A/V 同步诊断（LINGFAN_SYNC_DIAG=1）：呈现瞬间记录「视频帧 PTS − 音频主时钟」= 用户实际感知的音画偏差。
    // delta>0 ⇒ 视频领先音频；delta<0 ⇒ 视频落后；全程单调增长 ⇒ 时钟速率漂移。纯诊断。
    private static readonly bool SyncDiagEnabled =
        string.Equals(Environment.GetEnvironmentVariable("LINGFAN_SYNC_DIAG"), "1", StringComparison.Ordinal);
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    private volatile bool _pauseAcknowledged;
    private TaskCompletionSource<bool>? _pauseAckTcs;
    // 重播衔接卡顿解决：**首帧门控 + 视频预滚动**。
    // 成因：MediaPipelineHost.StartAsync 原为「音频先启动 → 视频后启动」。首播时 BufferManager 在
    // OpenAsync 阶段就已暖好包队列，视频首帧与音频同刻出现；但**重播**路径下
    // SeekAsync 会先 Stop 再重启 demuxer 读取线程、并 Reset 解码器，视频首帧要较长时间才产出，
    // 而音频设备（= 主时钟源 GetPlaybackPositionDirect）已在 0ms 起跑 →
    //   ① 起始一小段时间内的帧全部被 Synchronizer 判 Drop；
    //   ② 这段空窗内屏幕停在「上一次播放的末帧」，随后突跳到视频首个可呈现位置
    //   ⇒ 用户感知为「第二次播放先卡一下再继续」。
    // 修复：启动编排改为「视频先起 → 等预滚动 → 再启动音频设备 → 放行视频呈现」，
    // 把首帧预热**吸收在 PlayAsync 内部**（主时钟尚未起跑，不计入播放时间线），
    // 音频就绪后首帧与音频同刻出现 → 无缝。首播时预滚动瞬时满足，零回归。
    private volatile bool _audioReadySignaled;   // 由 MediaPipelineHost 在音频设备启动后置位（跨线程写）
    private volatile bool _audioGateOpened;      // 呈现线程已通过门控（Start 复位，之后仅呈现线程读写）
    private volatile bool _firstFramePresented;  // 首帧已真正上屏（ProcessFrame 首次 Present 后置位，供 A/V 启动对齐）
    private TaskCompletionSource<bool>? _firstFramePresentedTcs;  // 音频编排等待点（Start 重建）
    // 预滚动帧数下限：等帧队列预热到该深度再启动音频设备（吸收重播的重定位+解码开销）。
    // 取 2 而非 TargetDepth(6)：够消除首帧空窗即可，等太久会让「点播放到出声」的体感延迟变长。
    private const int VideoPrerollFrames = 2;
    // 预滚动等待上限：超时即放行启动音频，宁可轻微不同步也绝不卡住播放（解码异常/无视频帧时兜底）。
    private const int VideoPrerollTimeoutMs = 2000;
    // 门控等待上限：SignalAudioReady 因音频启动异常未被调用时的兜底，绝不让呈现线程永久阻塞。
    private const int AudioGateTimeoutMs = 3000;
    // 主时钟停摆降级标志（仅呈现线程读写，无需 volatile）：见 WaitUntilDue 的停摆看门狗。
    // 置位后同步等待改用 50ms 宽限期，避免每帧空等 500ms 把画面压到 2fps；主时钟恢复推进即复位。
    private bool _masterClockStalled;
    private long _droppedFrames;
    // A/V 同步诊断节流字段（仅 LINGFAN_SYNC_DIAG=1 时读取，生产路径恒 0 不影响任何逻辑）。
    private long _lastSyncDiagTicks;
    private int _presentCount;

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
    /// <param name="framePool">帧对象池。</param>
    /// <param name="processors">视频后处理链。
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
        // 音画同步：呈现提前量 = 渲染后端真实「Present→上屏」端到端延迟
        // （IVideoRenderer.PresentationLatency，D3D11≈40ms@60Hz 含 vsync 相位+Present/消费者管线，无头=0），
        // 不再用 SyncThreshold(50ms) 作呈现偏移。LINGFAN_SYNC_LEAD_MS 仅作为叠加微调（默认 0，
        // 作用于正确的「呈现延迟」变量，而非音频时钟）。
        double manualLeadMs = 0.0;
        string? leadEnv = System.Environment.GetEnvironmentVariable("LINGFAN_SYNC_LEAD_MS");
        if (int.TryParse(leadEnv, out int leadMs) && leadMs > 0)
            manualLeadMs = leadMs;
        _synchronizer.PresentationLatency = _renderer.PresentationLatency + TimeSpan.FromMilliseconds(manualLeadMs);
        _logger = logger;
        _framePool = framePool;
        _processors = processors;
        _processorReset = processorReset;
        _videoFrameSink = videoFrameSink;
    }

    /// <summary>
    /// 归还帧到池（若池可用）或 Dispose。
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
    /// 重播（Ended→Playing）修复：<see cref="BufferManager.ResetQueues"/> 在流末 EOF 后
    /// <c>StartAsync</c> 会<b>重建全新的包通道实例</b>以恢复可写状态，而本管线在 OpenAsync 构造时
    /// 按值捕获了旧通道引用。若不重指向新通道，重播时解码循环会从已 <c>Complete()</c> 的旧通道
    /// 一上来就读到 <see cref="ChannelClosedException"/> → 排空 0 帧 → 立即收尾 → 二次播放呈现 0 帧。
    /// <see cref="MediaPlayer.SeekAsync"/> 在 <c>BufferManager.StartAsync</c> 之后调用本方法把引用重新指向新通道；
    /// 由于重播的 <c>Start()</c> 会重启解码循环（旧循环已在首播 EOF 时 break），新循环读到新通道即可正常取包。
    /// 非 EOF 重播（播放中 Seek）下 <c>ResetQueues</c> 不触发，本方法重指向的是同一实例，幂等无害。
    /// </summary>
    internal void SetPacketQueue(Channel<MediaPacket> queue) => _packetQueue = queue;

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
        // 首帧门控复位（仅全新启动/重播路径）：恢复暂停时上方已早退，门控保持已开启不受影响。
        _audioReadySignaled = false;
        _audioGateOpened = false;
        // 首帧上屏信号复位：每次全新启动/重播重建 TCS（TCS 不可重置），
        // 供 MediaPipelineHost 在启动音频前等待「视频首帧已真正上屏」。
        _firstFramePresented = false;
        _firstFramePresentedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _decodeDone = false;
        _eosReached = false;
        // 重播正确性：若本次 Start 是从自然排干(Ended)态重启，必须清零自然完成标志，
        // 否则残留 true 会在后续 Stop/Dispose 的 finally 中误触发 Completed → 错误发出 Ended。
        _completedNaturally = false;

        // A/V 同步诊断：每轮开播重置计数，重新抓取起始帧偏移（重播/恢复也重置）。
        _presentCount = 0;
        _lastSyncDiagTicks = 0;

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
    /// 等待视频帧队列预滚动到 <see cref="VideoPrerollFrames"/> 帧（或超时/解码结束）。
    /// 由 <see cref="MediaPipelineHost.StartAsync"/> 在 <see cref="Start"/> 之后、**启动音频设备之前** await，
    /// 使主时钟（取自音频播放游标）在视频已有帧可呈现后才起跑。
    /// </summary>
    /// <remarks>
    /// <para>这是「重播先卡一下」的解决点：重播时 demuxer 重定位 + 解码器 Reset 需较长时间，
    /// 若音频先起跑，这段空窗会被计入播放时间线，导致开头帧被判 Drop、画面停在上次末帧后突跳。
    /// 把等待前移到音频启动前，该开销就落在「点击播放到出声」之间，用户感知为正常起播延迟而非卡顿。</para>
    /// <para>首播路径下 <c>BufferManager</c> 已在 <c>OpenAsync</c> 阶段暖好包队列，本方法通常在
    /// 一两个轮询周期内立即返回，无回归。</para>
    /// <para>三条提前返回路径确保绝不卡死播放：取消、解码已结束（极短流/无帧）、超时（<see cref="VideoPrerollTimeoutMs"/>）。</para>
    /// </remarks>
    public async Task WaitForPrerollAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
            return;

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            if (cancellationToken.IsCancellationRequested || _cts.IsCancellationRequested)
                return;
            if (_frameQueue.Count >= VideoPrerollFrames)
                return;
            // 解码生产者已收尾（流极短或异常）：再等也不会有帧，立即放行。
            if (_decodeDone || _frameQueue.IsCompleted)
                return;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds > VideoPrerollTimeoutMs)
            {
                _logger.LogWarning(
                    "视频预滚动等待超时（{Timeout}ms，队列仅 {Count} 帧），继续启动音频以免阻塞播放。",
                    VideoPrerollTimeoutMs, _frameQueue.Count);
                return;
            }

            try
            {
                await Task.Delay(5, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 通知视频管线「音频设备已启动、主时钟已可用」，放行呈现循环的首帧门控。
    /// 由 <see cref="MediaPipelineHost.StartAsync"/> 在音频管线启动后（含异常路径的 finally）调用。
    /// </summary>
    /// <remarks>
    /// 无音频轨时也必须调用（否则呈现线程会空等到 <see cref="AudioGateTimeoutMs"/> 兜底超时）。
    /// 幂等：重复调用无副作用；<see cref="Start"/> 会在下一轮全新启动时复位。
    /// </remarks>
    public void SignalAudioReady() => _audioReadySignaled = true;

    /// <summary>
    /// 等待视频首帧真正上屏（<see cref="ProcessFrame"/> 首次 Present 调用返回）。供
    /// <see cref="MediaPipelineHost.StartAsync"/> 在启动音频前 await，使音频 WASAPI 出声不早于
    /// 视频首帧上屏（A/V 启动对齐）。带超时兜底，绝不阻塞播放。
    /// </summary>
    /// <remarks>无视频轨（<c>VideoPipeline</c> 为 null）或被提前释放时，调用方判空不会走到这里。</remarks>
    public async Task WaitForFirstFramePresentedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_firstFramePresented) return;
        var tcs = _firstFramePresentedTcs;
        if (tcs == null) return;
        try { await Task.WhenAny(tcs.Task, Task.Delay(timeout, cancellationToken)); }
        catch (OperationCanceledException) { }
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
    /// 先暂停管线线程，等待确认或获取解码锁后清空和重置，最后恢复运行。
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
                    _frameQueue.Reset(_framePool);
                    _decoder.Reset();
                    _processorReset?.Invoke(); // 重置有状态处理器（释放 _held 等）
                }
                finally
                {
                    _decodeLock.Release();
                }
            }
            else
            {
                _logger.LogError("视频管线解码锁获取超时（2s），标记延迟 Reset 防止竞态崩溃");
                _frameQueue.Reset(_framePool); // Channel 线程安全，仍然清空
                _pendingDecoderReset = true;   // 锁超时未做 Reset，待管线线程下次进入锁时补做，确保解码器状态必然复位
                _pendingProcessorReset = true;   // 同上：有状态处理器延迟重置，避免与 Process 并发
            }
        }
        else
        {
            // 管线未运行，无需锁
            _frameQueue.Reset(_framePool);
            _decoder.Reset();
        }

        if (shouldResume)
        {
            _isPaused = false;
        }
    }

    /// <summary>
    /// 清空队列和解码器缓冲（Seek 后调用）。异步版本，优先使用。
    /// 先暂停管线线程，等待确认或获取解码锁后清空和重置，最后恢复运行。
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
                    _frameQueue.Reset(_framePool);
                    _decoder.Reset();
                    _processorReset?.Invoke(); // 重置有状态处理器（释放 _held 等）
                }
                finally
                {
                    _decodeLock.Release();
                }
            }
            else
            {
                _logger.LogError("视频管线解码锁获取超时（2s），标记延迟 Reset 防止竞态崩溃");
                _frameQueue.Reset(_framePool); // Channel 线程安全，仍然清空
                _pendingDecoderReset = true;   // 锁超时未做 Reset，待管线线程下次进入锁时补做，确保解码器状态必然复位
                _pendingProcessorReset = true;   // 同上：有状态处理器延迟重置，避免与 Process 并发
            }
        }
        else
        {
            // 管线未运行，无需锁
            _frameQueue.Reset(_framePool);
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
            // OS/GC 抢占造成的帧间墙钟抖动（残余抖动成因）。LongRunning 专用线程上该优先级全程有效。
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

                // 首帧门控：音频设备（主时钟源）启动前，**不做任何同步判定/呈现**。
                // 若不门控，此窗口内 GetCurrentMasterTime 恒为 0：第 0 帧会被立即 Present，
                // 后续帧走 Wait 分支并触发 WaitUntilDue 的「主时钟停摆」看门狗（500ms 后降级直接出帧）
                // → 画面按解码节奏爆发式推进，比原缺陷更糟。门控期间屏幕保持上一次播放的末帧，
                // 待主时钟起跑后从 PTS=0 起同刻呈现，衔接无跳变。
                if (!_audioGateOpened)
                {
                    long gateStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    while (!_audioReadySignaled)
                    {
                        if (_cts.IsCancellationRequested)
                            break;
                        // 门控等待期间仍需应答 Flush 的暂停确认，避免 FlushAsync 白等 50ms 走慢速路径。
                        if (_isPaused)
                        {
                            _pauseAcknowledged = true;
                            _pauseAckTcs?.TrySetResult(true);
                        }
                        if (System.Diagnostics.Stopwatch.GetElapsedTime(gateStart).TotalMilliseconds > AudioGateTimeoutMs)
                        {
                            _logger.LogWarning(
                                "视频首帧门控等待音频就绪超时（{Timeout}ms），降级直接呈现以免画面永久冻结。",
                                AudioGateTimeoutMs);
                            break;
                        }
                        Thread.Sleep(2);
                    }
                    _audioGateOpened = true;
                    if (_cts.IsCancellationRequested)
                        break;
                    continue;   // 重新走一轮，确保暂停/取消状态在放行后被重新评估
                }

                // 队头帧不出队即判定（Peek 在 SingleReader 下安全）。
                var head = _frameQueue.Peek();
                if (head is null)
                {
                    // 队列空：若生产者已结束（流尾）则收尾；否则短暂让出，等待生产者补帧。
                    if (_frameQueue.IsCompleted || _decodeDone)
                    {
                        // 流末自然耗尽（非取消）：标记后退出，finally 中触发 Completed 事件。
                        if (EosDiagEnabled)
                            _logger.LogInformation(
                                "[VIDEO-EOS] 自然完成 masterTime={Master:g} 帧队列余量={Cnt} decodeDone={DecodeDone}",
                                _synchronizer.GetCurrentMasterTime(), _frameQueue.Count, _decodeDone);
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

                // EOS（流末已到）覆盖：原实现在 eos 下把 Wait/Drop 一律改为 Present，
                // 导致末段缓冲超前的帧（TargetDepth≈6 帧 + DRAIN 尾 GOP）被瞬间整批呈现 →
                // "帧直接推进 / 画面突然变快"（用户痛点：末秒帧爆发式推进后骤停）。
                // 修正策略（仍保末帧绝不丢，但消除爆发）：
                //   · eos && Drop（音频时钟已越界）→ 改 Present（绝不丢末帧）；
                //   · eos && Wait（末段缓冲超前的帧）→ 保持 Wait，按各自 PTS 平滑收口；
                //   · eos && Present → 保持 Present。
                // 尾冻已由 DecodeLoop 的 EOS DRAIN 解决（末段 GOP 已入队），此处不再依赖强制呈现；
                // 正常播放末段帧按各自节奏呈现，爆发消失，末帧仍常驻屏直到音频结束。
                bool eos = _eosReached || _frameQueue.IsCompleted || _decodeDone;
                if (eos && action == SyncAction.Drop)
                    action = SyncAction.Present;

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
                        // EOS 意图已到达：提前置位，使下方 DRAIN 全窗口内 PipelineLoop 的
                        // Drop→Present 保护生效（否则 DRAIN 期间 eos 未置位，尾帧走真丢分支）。
                        _eosReached = true;
                        // 包源结束：必须先 DRAIN 解码器把末尾 B 帧重排缓冲（末段 GOP）全部取出入队，
                    // 再 Complete 帧队列 —— 顺序颠倒会让呈现侧提前收尾，末段 GOP 整批丢失。
                    // 修复：原实现只 Complete 不 DRAIN，导致 H.264/H.265 尾部
                    // 重排帧滞留 MFT 内部缓冲永不吐出 → 最后呈现帧冻结（即"30s 画面不动"缺陷）。
                    // 音频无 B 帧（无 ctts）不受影响，故仅视频侧需此 EOS 排空。
                    try
                    {
                        VideoFrame? drained;
                        while ((drained = await _decoder.FlushAsync()) != null)
                        {
                            await _frameQueue.EnqueueAsync(drained, _cts.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "视频解码器 EOS 排空异常（末段帧可能丢失）");
                    }
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

                    // 解码锁超时期间 Flush 可能跳过处理器重置，此处补做
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
                        // 经过视频后处理链（所有权转移）
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
            // 呈现误差：相对"最早可呈现时刻"(frame.Timestamp − 真实上屏延迟)的偏移。
            // Peek 方案下理想值 ≈ 0~2ms（WaitUntilDue 自旋收口精度）；若该值显著 >0
            // 说明帧在队头等到了过期后才被取走呈现（仍被某处阻塞）。
            var thr = _synchronizer.PresentationLatency;
            var masterNow = _synchronizer.GetCurrentMasterTime();
            double errMs = (masterNow - (frame.Timestamp - thr)).TotalMilliseconds;
            string? report = PacingDiagnostics.Present.OnPresent(frame.Timestamp, _frameQueue.Count, errMs);
            if (report != null)
            {
                _logger.LogInformation("[PACING] {Report}", report);
                _logger.LogInformation("[CLOCK] {Snapshot}", PacingDiagnostics.Clock.Snapshot());
            }
        }

        // A/V 同步诊断（LINGFAN_SYNC_DIAG=1）：**独立于 PacingDiagnostics**，呈现瞬间记录
        // 「视频帧 PTS − 音频主时钟」= 用户实际感知到的音画偏差。delta>0 ⇒ 视频领先音频（画面先到）；
        // delta<0 ⇒ 视频落后音频（声音先到）。前 8 帧全采以暴露起始偏移；之后每 500ms 采样一次，
        // 观察是否单调增长（时钟速率漂移）或长期恒定≈某值（固定偏移 = 视频/音频时间线起点不齐，
        // 典型为 MF/MP4 edit list 使视频流 PTS 起点与音频时钟原点错开）。
        // 纯观测：SyncDiagEnabled 恒 false 时整块跳过，且不依赖 PacingDiagnostics.Enabled。
        if (SyncDiagEnabled)
        {
            var masterNow = _synchronizer.GetCurrentMasterTime();
            double syncDeltaMs = (frame.Timestamp - masterNow).TotalMilliseconds;
            long nowTicks = DateTime.UtcNow.Ticks;
            if (_presentCount < 8 || nowTicks - _lastSyncDiagTicks > 5_000_000L) // 前 8 帧 + 每 500ms
            {
                _logger.LogInformation(
                    "[SYNC] present videoPTS={V:g} audioClock={A:g} delta={D,7:F1}ms queue={Q} pidx={P}",
                    frame.Timestamp, masterNow, syncDeltaMs, _frameQueue.Count, _presentCount);
                _lastSyncDiagTicks = nowTicks;
            }
            _presentCount++;
        }

        try
        {
            // 唯一帧出口（帧契约）：所有帧经注入的 _videoFrameSink（生产路径中恒为非空 lambda，
            // MediaPlayer 注入为 frame => _frameChannel.Emit，内部再扇出到订阅的 Sink——
            // 无头计算 / Skia 软渲染 / D3D11 零拷贝 GPU 呈现三者互斥，由 Sink 内部路由）。
            // 绝不在管线内直接 _renderer.Present(frame)（曾经的 else 兜底分支已删除：它构成第二条
            // 绕开 FrameChannel 扇出的呈现路径，违反「帧路由唯一、绝双路径」原则）。
            // 若 _videoFrameSink 为 null（仅测试/无 UI 且未接 Sink 的调用方），帧在此静默归池、不呈现。
            // 注意：路由决策在 Sink lambda 内完成，不可在此判 _videoFrameSink == null 来决定 D3D11——
            // 那样会让 D3D11 模式（无订阅方，但 lambda 非空）永不调用渲染器，导致视频不显示。
            _videoFrameSink?.Invoke(frame);
            // 首帧已真正提交上屏：通知 A/V 启动编排等待点，
            // 使音频 WASAPI 启动不早于视频首帧上屏 → 解决「声音比视频先出」。
            if (!_firstFramePresented)
            {
                _firstFramePresented = true;
                _firstFramePresentedTcs?.TrySetResult(true);
            }
        }
        finally { ReturnFrame(frame); } // Present 后归还到池
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
        // 呈现目标 = frameTimestamp − 真实上屏延迟：帧在 audioClock 到达 PTS 前「本延迟」时刻调用 Present，
        // 像素恰在 PTS 时可见（vsync）。此前错误用 SyncThreshold(50ms) → 视频系统性提前。
        var threshold = _synchronizer.PresentationLatency;
        long targetQpc = 0;

        // 主时钟停摆看门狗：主时钟取自音频设备游标，一旦音频侧异常
        // （设备未启动、启动锚点未捕获、引擎停摆），GetCurrentMasterTime 会恒定不前进 →
        // 本循环永久自旋 → 画面永久冻结（现象：present=1 dropped=0，整片只上屏首帧）。
        // 故记录进入时的主时钟与墙钟：墙钟已过 StallGraceMs 而主时钟前进不足 StallEpsilonMs，
        // 即判定主时钟停摆，放弃等待直接呈现——降级为「按解码节奏出帧」，宁可轻微不同步也绝不冻结。
        // 宽限期粘滞：首次判定用 500ms（足够长，绝不误伤正常抖动）；一旦确认停摆则降到 50ms，
        // 否则每帧空等 500ms 会把画面压到 2fps。主时钟恢复推进时自动复位回正常模式。
        double graceMs = _masterClockStalled ? 50.0 : 500.0;
        const double StallEpsilonMs = 20.0;
        var entryMaster = _synchronizer.GetCurrentMasterTime();
        long entryQpc = System.Diagnostics.Stopwatch.GetTimestamp();

        // 主体：睡到目标前 ~1.5ms，每轮一次平滑时钟读取。Stop() 取消时立即返回，
        // 避免专用实时线程在退出/暂停时仍按帧时睡眠（round-21 引入的回归：原 Thread.Sleep 不感知取消）。
        while (true)
        {
            if (ct.IsCancellationRequested) return;
            var master = _synchronizer.GetCurrentMasterTime();
            var remaining = (frameTimestamp - threshold - master).TotalMilliseconds;
            if (remaining <= tailMs)
            {
                // 音频时钟≈实时，故用本地高精度时钟反推目标 QPC，尾部纯自旋收口（零 COM 调用）。
                targetQpc = System.Diagnostics.Stopwatch.GetTimestamp()
                            + (long)(remaining * 10_000); // 100ns ticks
                break;
            }

            double waitedMs = System.Diagnostics.Stopwatch.GetElapsedTime(entryQpc).TotalMilliseconds;
            double advancedMs = (master - entryMaster).TotalMilliseconds;

            if (advancedMs >= StallEpsilonMs && _masterClockStalled)
            {
                _masterClockStalled = false;
                graceMs = 500.0;
                _logger.LogInformation("[SYNC] 主时钟已恢复推进，同步等待回到正常模式。");
            }

            if (waitedMs > graceMs && advancedMs < StallEpsilonMs)
            {
                if (!_masterClockStalled)
                {
                    _masterClockStalled = true;
                    _logger.LogWarning(
                        "[SYNC] 主时钟停摆：等待 {Waited:F0}ms 内主时钟仅前进 {Advanced:F1}ms（master={Master}），" +
                        "放弃等待直接呈现帧 PTS={Pts}，避免画面永久冻结。后续帧改用 50ms 宽限期降级出帧。",
                        waitedMs, advancedMs, master, frameTimestamp);
                }
                return;   // 立即呈现（跳过尾部自旋收口）
            }

            // 睡眠时长按宽限期截断：主时钟停摆时 remaining 可能高达整片时长（如 PTS=30s、master=0
            // ⇒ 睡 30 秒），必须在宽限期内醒来复查，否则看门狗永无机会触发。
            Thread.Sleep((int)Math.Ceiling(Math.Min(remaining - tailMs, graceMs)));
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

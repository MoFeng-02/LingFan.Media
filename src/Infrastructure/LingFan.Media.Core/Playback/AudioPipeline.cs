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
/// <para>Pause/Stop 与提交热路径为同步；StartAsync（首启含 preroll 等待）、
/// ResetForSeekAsync/ResumeAfterSeekAsync（定位复位与恢复）为真异步 Task；
/// DisposeAsync 为契约兼容，内部走同步清理路径。</para>
/// <para>关键区别于 VideoPipeline：音频不丢帧（丢帧会导致声音断裂）。</para>
/// <para>每次音频帧提交后通过 Synchronizer.OnAudioFrameSubmitted 更新主时钟。</para>
/// <para><b>分部划分</b>：Loop 分部管消费循环与供给诊断；Submit/Seek 分部管提交与定位复位。本文件为生命周期编排。</para>
/// </remarks>
public sealed partial class AudioPipeline : IAsyncDisposable, IDisposable
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
    private volatile bool _isResuming;   // Pause 后置位；下次 Start 走恢复路径（立即 Resume 而非 preroll）
    private TaskCompletionSource<bool>? _pauseAckTcs;
    // 自然完成标志：仅当管线因"流末耗尽"（包源关闭）正常退出时置位，
    // 用于区分 Ended（自然结束）与取消退出，避免停止/释放时误触发 Completed 事件。
    // 由消费循环线程写、由定位复位线程清，两个方向都跨线程，故必须是 volatile：
    // 否则复位方可能读到旧值，或旧循环的置位对复位方不可见而留下假完成。
    private volatile bool _completedNaturally;

    // 运行代号：每次拉起消费循环自增。定位复位后旧循环可能仍在收尾，其收尾动作与完成上报
    // 都必须只对当轮生效，否则会把新一轮的运行状态改坏。
    // 音频只有一条消费循环（提交与解码都在同一个循环里），循环是否存活由 _isRunning 单一表达，
    // 因此不需要视频侧那种独立的存活标志；代号只用于阻断旧循环的收尾副作用。
    private int _runId;

    // 上一轮消费循环是否已被定位复位作废。复位在有界等待超时的情况下会带着「旧循环仍在收尾」
    // 返回，此后仅凭 _pipelineTask 的完成状态无法判断它是否已被取代：一条既未完成又已作废的
    // 循环仍会在稍后退出，此时只解除暂停就会得到一条不再喂帧的管线。故把「已作废」显式记录下来，
    // ResumeAfterSeekAsync 据此决定是解暂停还是重拉循环；StartAsync 起新循环时清除。
    private volatile bool _loopSuperseded;

    // 复位时有界等待旧消费循环退出的上限。超时只记日志并继续：定位不能被卡住的循环拖住，
    // 旧循环余下的收尾由运行代号阻断。
    private static readonly TimeSpan LoopStopTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 解码锁：确保 DecodeAsync 与 Reset 不会并发执行。
    /// PipelineLoop 在解码+入队期间持有锁，定位复位 在 Clear+Reset 前获取锁。
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
    /// <param name="framePool">帧对象池。</param>
    /// <param name="transforms">音频变换链。
    /// 中立 BCL 委托，由 Audio 模块把 <c>VolumeControl</c>/<c>IAudioEffect</c>/<c>AudioMixer</c> 转换而来，Core 不依赖 Audio 模块。</param>
    /// <param name="effectReset">音频效果状态重置委托。
    /// 中立 BCL 委托（<see cref="Action"/>），由 Audio 模块把各 <c>IAudioEffect.Reset</c> 合并而来，Core 不依赖 Audio 模块。
    /// 在 Seek/定位复位 的解码锁内调用，清除有状态效果（均衡器 biquad / 混响延迟线 / 压缩器包络）的跨位置残留。</param>
    /// <param name="audioDataSink">音频采样数据观测回调（可为 null）。每帧提交给输出前在管线线程同步调用；
    /// 回调仅可只读借用帧（不得 Dispose、不得跨线程持有），用于接线上层 <see cref="IMediaPlayer.AudioDataAvailable"/> 事件。</param>
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
    /// 归还帧到池（若池可用）或 Dispose。
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
    /// 启动或恢复管线（异步）。
    /// StartAsync() 同时处理首次启动和恢复：首启走 BeginStreamingAsync preroll（写满设备缓冲后自动出声）；
    /// 恢复（Pause 后再启动）立即调用 audioOutput.Resume()。
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning)
        {
            _isPaused = false;
            _output.Resume();
            return;
        }

        _isRunning = true;
        _isPaused = false;
        // 重播正确性：若本次 Start 是从自然排干(Ended)态重启，必须清零自然完成标志，
        // 否则残留 true 会在后续 Stop/Dispose 的 finally 中误触发 Completed → 错误发出 Ended。
        _completedNaturally = false;

        if (_cts.IsCancellationRequested)
        {
            _cts = new CancellationTokenSource();
        }

        // 新运行代号：旧循环若仍在收尾，其收尾动作不会影响本轮。
        Interlocked.Increment(ref _runId);
        // 本轮循环即当前循环，复位留下的「已作废」标记到此为止。
        _loopSuperseded = false;

        // 起播无静默窗：非恢复播放走 preroll —— WASAPI 内部 Stop→Reset 并 arm，
        // 提交循环写满设备缓冲后自动 Start，引擎抓取真实数据而非静音。
        // 恢复播放（Pause→Resume）不走 preroll，立即 Resume。
        bool resuming = _isResuming;
        _isResuming = false;
        if (resuming)
        {
            _output.Resume();
            Func<Task> loop = () => PipelineLoop();
            _pipelineTask = Task.Factory.StartNew(loop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }
        else
        {
            var prime = _output.BeginStreamingAsync(_cts.Token);
            Func<Task> loop = () => PipelineLoop();
            _pipelineTask = Task.Factory.StartNew(loop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            await prime;
        }
    }

    /// <summary>
    /// 暂停管线和音频输出。
    /// </summary>
    public void Pause()
    {
        _isPaused = true;
        _isResuming = true;
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

    // 前瞻窗口（预解码帧数）：让提交阶段能成批提交，折叠逐帧 STA 跨线程往返的固定开销（修复听感卡顿/掉速）。
    private const int PrerollFrames = 16;

    // 小量子连续提交：每次最多提交 ~MaxSubmitChunkMs 的音频，使渲染线程的 submittedSamples 连续推进
    // （单批灌入全部帧会长时间阻塞提交、令 submittedSamples 冻结，诊断仪会误报"音频间隙/stall"），
    // 并把设备前置缓冲收窄到 ~缓冲时长（更贴近实时）。
    private const int MaxSubmitChunkMs = 40;   // 单批提交上限（毫秒）；远小于诊断仪 100ms stall 阈值，留足余量
    private const int MinChunkSamples = 1024;  // 单批下限（采样数，≈23ms@44.1k/21ms@48k），避免过小批导致提交开销占比上升
    private int _submitChunkSamples;           // 懒初始化：首次提交时按帧采样率换算（采样率跨流可能变化，故每流首帧确定）

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

        // 释放信号量前确保管线线程已退出，避免 SemaphoreSlim.Dispose 与并发 WaitAsync/Release 的未定义行为
        EnsureThreadStopped();

        _decodeLock.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// 释放解码锁前停止并 join 管线线程。
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

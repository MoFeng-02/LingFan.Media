using System;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

/// <summary>
/// 视频处理管线。从 BufferManager 包队列读取视频包 → Decoder 解码 → FrameQueue →
/// Synchronizer 同步 → Renderer 呈现。
/// </summary>
/// <remarks>
/// <para>Start/Pause/Stop/ResumeAfterSeek 与呈现热路径为同步；ResetForSeekAsync（定位复位）、
/// WaitForPrerollAsync/WaitForFirstFramePresentedAsync（启动编排等待）为真异步 Task；
/// DisposeAsync 为契约兼容，内部走同步清理路径。</para>
/// <para>Start() 同时处理首次启动和恢复暂停。Stop() 只调 cts.Cancel（fire-and-forget）。</para>
/// <para>线程 join（5s 超时）在 MediaPlayer.DisposeAsync 第1步中处理，不在 Stop() 中。</para>
/// <para>丢帧和 Present 后的帧通过 ReturnFrame 归还到 FramePool或 Dispose。</para>
/// </remarks>
public sealed partial class VideoPipeline : IAsyncDisposable, IDisposable
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
    // 运行代号：每次启动消费循环自增。循环线程在收尾时用它判断自己是否仍属于「当前这一轮」，
    // 从而让定位复位重启后的新循环不被上一轮的迟到收尾改写状态（否则新循环会立刻判为已结束）。
    private int _runId;
    // 解码消费循环存活标志：仅由当轮解码循环在自己的收尾处置 false。
    // 恢复呈现时必须以它判断是否需要重拉循环——只看 _isRunning 会漏掉
    // 「解码循环已退出、呈现循环仍在空转」的形态，那种形态下帧队列永远不会再被填充。
    private volatile bool _decodeLoopAlive;
    // 解码循环重建是否正在进行：仅用于「重建链已在跑」时阻止再起第二条重建链。
    // 重建期间 _decodeLoopAlive 保持 false——新的解码循环尚未真正启动，不能对外自述健康，
    // 否则上层（恢复呈现、启动）会误判解码仍健在，只解除暂停而不再重拉循环。
    private volatile bool _decodeRestarting;
    // 解码重建序号：重建链可能被后来的重建取代（旧链在新循环收尾后才走到 finally），
    // 只有仍是最新一次重建的链才允许清除 _decodeRestarting，避免旧链把新一轮的标记抹掉。
    private int _decodeRestartId;
    // 定位复位时等待消费循环退出的上限。超时后照常继续复位：运行代号已作废旧轮，
    // 旧线程即使残留也不会再改动新一轮状态。
    private static readonly TimeSpan LoopStopTimeout = TimeSpan.FromMilliseconds(500);

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
    private TaskCompletionSource<bool>? _pauseAckTcs;
    // 重播无缝衔接：**首帧门控 + 视频预滚动**。
    // 重播路径下 SeekAsync 会先 Stop 再重启 demuxer 读取线程、并 Reset 解码器，视频首帧要较长时间才产出；
    // 若音频设备（= 主时钟源 GetPlaybackPositionDirect）先从 0ms 起跑，
    // 起始空窗内的帧会被 Synchronizer 全判 Drop，屏幕停在上一次播放的末帧后突跳。
    // 因此 MediaPipelineHost.StartAsync 的编排为「视频先起 → 等预滚动 → 放行视频呈现（主时钟≈0 瞬态）
    // → 再启动音频设备」，把首帧预热**吸收在 PlayAsync 内部**（主时钟尚未起跑，不计入播放时间线），
    // 音频就绪后首帧与音频同刻出现。首播时包队列已在 OpenAsync 阶段暖好，预滚动瞬时满足。
    private volatile bool _audioReadySignaled;   // 由 MediaPipelineHost 在音频设备启动后置位（跨线程写）
    private volatile bool _audioGateOpened;      // 呈现线程已通过门控（Start 复位，之后仅呈现线程读写）
    private volatile bool _firstFramePresented;  // 首帧已真正上屏（ProcessFrame 首次 Present 后置位，供 A/V 启动对齐）
    private TaskCompletionSource<bool>? _firstFramePresentedTcs;  // 音频编排等待点（Start 重建）
    // 预滚动帧数下限：等帧队列预热到该深度再启动音频设备（吸收重播的重定位+解码开销）。
    //
    // 预滚动深度与前向缓冲目标对齐（= TargetDepth）：起播瞬间队列即满，
    // 具备抵抗启动期解码产出波动（启动期解码偏慢）的余量。深度过浅（如固定 2 帧）时
    // 呈现一开跑即吃光预滚动帧、解码来不及补，队列见底 → 呈现线程空等 + 帧 PTS 落后被判 Drop，
    // 启动期掉帧。代价为约 (TargetDepth−2)×帧间隔 的起播延迟，换来启动期不掉帧。
    private static readonly int VideoPrerollFrames = TargetDepth;
    // 预滚动等待上限：超时即放行启动音频，宁可轻微不同步也绝不卡住播放（解码异常/无视频帧时兜底）。
    private const int VideoPrerollTimeoutMs = 2000;
    // 门控等待上限：SignalAudioReady 因音频启动异常未被调用时的兜底，绝不让呈现线程永久阻塞。
    private const int AudioGateTimeoutMs = 3000;
    // 主时钟停摆降级标志（仅呈现线程读写，无需 volatile）：见 WaitUntilDue 的停摆看门狗。
    // 置位后同步等待改用 50ms 宽限期，避免每帧空等 500ms 把画面压到 2fps；主时钟恢复推进即复位。
    private bool _masterClockStalled;
    // 起播瞬态保持：门控放行后、音频设备起跑前，主时钟（音频游标）设计内冻结——这不是停摆。
    // 保持期内帧年线随当前 master 滑行重算，帧等到 master 追上 PTS 才呈现（首帧海报不前导、
    // 后续帧不提前）；观测到 master 首次真实推进即结束保持，回到 PTS 同步。消除「音频冷启动
    // 窗口被停摆看门狗降级提前出帧 → 音频起跑后画面冻结追同步」的起播不同步。
    private bool _masterClockEverAdvanced;
    // 保持预算：音频启动失败且时钟无任何回退源时的兜底（超限进入停摆降级保底出帧）。
    // 须大于最慢的音频设备冷启动（含设备激活/格式协商，可达数秒）。
    private const int StartupClockHoldTimeoutMs = 8000;
    // 停摆看门狗跨调用锚点（仅呈现线程读写）：master 推进 ≥ 墙钟 50% 即重锚；长期停滞时
    // 跨调用累计等待，超过宽限期触发降级出帧。
    private long _stallWatchQpc;
    private TimeSpan _stallWatchMaster;
    private bool _stallWatchAnchored;

    // 主时钟可见性（快照日志）：让「主时钟是否 1× 推进」一眼可见，
    // 为同步类问题（含设备 GetPosition 半冻结类异常）提供第一现场证据，取代间接推断。
    private long _pacingSnapshotQpc;
    // 每帧 Present 耗时实测（快照上报均值）：定位呈现侧瓶颈（GC 抢占/渲染慢）的直接证据，
    // 取代间接推断。正常应 <10ms；若 ≈1000ms 级则是呈现线程被抢占（GC 风暴/优先级）。
    private double _lastPresentMs;
    private double _presentMsAccum;
    private int _presentedCount;
    // 快照窗口内的呈现帧数。**必须**独立于 _presentedCount（后者是播放以来累计值）：
    // [SYNC] 快照 的「呈现均耗时」= 窗口内耗时总和 / **窗口内帧数**。此前误把累计帧数当分母，
    // 平均值被系统性低估，直接导致瓶颈被误判到别处。
    private int _presentedInWindow;
    private long _droppedFrames;
    // 首段丢帧诊断：Start() 置 3，Drop 分支前 3 次打印帧 PTS 与 master 对照——
    // 帧时间戳上游退化（恒 0/停滞）vs 时钟源异常的直接判别证据。
    private int _dropDiagCount;
    // 呈现节拍指标（快照上报）：帧间隔与迟到量。呈现节拍抖动（迟到呈现⇒画面重复一拍）
    // 不进丢帧计数——缺少本组指标时，此类卡顿在 [SYNC] 快照中完全不可见。
    // 仅呈现线程读写（快照日志同线程），无需 Interlocked。
    private long _lastPresentQpc;
    private double _presentIntervalAccum;
    private double _presentIntervalMaxMs;
    private int _latePresentsInWindow;
    private double _lateMaxMs;
    // 迟到判定阈值：超过即计入迟到（自旋收口精度 + 调度抖动的合理余量）。
    private const double LatePresentThresholdMs = 4.0;
    // 呈现段尖峰告警阈值：sink 链单帧超过即限频告警（正常 ≈2ms，告警即定位线索）。
    private const double SinkSpikeLogThresholdMs = 15.0;
    // 等待过调告警阈值：醒点越过墙钟年线超过即限频告警（抢占/睡眠过冲的直接观测）。
    private const double WaitOvershootLogThresholdMs = 15.0;
    private long _lastOvershootLogQpc; // 过调告警节流（呈现线程）

    // 快照发布（呈现线程 → 看门狗线程）
    // [SYNC] 快照的日志写入（Console/Debug 双 provider → logcat/调试器）在部分设备上单次
    // 可达数十 ms；周期性落在呈现线程会制造它自己所度量的迟到帧。故窗口统计由呈现线程
    // 发布（纯内存引用替换，零 I/O），由冻结看门狗线程代为输出。
    private sealed record PacingWindowStats(
        int WindowFrames, double AvgPresentMs, double LastPresentMs,
        double AvgIntervalMs, double MaxIntervalMs, int LateFrames, double LateMaxMs,
        double QueueEmptyWaitMs, string GcDelta, double MasterClockRatio,
        double PtsSpacingAvgMs, double PtsSpacingMaxMs, long PublishedQpc);
    private PacingWindowStats? _publishedPacingStats;
    private long _lastEmittedStatsQpc;   // 看门狗线程：上次已输出的统计版本
    private double _lastSnapshotWriteMs; // 看门狗线程：上一次快照日志写入耗时（自测量）
    private double _queueEmptyWaitMsAccum; // 队空等待累积（呈现线程，解码供帧不及的直接观测）
    private long _lastSpikeLogQpc;         // 呈现段尖峰告警节流（呈现线程）
    private int _lastGcGen0, _lastGcGen1, _lastGcGen2; // GC 代际计数基线（呈现线程，窗口差值）
    // 主时钟速率对表：窗口内 master 增量 / 墙钟增量（两者都用精确 Stopwatch 测量）。
    // 比值 ≈1.0 = 音频时钟健康；持续 <1 = 音频记账时钟慢（卡顿出在音频侧）。
    private double _lastPublishMaster;
    private long _lastPublishWallQpc;
    private bool _hasLastPublish;
    // 视频 PTS 间距（呈现线程逐帧统计）：区分「内容帧率/PTS 抖动」与「管线 pacing 问题」——
    // 若 PTS 间距本身 ≈ 间隔均值，则呈现完全跟随内容，卡顿源于素材而非管线。
    private TimeSpan _lastPresentPts;
    private bool _hasLastPresentPts;
    private double _ptsSpacingAccum;
    private double _ptsSpacingMaxMs;
    private int _ptsSpacingCount;
    // 呈现提前量的手动微调（LINGFAN_SYNC_LEAD_MS，构造期捕获）。
    // 渲染器的 PresentationLatency 可在运行期自适应更新，每帧叠加本偏移后同步给 Synchronizer。
    private readonly TimeSpan _manualLeadOffset;
    // A/V 同步诊断节流字段（仅 LINGFAN_SYNC_DIAG=1 时读取，生产路径恒 0 不影响任何逻辑）。
    private long _lastSyncDiagTicks;
    private int _presentCount;

    // 冻结看门狗（独立诊断线程）
    // 呈现循环的全部等待点（首帧门控/WaitUntilDue/空队列自旋）均有打卡或自带看门狗；
    // 若整条管线静默（无 [SYNC] 快照、无停摆日志），即卡死在某个不打日志的调用里。
    // 本看门狗在管线线程之外每 2s 输出：循环心跳年龄 / 主时钟 / 队列 / sink 在途深度——
    // 冻结时一眼定位「循环死 vs 时钟死 vs sink 链死」。常开，代价一条日志/2s。
    private Task? _freezeWatchdogTask;
    // 「已有看门狗在跑」的判据：存活标记 + 它绑定的令牌源。
    // 只看任务引用不可靠（包装任务在首个 await 处即完成），只看引用非空也不可靠
    // （取消退出后引用仍在）；带上令牌源是为了取消换代后能立即重建新一轮，
    // 而不必等旧循环自己收尾。
    private volatile bool _freezeWatchdogRunning;
    private CancellationTokenSource? _freezeWatchdogCts;
    private long _loopHeartbeatQpc;
    private long _sinkEnterQpc;
    private long _sinkExitQpc;
    private int _sinkInFlight;

    // 双线程阶段码（冻结看门狗配套）
    // 心跳只证明「线程不在循环入口」，阶段码进一步证明「卡在循环内哪一步」：
    // 两个循环各自的阻塞调用（native 解码 / sink 链 / 帧归还）在心跳停更后由阶段码定位。
    private long _decodeHeartbeatQpc;
    private volatile int _decodePhase;
    private volatile int _pipelinePhase;
    // 解码循环阶段：见 DecodeLoop 各赋值点（1=读包等待 2=解码锁 3=DecodeAsync 4=入队）。
    private const int DecodePhaseReadPacket = 1;
    private const int DecodePhaseLock = 2;
    private const int DecodePhaseDecode = 3;
    private const int DecodePhaseEnqueue = 4;
    // 呈现循环阶段：见 PipelineLoop/ProcessFrameCore/ReturnFrame 各赋值点（1=时钟等待 2=sink 链 3=帧归还）。
    private const int PipelinePhaseWait = 1;
    private const int PipelinePhaseSink = 2;
    private const int PipelinePhaseReturn = 3;

    /// <summary>
    /// 解码锁：确保 DecodeAsync 与 Reset 不会并发执行。
    /// PipelineLoop 在解码+入队期间持有锁，定位复位 在 Clear+Reset 前获取锁。
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
    /// <param name="processorReset">视频后处理状态重置委托（中立委托，可为 null）。由 Video 模块把各 <c>IVideoProcessor.Reset</c> 合并而来，
    /// 在 Seek/定位复位 的解码锁内调用，释放有状态处理器（帧率转换保留帧等）的跨位置残留。</param>
    /// <param name="videoFrameSink">视频帧出口回调（可为 null）。呈现路径的唯一出口：同步判定放行后帧交给本回调，
    /// 路由（无头计算 / Skia 软渲染 / D3D11 零拷贝 GPU 呈现）在回调内部完成；为 null 时帧静默归池、不呈现（测试/无 UI 调用方）。</param>
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
        _manualLeadOffset = TimeSpan.FromMilliseconds(manualLeadMs);
        _synchronizer.PresentationLatency = _renderer.PresentationLatency + _manualLeadOffset;
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
        // 泄漏对账（Trace 级）：ReturnFrame 调用计数。帧资源泄漏定位用——
        // 若 [RET-COUNT] 与已呈数同步增长而 [AHB-LEAK] live 仍涨，则断点在池 reset 之后（池未执行 reset）。
        int ret = System.Threading.Interlocked.Increment(ref _returnCount);
        if ((ret % 128) == 1)
            _logger.LogTrace(
                "[RET-COUNT] ReturnFrame 第 {N} 次 res={Res} 池三分支(disp/full/enq)={D}/{F}/{E}",
                ret, frame.Resource?.GetType().Name ?? "null",
                System.Threading.Volatile.Read(ref LingFan.Media.Core.FramePool<VideoFrame>._retDisposedBranch),
                System.Threading.Volatile.Read(ref LingFan.Media.Core.FramePool<VideoFrame>._retFullBranch),
                System.Threading.Volatile.Read(ref LingFan.Media.Core.FramePool<VideoFrame>._retEnqueueBranch));
        if (_framePool != null)
            _framePool.Return(frame);
        else
            frame.Dispose();
    }

    // 泄漏对账计数（ReturnFrame 调用次数）。
    private int _returnCount;

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
    public void Start() => StartCore(resetStartupGates: true);

    /// <summary>
    /// 启动消费循环（呈现循环 + 解码循环）。
    /// </summary>
    /// <param name="resetStartupGates">
    /// 是否复位起播门控（音频就绪信号、首帧门控、首帧上屏信号）。全新播放与重播必须复位；
    /// 定位后的重启不复位——门控在播放中已经放行，重新关上会让画面空等音频就绪数秒。
    /// </param>
    /// <remarks>幂等：循环仍在运行时只解除暂停，不重复创建线程。</remarks>
    private void StartCore(bool resetStartupGates)
    {
        // 两条消费循环都存活才算「已在运行」。只有呈现循环存活时不能整体早退：那时解码循环
        // 已退出、帧队列不会再被填充，整体早退会让管线永久停在零帧；也不能重拉呈现循环，
        // 帧队列以单读模式创建，第二条呈现线程会破坏该契约。
        if (_isRunning && _decodeLoopAlive)
        {
            _isPaused = false;
            return;
        }

        if (_isRunning)
        {
            RestartDecodeLoopOnly();
            return;
        }

        _isRunning = true;
        _isPaused = false;
        if (resetStartupGates)
        {
            // 首帧门控复位（仅全新启动/重播路径）：恢复暂停时上方已早退，门控保持已开启不受影响。
            _audioReadySignaled = false;
            _audioGateOpened = false;
            // 首帧上屏信号复位：每次全新启动/重播重建 TCS（TCS 不可重置），
            // 供 MediaPipelineHost 在启动音频前等待「视频首帧已真正上屏」。
            _firstFramePresented = false;
            _firstFramePresentedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _decodeDone = false;
        _eosReached = false;
        // 重播正确性：若本次 Start 是从自然排干(Ended)态重启，必须清零自然完成标志，
        // 否则残留 true 会在后续 Stop/Dispose 的 finally 中误触发 Completed → 错误发出 Ended。
        _completedNaturally = false;

        // A/V 同步诊断：每轮开播重置计数，重新抓取起始帧偏移（重播/恢复也重置）。
        _presentCount = 0;
        _lastSyncDiagTicks = 0;
        _dropDiagCount = 3;
        // 主时钟快照计数复位（[SYNC] 快照）
        _presentedCount = 0;
        _presentedInWindow = 0;
        _pacingSnapshotQpc = 0;
        _presentMsAccum = 0;
        _lastPresentMs = 0;
        _masterClockStalled = false;
        // 起播保持按次启动重建：首播与重播统一「等主时钟起跑再推进」，消除重播音频设备
        // 重启窗口的同款前导-冻结。停摆看门狗锚点必须一并重锚——跨次启动的旧锚点会让
        // waitedMs 携带上一轮的累计值，保持预算在重播第一帧就误超时直落降级。
        _masterClockEverAdvanced = false;
        _stallWatchAnchored = false;
        _stallWatchQpc = 0;
        _stallWatchMaster = default;

        // 重新创建 CTS（如果旧的已取消）
        if (_cts.IsCancellationRequested)
        {
            _cts = new CancellationTokenSource();
        }

        // 新运行代号：上一轮循环（若还有未收尾的线程）从此不再被视为当前这一轮。
        Interlocked.Increment(ref _runId);
        // 本次整体启动会自建解码循环，任何仍在等待的重建链都必须作废：
        // 否则它稍后也会启动一条解码循环，同一轮出现两条解码线程。
        Interlocked.Increment(ref _decodeRestartId);
        _decodeRestarting = false;

        // 双线程解耦：解码生产者 + 呈现消费者。两者经 FrameQueue 通信，解码延迟不阻塞呈现节拍。
        // 呈现消费者用 LongRunning 专用实时线程：避免 async 续体被线程池调度延迟（±50ms 抖动脉冲），
        // 且线程全程保持 Highest 优先级（不被 OS 抢占）。解码生产者保持 Task.Run（缓冲生产者，延迟不影响呈现节拍）。
        _pipelineTask = Task.Factory.StartNew(PipelineLoop, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _decodeLoopAlive = true;
        _decodeTask = Task.Run(DecodeLoop);
        StartFreezeWatchdog();
    }

    /// <summary>
    /// 放行呈现循环的首帧门控。由 <see cref="MediaPipelineHost.StartAsync"/> 在视频预滚动后、
    /// **音频管线启动前**调用（首帧在主时钟 ≈0 的起跑瞬态同刻呈现，与音频无缝对齐）；
    /// 放行后主时钟仍冻结的窗口由 <see cref="WaitUntilDue"/> 的起播保持兜住。
    /// </summary>
    /// <remarks>
    /// 无音频轨时也必须调用（否则呈现线程会空等到 <see cref="AudioGateTimeoutMs"/> 兜底超时）。
    /// 幂等：重复调用无副作用；<see cref="Start"/> 会在下一轮全新启动时复位。
    /// </remarks>
    public void SignalAudioReady() => _audioReadySignaled = true;

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

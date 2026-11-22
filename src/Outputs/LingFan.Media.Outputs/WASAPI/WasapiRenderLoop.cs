using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// WASAPI 渲染线程（STA 独占）。Phase 1 从 <see cref="WasapiOutput"/> 提取：
/// 把全部 WASAPI COM 调用收进一个常驻 STA 线程，消除逐帧 <c>RunOnSta</c> 跨线程封送税，
/// 为 Phase 2 的生产者/消费者解耦（<c>AudioSampleRing</c>）奠定线程模型基础。
/// </summary>
/// <remarks>
/// <para><b>线程模型</b>：构造后由 <see cref="InitializeAsync"/> 启动唯一 STA 线程（COINIT_APARTMENTTHREADED）。
/// 该线程在 <c>CoInitializeEx</c> 之后进入渲染循环：消费控制消息（Initialize/Pause/...）与音频帧，
/// 所有 COM 调用（CoCreateInstance/GetMixFormat/Initialize/GetBuffer/ReleaseBuffer/GetPosition）均在同一线程。
/// WASAPI 要求 IAudioClient 在 STA 公寓创建与使用，MTA 下 GetMixFormat/Initialize 会触发原生访问违规（ACCESS_VIOLATION），
/// 故 STA 线程唯一性不可破坏。</para>
/// <para><b>异步策略</b>（与提取前一致，未改变契约语义）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，内部启动 STA 线程 + 在渲染线程执行 InitializeCore，返回 <see cref="Task.CompletedTask"/>。
/// CoInitializeEx + COM 设备枚举均为同步 COM 调用，无 I/O 可 await，非伪异步。</item>
/// <item><see cref="Initialize"/>：同步（sync 分类），在渲染线程执行 IAudioClient + IAudioRenderClient 创建 +
/// 格式协商（GetMixFormat / IsFormatSupported）+ 事件驱动初始化（SetEventHandle）。</item>
/// <item><see cref="Submit"/>/<see cref="SubmitBatch"/>：同步边界（native 分类），将帧交给渲染线程消费（阻塞等待该帧写入完成，
/// 故调用方在 Submit 返回后可安全归还帧所有权——与行为一致）；缓冲满时由渲染线程的 COM 背压等待，
/// 不再由调用方跨线程往返。</item>
/// <item><see cref="Pause"/>/<see cref="Resume"/>/<see cref="Flush"/>：同步（sync 分类），渲染线程执行 IAudioClient.Stop/Start/Reset。</item>
/// <item><see cref="GetPlaybackPosition"/>：同步（sync 分类），渲染线程执行 IAudioClock.GetPosition。</item>
/// <item><see cref="Dispose"/>：同步快速释放（sync 分类），向渲染线程投递 Shutdown 消息触发 COM 释放 + CoUninitialize，释放事件句柄。</item>
/// <item><see cref="DisposeAsync"/>：接口契约，委托 <see cref="Dispose"/> + <see cref="ValueTask.CompletedTask"/>。非伪异步。</item>
/// </list>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，采用原始 vtable P/Invoke（ComVTable 委托封送），不使用 [ComImport]/RCW，NativeAOT 兼容。</para>
/// <para><b>资源所有权</b>：IMMDeviceEnumerator/IMMDevice/IAudioClient/IAudioRenderClient/ISimpleAudioVolume/IAudioClock
/// 的原生指针均由本类持有（Session 级），Dispose 时通过 Marshal.Release(IntPtr) 逆序释放。
/// 事件句柄（EventWaitHandle）由本类创建并持有，Dispose 时释放。</para>
/// <para><b>Submit 所有权</b>：语义保持——Submit 不接管帧所有权，不 Dispose 帧。调用方（AudioPipeline）负责 Return 到 FramePool 或 Dispose。</para>
/// <para><b>增强</b>：独占模式（IsFormatSupported 协商 + 错误处理）、事件驱动（SetEventHandle + WaitOne 替代 Sleep 轮询）、
/// 多格式直出（GetMixFormat 检测 + S16/S32/F32 直出）。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WasapiRenderLoop
{
    private readonly WasapiOptions _options;
    private readonly ILogger<WasapiOutput> _logger;
    private readonly bool _exclusiveMode;
    private volatile bool _eventDrivenMode;  // 非 readonly：SetEventHandle 失败时回退到轮询；volatile 确保跨线程可见性

    // COM 对象（原生指针，Session 级，Dispose 时 Marshal.Release）
    private IntPtr _enumeratorPtr;
    private IntPtr _devicePtr;
    private IntPtr _audioClientPtr;
    private IntPtr _renderClientPtr;
    private IntPtr _simpleVolumePtr;
    private IntPtr _audioClockPtr;

    // 缓存的 vtable 委托（AOT 兼容：避免 [ComImport]/RCW）
    private IMMDeviceEnumerator_GetDefaultAudioEndpoint? _enumeratorGetDefault;
    private IMMDevice_Activate? _deviceActivate;
    private IAudioClient_Initialize? _audioClientInitialize;
    private IAudioClient_GetBufferSize? _audioClientGetBufferSize;
    private IAudioClient_GetStreamLatency? _audioClientGetStreamLatency;
    private IAudioClient_GetCurrentPadding? _audioClientGetCurrentPadding;
    private IAudioClient_IsFormatSupported? _audioClientIsFormatSupported;
    private IAudioClient_GetMixFormat? _audioClientGetMixFormat;
    private IAudioClient_Start? _audioClientStart;
    private IAudioClient_Stop? _audioClientStop;
    private IAudioClient_Reset? _audioClientReset;
    private IAudioClient_SetEventHandle? _audioClientSetEventHandle;
    private IAudioClient_GetService? _audioClientGetService;
    private IAudioRenderClient_GetBuffer? _renderClientGetBuffer;
    private IAudioRenderClient_ReleaseBuffer? _renderClientReleaseBuffer;
    private ISimpleAudioVolume_SetMasterVolume? _simpleVolumeSetMasterVolume;
    private IAudioClock_GetPosition? _audioClockGetPosition;
    private IAudioClock_GetFrequency? _audioClockGetFrequency;

    // 状态
    private bool _initialized;
    private bool _disposed;
    private int _bufferSize;      // WASAPI 缓冲区大小（帧数）
    private bool _prerollPending;             // BeginStreamingAsync 已 arm（Stop→Reset），等待 WriteFrame 预填达标后自动 Start
    private bool _prerollStarted;             // 已自动 Start（防重复触发）
    private TaskCompletionSource<bool>? _primeTcs;
    private const int PrimeTimeoutMs = 600;  // 无音频轨/极短片段兜底：超时强制 Start，防 PlayAsync 挂起
    // 分段写入背压：缓冲满时等待引擎腾出空间的上限。引擎周期通常 ~10ms，200ms 内无信号即判定设备停摆
    // （丢弃该帧剩余部分并计数），既保证正常播放全程无丢样，又不会让渲染线程上的控制消息被长时间阻塞。
    private const int BufferWaitTimeoutMs = 200;
    private const int BufferPollIntervalMs = 5;   // 轮询模式（设备不支持事件驱动）下缓冲满的重试间隔
    private double _streamLatencySec; // IAudioClient.GetStreamLatency() 返回的「提交→可闻」延迟（秒），用于主时钟校准
    // 音画同步校准（以墙钟为锚，消除设备游标偏移）：
    // 共享模式 IAudioClock::GetPosition 的 devicePosition 含音频引擎「抓取领先」（Start 后瞬间预取
    // 进系统混音缓冲），该领先对 GetCurrentPadding（仅本 IAudioClient 设备缓冲，≤bufferSize≈100ms）
    // 与 GetStreamLatency（本地返回 0）均不可见，故直接减锚点/填充/延迟整段无效。
    // 改为以墙钟为锚：起播 >100ms 后锁定 bias = rawSec - wallElapsed（引擎领先+常偏），
    // 主时钟减此值即得真实可闻位置（≈墙钟，与视频 PTS 同源）。三者跨线程（渲染线程写、时钟线程读）均用 Volatile 访问。
    private System.Diagnostics.Stopwatch? _startStopwatch;
    private double _calibratedBias;
    private bool _biasLatched;
    // 重播（Ended→Playing）主时钟武装标志：音频尚未为本遍播放 Start 前，GetPlaybackPositionDirect 须返回 0
    // （设备尚未起跑，此时读出的游标是上一遍播放残留的陈旧值）。Resume/BeginStreaming 自动 Start 时经
    // CaptureStartAnchor 置 true；重播起点经 ResetPlaybackClock 置 false。缺此标志时，重播预滚动/门控窗口
    // 会沿用第一遍播放的 Start 锚点返回陈旧值，导致重播 PTS=0 首帧被错判落后、遭 Drop，画面冻结较长时间才切入。
    private volatile bool _clockArmed;
    // 注：LINGFAN_SYNC_LEAD_MS 已改由 VideoPipeline 作用在「呈现延迟」变量（治本），
    // 本音频时钟只暴露纯可闻位置，不再做任何前移补偿。
    private int _sampleRate;
    private int _channels;
    private float _volume = 1.0f;

    // 事件驱动模式
    private EventWaitHandle? _bufferEvent;

    // 设备是否处于 Running（引擎正在消费缓冲）。分段写入遇缓冲满时据此决策：
    // 引擎在跑 → 等一个缓冲事件后续写（正确背压）；引擎已停 → 立即丢弃剩余数据，
    // 绝不空等超时（否则 Pause 后的残留帧会逐帧空耗 BufferWaitTimeoutMs，拖慢控制消息响应）。
    private volatile bool _deviceRunning;

    // 诊断计数器（纯观察，不影响音频逻辑）：定位"听感卡顿/断续"成因。
    // submittedSamples = 实际成功写入 WASAPI 的累计采样帧数；droppedFrames = 缓冲满(available==0)丢弃的帧数。
    private long _submittedSamples;
    private int _submittedFrames;
    private long _droppedFrames;

    // 冷启动诊断（WASAPI_OPEN_DIAG=1 时启用）：拆开 InitializeAsync/Initialize 各 COM 步耗时，定位 OpenAsync 启动耗时来源。
    private readonly bool _openDiag = System.Environment.GetEnvironmentVariable("WASAPI_OPEN_DIAG") == "1";
    private Stopwatch? _initDiagSw;

    // 设备原生采样格式（Initialize 时检测，Submit 时用于直出判断）
    private SampleFormat _deviceSampleFormat = SampleFormat.F32;

    // 设备原生 mix format 的采样率/声道数（GetMixFormat 检测）。
    // 此前注释称"初始化 WAVEFORMATEX 必须用它而非解码器输出格式"，这是错的
    // ——那样会让 Submit 侧的解码器格式帧被按设备速率播出。现仅用于诊断日志与 AUTOCONVERTPCM 判断，
    // 实际初始化格式一律用客户端（解码器）采样率/声道数。详见 NegotiateSharedFormat 步骤 4。
    private int _mixSampleRate;
    private int _mixChannels;

    // IAudioClock 的设备频率（units/秒）。GetPosition 的返回值须除以它才是秒数，
    // 单位由设备定义（共享模式常见为字节/秒 = nSamplesPerSec * nBlockAlign），不等于采样率。
    // 0 表示不可用（GetFrequency 失败），此时 GetPlaybackPosition 回落到采样率换算并告警。
    private long _audioClockFrequency;

    // STA 渲染线程基础设施（Phase 1：常驻渲染循环，替代原"每次 RunOnSta 跨线程封送"）
    // 单一 STA 线程：CoInitializeEx(STA) → 处理控制消息与音频帧 → 关闭时释放 COM + CoUninitialize。
    // 所有 COM 调用都在该线程，调用方通过 ConcurrentQueue + AutoResetEvent 投递工作项并等待完成。
    private Thread? _thread;
    private readonly ConcurrentQueue<RenderItem> _queue = new();
    private readonly AutoResetEvent _workAvailable = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    // 关闭信号：Dispose 时置位，使 WaitForBufferSpaceOnce 立即放弃等待（残留帧在关闭期被跳过，不拖慢退出）
    private readonly ManualResetEventSlim _shutdownEvent = new(false);

    /// <summary>
    /// 初始化 <see cref="WasapiRenderLoop"/> 的新实例。
    /// </summary>
    /// <param name="options">WASAPI 配置选项。</param>
    /// <param name="logger">日志器（类型用 WasapiOutput，保持日志归属一致）。</param>
    internal WasapiRenderLoop(WasapiOptions options, ILogger<WasapiOutput> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _exclusiveMode = options.ExclusiveMode;
        _eventDrivenMode = options.EventDrivenMode;
    }

    private void LogOpen(string step)
    {
        if (_openDiag && _initDiagSw is not null)
            _logger.LogInformation("[WASAPI-OPEN] {Step} 累计 {Ms}ms", step, _initDiagSw.ElapsedMilliseconds);
    }

    /// <inheritdoc cref="WasapiOutput.InitializeAsync"/>
    /// <remarks>
    /// 接口契约：启动 STA 渲染线程 + 在渲染线程执行 InitializeCore（COM 设备枚举），均为同步 COM 调用，无 I/O 可 await。
    /// 同步执行后返回 <see cref="Task.CompletedTask"/>，非伪异步。
    /// </remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_openDiag) _initDiagSw = Stopwatch.StartNew();
        EnsureRenderThread();
        RunControl(InitializeCore);
        LogOpen("InitializeCore(设备枚举)完成");
        return Task.CompletedTask;
    }

    /// <inheritdoc cref="WasapiOutput.Initialize"/>
    public void Initialize(int sampleRate, int channels)
    {
        RunControl(() => InitializeImpl(sampleRate, channels));
    }

    /// <inheritdoc cref="WasapiOutput.Submit"/>
    /// <remarks>
    /// 语义保持：Submit 不接管帧所有权。将帧投递给渲染线程并阻塞等待其写入完成（COM 背压在渲染线程内），
    /// 故调用方在 Submit 返回后可安全归还帧；不跨越调用方线程做 COM 封送。
    /// </remarks>
    public void Submit(AudioFrame frame) => Submit(frame, CancellationToken.None);

    /// <summary>
    /// 提交单帧并阻塞等待渲染线程写入完成（可感知取消令牌）。
    /// </summary>
    public void Submit(AudioFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var item = new RenderItem(ItemKind.Frame, frame);
        _queue.Enqueue(item);
        _workAvailable.Set();
        item.Done.Wait(ct);
        if (item.Exception is not null)
            ExceptionDispatchInfo.Throw(item.Exception);
    }

    /// <summary>
    /// 批量提交：把多帧音频投递给渲染线程（在渲染线程内连续写入，消除逐帧跨线程往返开销）。
    /// 仅<b>背压超时</b>（<see cref="TimeoutException"/>）降级为丢弃该帧并继续后续帧；
    /// 参数类错误（声道/尺寸不匹配等 <see cref="ArgumentException"/>）原样冒泡、中断该批（契约要求，不得吞掉）。
    /// 不接管帧所有权。
    /// </summary>
    public void SubmitBatch(IEnumerable<AudioFrame> frames) => SubmitBatch(frames, CancellationToken.None);

    /// <summary>
    /// 批量提交（可感知取消令牌）。语义同 <see cref="SubmitBatch(IEnumerable{AudioFrame})"/>，
    /// 但 <paramref name="ct"/> 触发取消时立即放弃对渲染线程的阻塞等待，使调用方（音频管线）在 Stop/Dispose 时快速退出。
    /// </summary>
    public void SubmitBatch(IEnumerable<AudioFrame> frames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frames);

        // Phase 1：逐帧投递 + 等待（保持阻塞语义与异常可观测性）。Phase 2 将改由 AudioSampleRing 非阻塞接管所有权。
        var pending = new List<RenderItem>();
        foreach (var frame in frames)
        {
            if (frame is null) continue;
            var item = new RenderItem(ItemKind.Frame, frame);
            _queue.Enqueue(item);
            _workAvailable.Set();
            pending.Add(item);
        }

        foreach (var item in pending)
        {
            try
            {
                item.Done.Wait(ct);
            }
            catch (OperationCanceledException)
            {
                // 取消（Stop/Dispose）是正常退出路径：立即放弃剩余帧提交，使音频管线快速退出，
                // 不再冒泡成 fail 日志。
                break;
            }
            if (item.Exception is TimeoutException tex)
            {
                // 背压超时（缓冲区暂无可写空间）：仅丢弃该帧并继续后续帧。
                // 声道/尺寸不匹配等 ArgumentException 不在此吞掉，让其冒泡以暴露真实管线 bug。
                _logger.LogWarning("WASAPI 批量提交跳过单帧（背压超时）：{Msg}", tex.Message);
            }
            else if (item.Exception is not null)
            {
                ExceptionDispatchInfo.Throw(item.Exception);
            }
        }
    }

    /// <inheritdoc cref="WasapiOutput.Pause"/>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClientPtr == IntPtr.Zero) return;
        RunControl(() =>
        {
            int hr = _audioClientStop!(_audioClientPtr);
            _deviceRunning = false;
            // 修复：0x88890004 实际是 AUDCLNT_E_DEVICE_INVALIDATED（设备移除），非 AUDCLNT_E_NOT_INITIALIZED（0x88890001）。
            // 两者在 Stop() 上下文中均可安全忽略。
            if (hr < 0
                && hr != WasapiInterop.AUDCLNT_E_DEVICE_INVALIDATED
                && hr != WasapiInterop.AUDCLNT_E_NOT_INITIALIZED)
            {
                _logger.LogWarning("IAudioClient.Stop 失败：HRESULT=0x{HR:X8}", hr);
            }
        });
    }

    /// <inheritdoc cref="WasapiOutput.Resume"/>
    /// <remarks>
    /// 重播（Ended→Playing）健壮性：自然 Ended 时客户端仍 Running（尾音由设备自然放完，不主动 Stop），
    /// 重播若直接 Start 会得 <c>AUDCLNT_E_NOT_STOPPED</c>(0x88890005)。故先 Stop（幂等，已停止亦 S_OK）
    /// 再 Reset（丢弃残留未播缓冲，避免重播开头混入上一次尾音）最后 Start，形成确定的「停止→清空→启动」序列。
    /// 首次播放（从未 Start）与恢复暂停（Pause 已 Stop）场景下 Stop 均为幂等空操作，无副作用。
    /// </remarks>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClientPtr == IntPtr.Zero) return;
        RunControl(() =>
        {
            // 1. 停止（幂等，忽略返回：Stop 在任意合法态均安全）
            _audioClientStop!(_audioClientPtr);
            // 2. 清空残留缓冲（不改动客户端 Running/Stopped 状态，仅丢弃未播样本）
            _audioClientReset!(_audioClientPtr);
            // 3. 启动
            int hr = _audioClientStart!(_audioClientPtr);
            if (hr < 0)
                _logger.LogWarning("IAudioClient.Start 失败：HRESULT=0x{HR:X8}", hr);
            else
                _deviceRunning = true;

            // 恢复播放路径：立即启动，清除 preroll 状态（避免 WriteFrame 误触发自动启动）。
            _prerollStarted = true;
            _prerollPending = false;
            _primeTcs = null;

            // 捕获启动锚点。Start 后立刻读一次设备游标作为本流
            // 「已播放量」的零点，主时钟后续用 (devicePosition - 锚点) 得到本流真实播放秒数，
            // 消除共享模式 devicePosition 启动即带的任意累计偏移。重播时 Stop→Reset→Start
            // 会重新调用本方法，锚点随之刷新（Reset 后游标归零，新锚点≈旧锚点，差值连续）。
            CaptureStartAnchor();
        });
    }

    /// <inheritdoc cref="WasapiOutput.BeginStreamingAsync"/>
    /// <remarks>
    /// 解决起播静默窗：WASAPI 共享模式下，若在空缓冲上直接 Start，音频引擎会瞬间
    /// 抓取静音进系统混音缓冲，真实 PCM 排在其后才可闻 → 起播出现静默窗。
    /// 本方法只做 Stop→Reset 并 arm preroll（不 Start）；提交循环把真实 PCM 写满设备缓冲后，
    /// <see cref="WriteFrame"/> 在 padding 达标时自动 Start，引擎抓取的是真实数据 → 无静默窗。
    /// 返回的任务在自动 Start 后完成（或由超时兜底强制 Start，防无音频轨/极短片段挂起 PlayAsync）。
    /// </remarks>
    public ValueTask BeginStreamingAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // preroll 必须在 OpenAsync 之后、首帧之前 arm，即使设备尚未 Initialize
        // （如 FFmpeg+AAC 采样率延迟到首帧回填的场景）。原逻辑在 !_initialized 时 early-return 导致
        // _prerollPending 从未置位；渲染线程首帧惰性 Initialize 后才到 WriteFrame，此时已错过 arm →
        // 设备永不 Start → 音频饥饿死锁（日志: 需要 2048 帧/可用 314 帧，卡死退不出）。
        // 故 preroll 无条件 arm；仅 Stop/Reset 这类需有效客户端对象的操作在 _initialized 下执行
        // （_audioClientPtr 仅在 InitializeImpl 成功后才非 Zero，与 _initialized 等价）。
        if (_audioClientPtr != IntPtr.Zero)
        {
            RunControl(() =>
            {
                _audioClientStop!(_audioClientPtr);
                _audioClientReset!(_audioClientPtr);
                _deviceRunning = false;
            });
        }
        _prerollStarted = false;
        _prerollPending = true;
        _primeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return new ValueTask(WaitForPrimeAsync(ct));
    }

    private async Task WaitForPrimeAsync(CancellationToken ct)
    {
        if (_primeTcs == null) return;
        var tcs = _primeTcs;
        var timeout = Task.Delay(PrimeTimeoutMs, ct);
        var completed = await Task.WhenAny(tcs.Task, timeout);
        if (completed != tcs.Task)
        {
            // 超时兜底：强制启动，避免 PlayAsync 在"无音频轨/极短片段"场景挂起。
            // 守卫：惰性初始化场景下 600ms 兜底可能在设备尚未 Initialize 前触发，
            // 此时 _audioClientStart 委托为 null，调用会 NRE；仅在 _initialized 下强制 Start。
            RunControl(() =>
            {
                if (_prerollPending && !_prerollStarted && _initialized && _audioClientPtr != IntPtr.Zero)
                {
                    int hr = _audioClientStart!(_audioClientPtr);
                    if (hr >= 0)
                    {
                        _deviceRunning = true;
                        CaptureStartAnchor();
                        _prerollStarted = true;
                        _prerollPending = false;
                    }
                }
            });
        }
        // timeout 是 Task.Delay：只会以取消结束（ct 触发），不会故障——仅豁免取消，其余异常照抛。
        try { await timeout; } catch (OperationCanceledException) { }
    }

    /// <inheritdoc cref="WasapiOutput.Flush"/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClientPtr == IntPtr.Zero) return;
        RunControl(() =>
        {
            int hr = _audioClientReset!(_audioClientPtr);
            _prerollPending = false;   // Seek/Flush 后清除 preroll 状态
            // 重播（Ended→Playing）场景下，自然 Ended 时客户端仍 Running（尾音由设备自然放完，不主动 Stop），
            // 此时调 Reset 会返回 AUDCLNT_E_NOT_STOPPED——属良性（后序 Resume 会 Stop→Reset→Start 正确清空并启动），
            // 不记入警告避免噪音。其余失败码仍告警。
            if (hr < 0 && hr != WasapiInterop.AUDCLNT_E_NOT_STOPPED)
                _logger.LogWarning("IAudioClient.Reset 失败：HRESULT=0x{HR:X8}", hr);
        });
    }

    /// <inheritdoc cref="WasapiOutput.GetPlaybackPosition"/>
    public TimeSpan GetPlaybackPosition()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_audioClockPtr == IntPtr.Zero)
            return TimeSpan.Zero;

        return RunControl(() =>
        {
            int hr = _audioClockGetPosition!(_audioClockPtr, out ulong devicePosition, out _);
            if (hr < 0)
                return TimeSpan.Zero;

            // 修复（真 bug）：devicePosition 的单位由设备定义，【不是】帧数。
            // 官方换算：秒 = position / frequency（IAudioClock::GetFrequency，见 _audioClockFrequency）。
            if (_audioClockFrequency > 0)
                return TimeSpan.FromSeconds((double)devicePosition / _audioClockFrequency);

            // 回落：GetFrequency 不可用时按采样率换算（不精确，初始化时已告警）。
            if (_sampleRate <= 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds((double)devicePosition / _sampleRate);
        });
    }

    /// <summary>
    /// 线程安全的直接播放位置读取（不经过 <see cref="RunControl"/> 跨线程封送）。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不需要跨线程</b>：<c>IAudioClock::GetPosition</c> 按 MSDN 可由任意线程调用，
    /// 它只读设备维护的原生播放游标，且只被设备自身写入；读取是稳定的单值读取。</para>
    /// <para><b>为什么用它是合适方案</b>：该游标随真实播放平滑前进，且音频缓冲耗尽时天然停摆 ——
    /// 不会像"已提交末端时间"那样在批提交时被瞬间预支、又在两批间自由运行超调后猛拽回退。
    /// 视频主时钟应以此为准（</para>
    /// <para><b>为什么用它</b>：该游标随真实播放平滑前进，且音频缓冲耗尽时天然停摆 ——
    /// 不会像"已提交末端时间"那样在批提交时被瞬间预支、又在两批间自由运行超调后猛拽回退。
    /// 视频主时钟应以此为准。</para>
    /// <para>字段读取：<c>_audioClockPtr</c> / <c>_audioClockFrequency</c> / <c>_audioClockGetPosition</c>
    /// 均为 Initialize 时一次性写入、播放期恒定（已标记 volatile），跨线程读取安全。</para>
    /// </remarks>
    internal TimeSpan GetPlaybackPositionDirect()
    {
        var clockPtr = Volatile.Read(ref _audioClockPtr);
        var freq = Volatile.Read(ref _audioClockFrequency);
        var getPos = Volatile.Read(ref _audioClockGetPosition);
        if (clockPtr == IntPtr.Zero || freq == 0 || getPos is null)
            return TimeSpan.Zero;

        // 重播（Ended→Playing）主时钟武装：音频尚未为本遍播放 Start 时返回 0，使视频门控窗口内主时钟恒定=0，
        // 重播 PTS=0 首帧得以立即呈现（否则沿用上一遍锚点 → 返回陈旧的播放秒数 → 首帧被错判落后 → 整段 Drop 直到解码器推进、首帧追上主时钟才切入）。
        if (!_clockArmed)
            return TimeSpan.Zero;

        // WASAPI 回传 pu64QPCPosition（QPC 计数，100ns ticks），是本次 devicePosition 读数时刻的高精度
        // 时间锚点。用 QPC 插值出「现在」的平滑播放位置，消除音频引擎刷新周期的离散量化阶梯——
        // 之前丢弃该值(out _)导致主时钟按引擎周期跳变，直接钉死 WaitUntilDue 的呈现时刻相位（残留墙钟抖动成因）。
        int hr = getPos(clockPtr, out ulong devicePosition, out ulong qpcPosition);
        if (hr < 0)
            return TimeSpan.Zero;

        long qpcNow = System.Diagnostics.Stopwatch.GetTimestamp(); // 100ns ticks，与 qpcPosition 同单位
        double posSec = (double)devicePosition / freq;
        double driftSec = (qpcNow - (long)qpcPosition) / 10_000_000.0; // QPC 单位 = 100ns
        double rawSec = posSec + driftSec;

        double pendingSec = _sampleRate > 0 ? GetCurrentPaddingFrames() / (double)_sampleRate : 0.0;

        // 音画同步校准：共享模式 IAudioClock::GetPosition 的 devicePosition 含音频引擎「抓取领先」
        // （Start 后瞬间预取一段数据进系统混音缓冲）。该领先对 GetCurrentPadding（仅本 IAudioClient
        // 设备缓冲，≤bufferSize≈100ms）与 GetStreamLatency（返回 0）均不可见，故直接减锚点/
        // 填充/延迟整段无效。现以墙钟为锚：引擎领先稳定后（起播 >100ms）锁定 bias = rawSec -
        // wallElapsed（引擎领先+常偏），主时钟减此值即得真实可闻位置（≈墙钟，与视频 PTS 同源）。
        // 瞬态期（≤100ms）devicePosition≈墙钟，直接以墙钟为准，避免相位跳变。
        // 稳态锁定引擎领先：若在「devicePosition 尚未与墙钟锁步」的起播瞬态就捕获 bias 并永久减回，
        // 此刻 devicePosition 落后墙钟一个瞬态量（bias 为负）→ 减负数 = 变相给主时钟加上该量 →
        // 音频时钟比真实可闻位置快同一量 → 视频按此时钟提前呈现。因此必须等偏差稳定后再锁定：
        // 主时钟应反映真实可闻位置。devicePosition→可闻 的延迟（引擎领先 L）在稳态时
        // 等于 (devicePosition − 墙钟) 的锁定值；起播瞬态该值为负且持续收敛，故须等其稳定后再锁定。
        // 稳定判据：连续两次采样偏差 < 1ms，即引擎已与墙钟锁步、L 不再漂移。锁定后 bias 即为稳态 L，
        // 主时钟减此值即得真实可闻位置（与视频 PTS 同源）， skew 归零。
        double wallElapsed = _startStopwatch?.Elapsed.TotalSeconds ?? 0.0;
        const double CalibMinSec = 0.3;       // 过引擎起转瞬态再开始采样
        const double CalibStableEps = 0.001;  // 1ms 内偏差视为已稳态（引擎与墙钟锁步）
        double cand = rawSec - wallElapsed;   // 稳态时 = 引擎领先 L（devicePosition→可闻的真实延迟）

        if (wallElapsed < CalibMinSec)
        {
            // 起转瞬态：devicePosition 尚未与墙钟锁步，直接以墙钟为可闻近似
            double a0 = wallElapsed - pendingSec - _streamLatencySec;
            return TimeSpan.FromSeconds(a0 > 0 ? a0 : 0.0);
        }

        double prev = Volatile.Read(ref _calibratedBias);
        if (!Volatile.Read(ref _biasLatched))
        {
            if (Math.Abs(cand - prev) < CalibStableEps)
            {
                // 引擎领先已稳定 → 锁定（此刻 cand ≈ 稳态 L，主时钟减此值即真实可闻位置）
                Volatile.Write(ref _calibratedBias, cand);
                Volatile.Write(ref _biasLatched, true);
                _logger.LogInformation("[WASAPI-CALIB] 锁定引擎领先偏移={BiasMs:F1}ms（主时钟将减此值对齐可闻位置）",
                    cand * 1000.0);
            }
            else
            {
                // 仍收敛中：暂存候选，audible 继续以墙钟近似（避免瞬态偏置污染主时钟）
                Volatile.Write(ref _calibratedBias, cand);
                double a0 = wallElapsed - pendingSec - _streamLatencySec;
                return TimeSpan.FromSeconds(a0 > 0 ? a0 : 0.0);
            }
        }
        double audibleSec = rawSec - Volatile.Read(ref _calibratedBias) - pendingSec - _streamLatencySec;
        return TimeSpan.FromSeconds(audibleSec > 0 ? audibleSec : 0.0);
    }

    /// <summary>
    /// 记录启动墙钟基准：在 <c>IAudioClient.Start()</c> 之后调用（渲染线程内，RunControl 同步执行）。
    /// 用于起播后锁定音频引擎「抓取领先」偏移（<see cref="GetPlaybackPositionDirect"/> 的 bias 校准）。
    /// 重播（Ended→Playing）时 Stop→Reset→Start 会重新调用本方法，基准与锁定标志随之刷新。
    /// </summary>
    private void CaptureStartAnchor()
    {
        // 记录 Start 时刻墙钟基准（用于起播后锁定引擎领先偏移）
        _startStopwatch?.Stop();
        _startStopwatch = System.Diagnostics.Stopwatch.StartNew();
        Volatile.Write(ref _biasLatched, false);
        Volatile.Write(ref _calibratedBias, 0.0);
        // 主时钟武装：本遍播放音频已 Start，GetPlaybackPositionDirect 自此返回真实可闻位置。
        // （重播起点经 ResetPlaybackClock 置 false，确保预滚动/门控窗口内主时钟恒定=0。）
        _clockArmed = true;
        // LINGFAN_SYNC_LEAD_MS 已改由 VideoPipeline 作用在「呈现延迟」变量（治本），
        // 此处音频时钟保持纯可闻位置，不再做任何前移补偿。
        _logger.LogInformation("[WASAPI-ANCHOR] 启动墙钟基准已记录（用于校准引擎领先偏移）");
    }

    /// <summary>
    /// 重播（Ended→Playing）主时钟归零：解除武装并清校准，使 <see cref="GetPlaybackPositionDirect"/>
    /// 在音频重新 Start 之前恒定返回 0。
    /// <para><b>成因</b>：自然 Ended 时 WASAPI 客户端仍 Running（尾音由设备自然放完），<c>_startStopwatch</c>
    /// 持续累计（未随 Ended 归零）；而重播的视频门控（MediaPipelineHost.StartAsync 第(3)步 SignalAudioReady）早于音频
    /// Start（第(5)步），预滚动窗口内同步器据此读到的主时钟是陈旧值，把 PTS=0 重播首帧判为「落后过多 → Drop」，
    /// 直到解码器把流推进、首帧追上主时钟才切入。</para>
    /// <para>本方法在 MediaPlayer 重播分支（SeekAsync 后、StartAsync 前）调用，使该窗口主时钟=0，首帧立即呈现，无缝重播。</para>
    /// </summary>
    internal void ResetPlaybackClock()
    {
        _clockArmed = false;
        Volatile.Write(ref _biasLatched, false);
        Volatile.Write(ref _calibratedBias, 0.0);
        _startStopwatch?.Stop();
    }

    /// <inheritdoc cref="WasapiOutput.Latency"/>
    public TimeSpan Latency
    {
        get
        {
            if (!_initialized || _sampleRate <= 0 || _bufferSize <= 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds((double)_bufferSize / _sampleRate);
        }
    }

    /// <inheritdoc cref="WasapiOutput.Volume"/>
    public float Volume
    {
        get => _volume;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Clamp 0.0~1.0
            float clamped = Math.Clamp(value, 0.0f, 1.0f);
            _volume = clamped;

            if (_simpleVolumePtr != IntPtr.Zero)
            {
                RunControl(() =>
                {
                    var ec = Guid.Empty;
                    int hr = _simpleVolumeSetMasterVolume!(_simpleVolumePtr, clamped, ref ec);
                    if (hr < 0)
                        _logger.LogWarning("SetMasterVolume 失败：HRESULT=0x{HR:X8}", hr);
                });
            }
        }
    }

    /// <inheritdoc cref="WasapiOutput.BufferSize"/>
    public int BufferSize => _bufferSize;

    /// <inheritdoc cref="WasapiOutput.ExclusiveMode"/>
    public bool ExclusiveMode => _exclusiveMode;

    /// <inheritdoc cref="WasapiOutput.EventDrivenMode"/>
    public bool EventDrivenMode => _eventDrivenMode;

    /// <inheritdoc cref="WasapiOutput.DeviceSampleFormat"/>
    public SampleFormat DeviceSampleFormat => _deviceSampleFormat;

    /// <inheritdoc cref="WasapiOutput.Dispose"/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 向渲染线程投递 Shutdown 消息：触发 COM 释放（ReleaseComObjects）+ CoUninitialize，并停止线程。
        if (_thread is not null)
        {
            try
            {
                // 先置位关闭信号：渲染线程在 WriteFrame 的缓冲满丢帧路径不阻塞；Shutdown 消息入队后
                // 渲染线程得以快速处理完队列并进入 Shutdown（不再有 2s 卡顿退出）。
                _shutdownEvent.Set();
                var shutdown = new RenderItem(ItemKind.Shutdown, null);
                _queue.Enqueue(shutdown);
                _workAvailable.Set();
                shutdown.Done.Wait();   // 等到渲染线程完成 COM 释放
            }
            catch { /* 释放时忽略错误 */ }

            try { _thread.Join(); } catch { }
            _thread = null;
        }
        else
        {
            // 极端情况：STA 线程从未创建（InitializeAsync 未被调用），直接释放 COM 对象
            ReleaseComObjects();
        }

        // 释放事件句柄
        if (_bufferEvent != null)
        {
            _bufferEvent.Dispose();
            _bufferEvent = null;
        }

        // STA 公寓（CoInitializeEx(COINIT_APARTMENTTHREADED)）由渲染线程在 Shutdown 消息处理完成后
        // 经 RenderThreadProc finally → CoUninitialize 正确反初始化，不再有跨实例/测试污染 COM 单元的问题。

        if (_submittedFrames > 0 || _droppedFrames > 0)
        {
            double approxSec = _sampleRate > 0 ? (double)_submittedSamples / _sampleRate : 0.0;
            _logger.LogWarning(
                "[WASAPI-DIAG] submittedFrames={SubmittedFrames} submittedSamples={SubmittedSamples} approxSeconds={ApproxSeconds:F2} droppedFrames={DroppedFrames} bufferSize={BufferSize} sampleRate={SampleRate}",
                _submittedFrames, _submittedSamples, approxSec, _droppedFrames, _bufferSize, _sampleRate);
            // 双输出：xunit detailed 不转发 ILogger，用 Console 确保测试输出可见
            Console.WriteLine($"[WASAPI-DIAG] submittedFrames={_submittedFrames} submittedSamples={_submittedSamples} approxSeconds={approxSec:F2} droppedFrames={_droppedFrames} bufferSize={_bufferSize} sampleRate={_sampleRate}");
        }

        _initialized = false;
        _logger.LogDebug("WASAPI 渲染线程已释放");
    }

    /// <inheritdoc cref="WasapiOutput.DisposeAsync"/>
    /// <remarks>
    /// 接口契约：COM 释放为快速同步调用，无 I/O 可 await。
    /// 委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private enum ItemKind : byte { Control, Frame, Shutdown }

    /// <summary>
    /// 渲染线程工作项：控制消息（Action/Func）或音频帧（Frame）或关闭（Shutdown）。
    /// 调用方 Enqueue + Set(_workAvailable) 唤醒渲染线程；渲染线程处理完后 Set(Done) 通知调用方。
    /// </summary>
    private sealed class RenderItem
    {
        public readonly ItemKind Kind;
        public readonly AudioFrame? Frame;
        public Action? Action;
        public Func<object?>? Func;
        public object? Result;
        public Exception? Exception;
        public readonly ManualResetEventSlim Done = new(false);

        public RenderItem(ItemKind kind, AudioFrame? frame)
        {
            Kind = kind;
            Frame = frame;
        }

        public void Invoke()
        {
            if (Action is not null) Action();
            else Result = Func!();
        }
    }

    private void EnsureRenderThread()
    {
        if (_thread is not null) return;
        _thread = new Thread(RenderThreadProc) { IsBackground = true, Name = "WasapiRenderLoop" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Wait();
    }

    /// <summary>
    /// 在渲染线程执行控制动作并等待其完成（异常透传）。
    /// </summary>
    private void RunControl(Action action)
    {
        var item = new RenderItem(ItemKind.Control, null) { Action = action };
        _queue.Enqueue(item);
        _workAvailable.Set();
        item.Done.Wait();
        if (item.Exception is not null)
            ExceptionDispatchInfo.Throw(item.Exception);
    }

    /// <summary>
    /// 在渲染线程执行带返回值的控制函数并等待其完成（异常透传）。
    /// </summary>
    private T RunControl<T>(Func<T> func)
    {
        var item = new RenderItem(ItemKind.Control, null) { Func = () => func()! };
        _queue.Enqueue(item);
        _workAvailable.Set();
        item.Done.Wait();
        if (item.Exception is not null)
            ExceptionDispatchInfo.Throw(item.Exception);
        return (T)(item.Result ?? default(T))!;
    }

    /// <summary>
    /// 释放所有 COM 对象（逆序释放）。由渲染线程在 Shutdown 时调用。
    /// </summary>
    private void ReleaseComObjects()
    {
        if (_audioClientPtr != IntPtr.Zero && _audioClientStop is not null)
        {
            try { _audioClientStop(_audioClientPtr); }
            catch { }
        }
        _deviceRunning = false;

        ReleaseComPtr(ref _audioClockPtr);
        _audioClockGetPosition = null;
        _audioClockGetFrequency = null;
        _audioClockFrequency = 0;

        ReleaseComPtr(ref _simpleVolumePtr);
        _simpleVolumeSetMasterVolume = null;

        ReleaseComPtr(ref _renderClientPtr);
        _renderClientGetBuffer = null;
        _renderClientReleaseBuffer = null;

        ReleaseComPtr(ref _audioClientPtr);
        _audioClientInitialize = null;
        _audioClientGetBufferSize = null;
        _audioClientGetCurrentPadding = null;
        _audioClientIsFormatSupported = null;
        _audioClientGetMixFormat = null;
        _audioClientStart = null;
        _audioClientStop = null;
        _audioClientReset = null;
        _audioClientSetEventHandle = null;
        _audioClientGetService = null;

        ReleaseComPtr(ref _devicePtr);
        _deviceActivate = null;

        ReleaseComPtr(ref _enumeratorPtr);
        _enumeratorGetDefault = null;
    }

    /// <summary>安全释放单个原生 COM 指针（Marshal.Release 减引用计数并置零）。</summary>
    private static void ReleaseComPtr(ref IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        try
        {
            Marshal.Release(ptr);
        }
        catch { }
        ptr = IntPtr.Zero;
    }
}

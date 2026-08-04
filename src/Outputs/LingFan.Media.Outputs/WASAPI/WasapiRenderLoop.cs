using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// WASAPI 渲染线程（STA 独占）。Phase 1 从 <see cref="WasapiOutput"/> 提取：
/// 把全部 WASAPI COM 调用收进一个常驻 STA 线程，消除逐帧 <c>RunOnSta</c> 跨线程封送税（F1 根因），
/// 为 Phase 2 的生产者/消费者解耦（<c>AudioSampleRing</c>）奠定线程模型基础。
/// </summary>
/// <remarks>
/// <para><b>线程模型</b>：构造后由 <see cref="InitializeAsync"/> 启动唯一 STA 线程（COINIT_APARTMENTTHREADED）。
/// 该线程在 <c>CoInitializeEx</c> 之后进入渲染循环：消费控制消息（Initialize/Pause/...）与音频帧，
/// 所有 COM 调用（CoCreateInstance/GetMixFormat/Initialize/GetBuffer/ReleaseBuffer/GetPosition）均在同一线程。
/// WASAPI 要求 IAudioClient 在 STA 公寓创建与使用，MTA 下 GetMixFormat/Initialize 会触发 native AV（0xC0000005），
/// 故 STA 线程唯一性不可破坏（F3 关联）。</para>
/// <para><b>异步策略</b>（与提取前一致，未改变契约语义）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，内部启动 STA 线程 + 在渲染线程执行 InitializeCore，返回 <see cref="Task.CompletedTask"/>。
/// CoInitializeEx + COM 设备枚举均为同步 COM 调用，无 I/O 可 await，非伪异步。</item>
/// <item><see cref="Initialize"/>：同步（sync 分类），在渲染线程执行 IAudioClient + IAudioRenderClient 创建 +
/// V2 格式协商（GetMixFormat / IsFormatSupported）+ V2 事件驱动初始化（SetEventHandle）。</item>
/// <item><see cref="Submit"/>/<see cref="SubmitBatch"/>：同步边界（native 分类），将帧交给渲染线程消费（阻塞等待该帧写入完成，
/// 故调用方在 Submit 返回后可安全归还帧所有权——与 v1 行为一致）；缓冲满时由渲染线程的 COM 背压等待，
/// 不再由调用方跨线程往返。</item>
/// <item><see cref="Pause"/>/<see cref="Resume"/>/<see cref="Flush"/>：同步（sync 分类），渲染线程执行 IAudioClient.Stop/Start/Reset。</item>
/// <item><see cref="GetPlaybackPosition"/>：同步（sync 分类），渲染线程执行 IAudioClock.GetPosition。</item>
/// <item><see cref="Dispose"/>：同步快速释放（sync 分类），向渲染线程投递 Shutdown 消息触发 COM 释放 + CoUninitialize，释放事件句柄。</item>
/// <item><see cref="DisposeAsync"/>：接口契约，委托 <see cref="Dispose"/> + <see cref="ValueTask.CompletedTask"/>。非伪异步。</item>
/// </list>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，采用原始 vtable P/Invoke（ComVTable 委托封送），不使用 [ComImport]/RCW，NativeAOT 兼容。</para>
/// <para><b>资源所有权</b>：IMMDeviceEnumerator/IMMDevice/IAudioClient/IAudioRenderClient/ISimpleAudioVolume/IAudioClock
/// 的原生指针均由本类持有（Session 级），Dispose 时通过 Marshal.Release(IntPtr) 逆序释放。
/// V2 事件句柄（EventWaitHandle）由本类创建并持有，Dispose 时释放。</para>
/// <para><b>Submit 所有权</b>：v2 语义保持——Submit 不接管帧所有权，不 Dispose 帧。调用方（AudioPipeline）负责 Return 到 FramePool 或 Dispose。</para>
/// <para><b>V2 增强（Task-V2-13）</b>：O7 独占模式（IsFormatSupported 协商 + 错误处理）、O8 事件驱动（SetEventHandle + WaitOne 替代 Sleep 轮询）、
/// O9 多格式直出（GetMixFormat 检测 + S16/S32/F32 直出）。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WasapiRenderLoop
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
    // 治本①（起播静默窗）：预填目标帧数 = 设备缓冲总大小，写满后再 Start，引擎抓取真实数据而非静音。
    private int _primeFrames;
    private bool _prerollPending;             // BeginStreamingAsync 已 arm（Stop→Reset），等待 WriteFrame 预填达标后自动 Start
    private bool _prerollStarted;             // 已自动 Start（防重复触发）
    private TaskCompletionSource<bool>? _primeTcs;
    private const int PrimeTimeoutMs = 600;  // 无音频轨/极短片段兜底：超时强制 Start，防 PlayAsync 挂起
    private double _streamLatencySec; // IAudioClient.GetStreamLatency() 返回的「提交→可闻」延迟（秒），用于主时钟校准
    // 🔴 音画同步（2026-08-04 实测校准，数据坐实前版 anchor/padding 修复为 no-op）：
    // 共享模式 IAudioClock::GetPosition 的 devicePosition 含音频引擎「抓取领先」（Start 后瞬间预取 ~0.5s
    // 进系统混音缓冲），该领先对 GetCurrentPadding（仅本 IAudioClient 设备缓冲，≤bufferSize≈100ms）
    // 与 GetStreamLatency（本机返回 0）均不可见，故直接减锚点/填充/延迟整段无效。
    // 改为以墙钟为锚：起播 >100ms 后锁定 bias = rawSec - wallElapsed（引擎领先+常偏），
    // 主时钟减此值即得真实可闻位置（≈墙钟，与视频 PTS 同源）。三者跨线程（渲染线程写、时钟线程读）均用 Volatile 访问。
    private System.Diagnostics.Stopwatch? _startStopwatch;
    private double _calibratedBias;
    private bool _biasLatched;
    // 注：LINGFAN_SYNC_LEAD_MS 已改由 VideoPipeline 作用在「呈现延迟」变量（治本），
    // 本音频时钟只暴露纯可闻位置，不再做任何前移补偿。
    private int _sampleRate;
    private int _channels;
    private float _volume = 1.0f;

    // V2: 事件驱动模式
    private EventWaitHandle? _bufferEvent;

    // 诊断计数器（纯观察，不影响音频逻辑）：定位"听感卡顿/断续"根因。
    // submittedSamples = 实际成功写入 WASAPI 的累计采样帧数；droppedFrames = WaitForBufferSpace 超时/参数异常丢弃的帧数。
    private long _submittedSamples;
    private int _submittedFrames;
    private long _droppedFrames;

    // 冷启动诊断（WASAPI_OPEN_DIAG=1 时启用）：拆开 InitializeAsync/Initialize 各 COM 步耗时，定位 OpenAsync 2.8s 真凶。
    private readonly bool _openDiag = System.Environment.GetEnvironmentVariable("WASAPI_OPEN_DIAG") == "1";
    private Stopwatch? _initDiagSw;

    // V2: 设备原生采样格式（Initialize 时检测，Submit 时用于直出判断）
    private SampleFormat _deviceSampleFormat = SampleFormat.F32;

    // V2: 设备原生 mix format 的采样率/声道数（GetMixFormat 检测）。
    // ⚠️ 审计修正（2026-07-31）：此前注释称"初始化 WAVEFORMATEX 必须用它而非解码器输出格式"，这是错的
    // ——那样会让 Submit 侧的解码器格式帧被按设备速率播出。现仅用于诊断日志与 AUTOCONVERTPCM 判断，
    // 实际初始化格式一律用客户端（解码器）采样率/声道数。详见 NegotiateSharedFormat 步骤 4。
    private int _mixSampleRate;
    private int _mixChannels;

    // V2: IAudioClock 的设备频率（units/秒）。GetPosition 的返回值须除以它才是秒数，
    // 单位由设备定义（共享模式常见为字节/秒 = nSamplesPerSec * nBlockAlign），不等于采样率。
    // 0 表示不可用（GetFrequency 失败），此时 GetPlaybackPosition 回落到采样率换算并告警。
    private long _audioClockFrequency;

    // ── STA 渲染线程基础设施（Phase 1：常驻渲染循环，替代原"每次 RunOnSta 跨线程封送"）──
    // 单一 STA 线程：CoInitializeEx(STA) → 处理控制消息与音频帧 → 关闭时释放 COM + CoUninitialize。
    // 所有 COM 调用都在该线程，调用方通过 ConcurrentQueue + AutoResetEvent 投递工作项并等待完成。
    private Thread? _thread;
    private readonly ConcurrentQueue<RenderItem> _queue = new();
    private readonly AutoResetEvent _workAvailable = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    // 关闭信号：Dispose 时置位，使 WaitForBufferSpace 立即放弃阻塞等待（残留帧在关闭期被跳过，不卡 2s 超时）
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

    // 冷启动诊断辅助：WASAPI_OPEN_DIAG=1 时打印各 COM 步累计耗时（[WASAPI-OPEN]）。
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
    /// V2 语义保持：Submit 不接管帧所有权。将帧投递给渲染线程并阻塞等待其写入完成（COM 背压在渲染线程内），
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
    /// 单帧提交失败（缓冲区超时/参数异常）仅丢弃该帧并继续后续帧，不会中断整批。不接管帧所有权。
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
                // 不再冒泡成 fail 日志（回归修复 2026-08-03）。
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
            // 审计修复：0x88890004 实际是 AUDCLNT_E_DEVICE_INVALIDATED（设备移除），非 AUDCLNT_E_NOT_INITIALIZED（0x88890001）。
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
    /// 🔴 重播（Ended→Playing）健壮性：自然 Ended 时客户端仍 Running（尾音由设备自然放完，不主动 Stop），
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

            // 恢复播放路径：立即启动，清除 preroll 状态（避免 WriteFrame 误触发自动启动）。
            _prerollStarted = true;
            _prerollPending = false;
            _primeTcs = null;

            // 🔴 音画同步修复（2026-08-04）：捕获启动锚点。Start 后立刻读一次设备游标作为本流
            // 「已播放量」的零点，主时钟后续用 (devicePosition - 锚点) 得到本流真实播放秒数，
            // 消除共享模式 devicePosition 启动即 ~0.5s 的任意累计偏移。重播时 Stop→Reset→Start
            // 会重新调用本方法，锚点随之刷新（Reset 后游标归零，新锚点≈旧锚点，差值连续）。
            CaptureStartAnchor();
        });
    }

    /// <inheritdoc cref="WasapiOutput.BeginStreamingAsync"/>
    /// <remarks>
    /// 根治起播静默窗（2026-08-04）：WASAPI 共享模式下，若在空缓冲上直接 Start，音频引擎会瞬间
    /// 抓取 ~0.5s 静音进系统混音缓冲，真实 PCM 排在其后才可闻 → 起播出现静默窗。
    /// 本方法只做 Stop→Reset 并 arm preroll（不 Start）；提交循环把真实 PCM 写满设备缓冲后，
    /// <see cref="WriteFrame"/> 在 padding 达标时自动 Start，引擎抓取的是真实数据 → 无静默窗。
    /// 返回的任务在自动 Start 后完成（或由超时兜底强制 Start，防无音频轨/极短片段挂起 PlayAsync）。
    /// </remarks>
    public ValueTask BeginStreamingAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClientPtr == IntPtr.Zero)
            return ValueTask.CompletedTask;
        RunControl(() =>
        {
            _audioClientStop!(_audioClientPtr);
            _audioClientReset!(_audioClientPtr);
            _prerollStarted = false;
            _prerollPending = true;
            _primeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        });
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
            RunControl(() =>
            {
                if (_prerollPending && !_prerollStarted)
                {
                    int hr = _audioClientStart!(_audioClientPtr);
                    if (hr >= 0)
                    {
                        CaptureStartAnchor();
                        _prerollStarted = true;
                        _prerollPending = false;
                    }
                }
            });
        }
        // 忽略 timeout 任务自身的取消异常（ct 取消时静默收尾）
        try { await timeout; } catch { }
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

            // ⚠️ 审计修复（2026-07-31，真 bug）：devicePosition 的单位由设备定义，【不是】帧数。
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
    /// <para><b>为什么用它是根治方案</b>：该游标随真实播放平滑前进，且音频缓冲耗尽时天然停摆 ——
    /// 不会像"已提交末端时间"那样在批提交时被瞬间预支、又在两批间自由运行超调后猛拽回退。
    /// 视频主时钟应以此为准（</para>
    /// <para><b>为什么用它根治</b>：该游标随真实播放平滑前进，且音频缓冲耗尽时天然停摆 ——
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

        // WASAPI 回传 pu64QPCPosition（QPC 计数，100ns ticks），是本次 devicePosition 读数时刻的高精度
        // 时间锚点。用 QPC 插值出「现在」的平滑播放位置，消除音频引擎周期(~10ms)的离散量化阶梯——
        // 之前丢弃该值(out _)导致主时钟按 ~10ms 跳变，直接钉死 WaitUntilDue 的呈现时刻相位（残留墙钟抖动根因）。
        int hr = getPos(clockPtr, out ulong devicePosition, out ulong qpcPosition);
        if (hr < 0)
            return TimeSpan.Zero;

        long qpcNow = System.Diagnostics.Stopwatch.GetTimestamp(); // 100ns ticks，与 qpcPosition 同单位
        double posSec = (double)devicePosition / freq;
        double driftSec = (qpcNow - (long)qpcPosition) / 10_000_000.0; // QPC 单位 = 100ns
        double rawSec = posSec + driftSec;

        double pendingSec = _sampleRate > 0 ? GetCurrentPaddingFrames() / (double)_sampleRate : 0.0;

        // 🔴 音画同步修复（2026-08-04 实测校准，数据坐实前版 anchor/padding 修复为 no-op）：
        // 共享模式 IAudioClock::GetPosition 的 devicePosition 含音频引擎「抓取领先」（Start 后瞬间预取
        // ~0.5s 进系统混音缓冲）。该领先对 GetCurrentPadding（仅本 IAudioClient 设备缓冲，≤bufferSize≈100ms）
        // 与 GetStreamLatency（本机返回 0）均不可见，故前版减 anchor(0)/padding(~0)/latency(0) 整段无效
        // （log 证 start delta 仍为 -182ms、steady +45ms，与修复前完全一致）。
        // 现以墙钟为锚：引擎领先稳定后（起播 >100ms）锁定 bias = rawSec - wallElapsed = 引擎领先+常偏，
        // 主时钟减此值即得真实可闻位置（≈墙钟，与视频 PTS 同源）。瞬态期（≤100ms）devicePosition≈墙钟，
        // 直接以墙钟为准，避免相位跳变。零架构风险、纯本地可闻校准。
        // 🔴 音画同步根治（2026-08-04 R34）：稳态锁定引擎领先，消除起播瞬态污染。
        // 旧实现（CalibWindowSec=0.1）在「devicePosition 尚未与墙钟锁步」的起播瞬态就捕获 bias 并永久减回，
        // 而此刻 devicePosition 落后墙钟 ~29ms（bias 为负）→ 减负数 = 变相给主时钟加 29ms →
        // 音频时钟比真实可闻位置快 ~29ms → 视频按此时钟提前 ~29ms 呈现 → 用户感知「声音晚一点点」。
        // 真根：主时钟应反映真实可闻位置。devicePosition→可闻 的延迟（引擎领先 L）在稳态时
        // 等于 (devicePosition − 墙钟) 的锁定值；起播瞬态该值为负且持续收敛，故必须等其稳定后再锁定。
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
        // 🔴 LINGFAN_SYNC_LEAD_MS 已改由 VideoPipeline 作用在「呈现延迟」变量（治本），
        // 此处音频时钟保持纯可闻位置，不再做任何前移补偿。
        _logger.LogInformation("[WASAPI-ANCHOR] 启动墙钟基准已记录（用于校准引擎领先偏移）");
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
                // 先置位关闭信号：使残留帧的 WaitForBufferSpace 立即放弃等待（跳过而非卡 2s 超时），
                // 渲染线程得以快速处理完队列并进入 Shutdown。
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

        // V2: 释放事件句柄
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

    // ── 渲染线程基础设施 ──

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

    private void RenderThreadProc()
    {
        WasapiInterop.CoInitializeEx(IntPtr.Zero, WasapiInterop.COINIT_APARTMENTTHREADED);
        _started.Set();
        LogOpen("STA线程CoInit完成");
        try
        {
            // 渲染循环：持续消费队列中的控制消息与音频帧，直到收到 Shutdown。
            while (true)
            {
                while (_queue.TryDequeue(out var item))
                {
                    if (item.Kind == ItemKind.Shutdown)
                    {
                        ReleaseComObjects();
                        item.Done.Set();
                        return;
                    }

                    try
                    {
                        if (item.Kind == ItemKind.Frame)
                            WriteFrame(item.Frame!);
                        else
                            item.Invoke();
                    }
                    catch (Exception ex)
                    {
                        item.Exception = ex;
                    }
                    finally
                    {
                        item.Done.Set();
                    }
                }

                // 无待处理项时等待唤醒（新帧 / 控制消息 / Shutdown 均会 Set _workAvailable）。
                // AutoResetEvent 保证：若在 TryDequeue 排空后、WaitAny 前发生 Enqueue+Set，等待会立即返回，无丢失唤醒。
                _workAvailable.WaitOne();
            }
        }
        finally
        {
            WasapiInterop.CoUninitialize();
        }
    }

    /// <summary>
    /// 初始化 COM 单元并获取默认音频渲染设备（在渲染线程内执行）。
    /// </summary>
    private void InitializeCore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_devicePtr != IntPtr.Zero)
            throw new InvalidOperationException("InitializeAsync 已调用，请勿重复调用。");

        // 注意：COM 单元（STA）的 CoInitializeEx 已在渲染线程（RenderThreadProc）内完成。
        // 本方法在渲染线程上执行，故此处不再初始化/反初始化 COM 单元。

        try
        {
            // 2. 创建 IMMDeviceEnumerator
            var clsid = WasapiInterop.CLSID_MMDeviceEnumerator;
            var iid = WasapiInterop.IID_IMMDeviceEnumerator;
            int hr = WasapiInterop.CoCreateInstance(
                ref clsid, IntPtr.Zero, WasapiInterop.CLSCTX_ALL,
                ref iid, out IntPtr pEnumerator);
            Marshal.ThrowExceptionForHR(hr);

            _enumeratorPtr = pEnumerator;   // 持有 CoCreateInstance 返回的引用（refcount 由本类拥有）
            _enumeratorGetDefault = ComVTable.Get<IMMDeviceEnumerator_GetDefaultAudioEndpoint>(pEnumerator, 1);
            LogOpen("CoCreateInstance(Enumerator)");

            // 3. 获取默认音频渲染设备
            hr = _enumeratorGetDefault(
                _enumeratorPtr,
                WasapiInterop.EDataFlow_Render,
                WasapiInterop.ERole_Console,
                out IntPtr pDevice);
            Marshal.ThrowExceptionForHR(hr);

            _devicePtr = pDevice;   // 持有 GetDefaultAudioEndpoint 返回的引用
            _deviceActivate = ComVTable.Get<IMMDevice_Activate>(pDevice, 0);
            LogOpen("GetDefaultAudioEndpoint");
        }
        catch
        {
            // 初始化失败，清理已创建的 COM 对象（CoUninitialize 由渲染线程 proc 的 finally 负责）
            ReleaseComObjects();
            throw;
        }

        _logger.LogDebug("WASAPI 设备枚举器已创建，默认渲染设备已获取。");
    }

    /// <summary>
    /// 初始化 IAudioClient（在渲染线程内执行）：激活客户端 + 格式协商 + 获取服务 + 事件驱动 + 初始音量。
    /// 即原 <see cref="WasapiOutput.Initialize"/> 主体（去除 RunOnSta 包装）。
    /// </summary>
    private void InitializeImpl(int sampleRate, int channels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
            throw new InvalidOperationException("WASAPI 输出已初始化，请先 Dispose 再重新初始化。");

        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率必须大于 0。");
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), "声道数必须大于 0。");

        if (_devicePtr == IntPtr.Zero)
            throw new InvalidOperationException("InitializeAsync 尚未调用，无法 Initialize。");

        _sampleRate = sampleRate;
        _channels = channels;

        try
        {
            // 1. 激活 IAudioClient
            var iid = WasapiInterop.IID_IAudioClient;
            int hr = _deviceActivate!(_devicePtr, ref iid, WasapiInterop.CLSCTX_ALL, IntPtr.Zero, out IntPtr pAudioClient);
            Marshal.ThrowExceptionForHR(hr);

            _audioClientPtr = pAudioClient;   // 持有 Activate 返回的引用
            LogOpen("Activate(IAudioClient)");
            // ComVTable.Get 的 slotIndex 为「相对 IUnknown 的方法索引」（IUnknown 占用 vtable 绝对槽位 0-2，故绝对槽位 = 3 + slotIndex）。
            // 标准 IAudioClient vtable（audioclient.h 官方声明顺序，IUnknown 之后相对索引）：
            //   0 Initialize | 1 GetBufferSize | 2 GetStreamLatency(未使用) | 3 GetCurrentPadding
            //   | 4 IsFormatSupported | 5 GetMixFormat | 6 GetDevicePeriod(未使用)
            //   | 7 Start | 8 Stop | 9 Reset | 10 SetEventHandle | 11 GetService
            // ⚠️ 审计修复（2026-07-30 第二轮，真机 DIAG 探针坐实）：此前基线注释抄漏了相对槽 2 的
            //    GetStreamLatency，导致 GetCurrentPadding 起整体 -1 错位——GetMixFormat(误取槽4)
            //    实际调到 IsFormatSupported，x64 下垃圾 pFormat 被解引用 → 原生 AV 0xC0000005。
            _audioClientInitialize = ComVTable.Get<IAudioClient_Initialize>(pAudioClient, 0);
            _audioClientGetBufferSize = ComVTable.Get<IAudioClient_GetBufferSize>(pAudioClient, 1);
            // 🔴 2026-08-04 音画同步修复：补上被刻意跳过的 slot 2 GetStreamLatency（不扰动后续槽位，
            // 因为 GetCurrentPadding 仍在 slot 3，相对索引未变）。用于把主时钟从「设备渲染游标」校准到
            // 「真实可闻位置」——之前不减数百 ms 延迟导致视频比听到的声音整体提前 ~0.5s。
            _audioClientGetStreamLatency = ComVTable.Get<IAudioClient_GetStreamLatency>(pAudioClient, 2);
            _audioClientGetCurrentPadding = ComVTable.Get<IAudioClient_GetCurrentPadding>(pAudioClient, 3);
            _audioClientIsFormatSupported = ComVTable.Get<IAudioClient_IsFormatSupported>(pAudioClient, 4);
            _audioClientGetMixFormat = ComVTable.Get<IAudioClient_GetMixFormat>(pAudioClient, 5);
            // 跳过未使用的 GetDevicePeriod（相对 slot 6）
            _audioClientStart = ComVTable.Get<IAudioClient_Start>(pAudioClient, 7);
            _audioClientStop = ComVTable.Get<IAudioClient_Stop>(pAudioClient, 8);
            _audioClientReset = ComVTable.Get<IAudioClient_Reset>(pAudioClient, 9);
            _audioClientSetEventHandle = ComVTable.Get<IAudioClient_SetEventHandle>(pAudioClient, 10);
            _audioClientGetService = ComVTable.Get<IAudioClient_GetService>(pAudioClient, 11);

            // 1.5 V2 O10：在 Initialize 之前，通过 IAudioClient2.SetClientProperties 设置会话分类，
            // 防止 Windows 将后台/非前台/隐藏窗口的音频会话在播放数秒后挂起（声音 ~15s 中断）。
            // 全程 try/guard：任何不支持/失败都只记日志，不影响后续正常 Initialize（最坏退回旧行为）。
            // ⚠️ 2026-08-02 结案：曾长期 0xC0000005，真因是本文件 TrySetSessionCategory 的 vtable 槽位算错一格
            // （误调 IAudioClient2::IsOffloadCapable —— 它多一个 BOOL* 出参，导致向未初始化寄存器指向的野地址写入）。
            // 槽位已修正为 slotIndex 13（绝对槽 16），官方 COM 探针九个分类均 S_OK，调用本身不再崩。
            // 但 EnableBackgroundCapableSession 仍默认 false：启用它的原始动机（防 OS 挂起后台会话）未被证实，
            // 且实测启用后出现「约 30s 静音后才出声」的回归。详见 WasapiOptions 上的说明。
            if (_options.EnableBackgroundCapableSession)
                TrySetSessionCategory(pAudioClient);

            // 2. V2 格式协商（O7 独占模式 + O9 多格式直出）
            WAVEFORMATEX format;
            if (_exclusiveMode)
            {
                format = NegotiateExclusiveFormat(sampleRate, channels);
            }
            else
            {
                format = NegotiateSharedFormat(sampleRate, channels);
                LogOpen("NegotiateSharedFormat");
            }

            _logger.LogDebug("WASAPI 格式协商完成：设备格式={Format}, 采样率={SampleRate}Hz, 声道={Channels}",
                _deviceSampleFormat, sampleRate, channels);

            // 3. 初始化 IAudioClient
            int shareMode = _exclusiveMode
                ? WasapiInterop.AUDCLNT_SHAREMODE_EXCLUSIVE
                : WasapiInterop.AUDCLNT_SHAREMODE_SHARED;

            // V2 O8: 事件驱动模式
            int streamFlags = _eventDrivenMode
                ? WasapiInterop.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                : 0;

            // ⚠️ 审计修复（2026-07-31，真 bug 配套修复）：共享模式下必须显式要求音频引擎
            //    插入声道矩阵器 + 采样率转换器，否则客户端格式（解码器 44.1kHz/2ch）与引擎
            //    mix format（常见 48kHz/2ch，多声道设备可能 6ch）不一致时，引擎【不会】自动转换。
            if (!_exclusiveMode)
            {
                streamFlags |= WasapiInterop.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                             | WasapiInterop.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            }

            long bufferDurationHns = (long)(_options.BufferDuration.TotalSeconds * WasapiInterop.ReftimesPerSec);

            var sessionGuid = Guid.Empty;
            unsafe
            {
                hr = _audioClientInitialize(
                    _audioClientPtr,
                    shareMode,
                    streamFlags,
                    bufferDurationHns,
                    _exclusiveMode ? bufferDurationHns : 0, // 独占模式需指定 periodicity，共享模式 = 0
                    (IntPtr)(&format),
                    ref sessionGuid);
            }
            LogOpen("IAudioClient.Initialize");

            // V2 O7: 独占模式错误处理
            if (hr == WasapiInterop.AUDCLNT_E_DEVICE_IN_USE)
            {
                throw new InvalidOperationException(
                    "音频设备已被其他应用程序独占占用，无法以独占模式初始化。请关闭其他音频应用或切换到共享模式。",
                    new COMException("AUDCLNT_E_DEVICE_IN_USE", hr));
            }
            if (hr == WasapiInterop.AUDCLNT_E_UNSUPPORTED_FORMAT)
            {
                throw new NotSupportedException(
                    $"音频设备不支持请求的格式：{_deviceSampleFormat} {sampleRate}Hz {channels}ch。" +
                    (_exclusiveMode
                        ? "独占模式直通硬件，请改用共享模式（由音频引擎重采样）或调整 WasapiOptions.PreferredSampleFormat。"
                        : $"共享模式已启用 AUTOCONVERTPCM，设备 mix 为 {_mixSampleRate}Hz/{_mixChannels}ch；" +
                          "若仍失败，请调整 WasapiOptions.PreferredSampleFormat。"));
            }
            // 审计修复（2026-07-31）：给出可诊断的错误，而不是笼统的 HRESULT。
            if (hr == WasapiInterop.AUDCLNT_E_INVALID_STREAM_FLAG)
            {
                throw new NotSupportedException(
                    "IAudioClient.Initialize 拒绝了 streamFlags 组合（AUDCLNT_E_INVALID_STREAM_FLAG）。" +
                    $"当前：共享模式={!_exclusiveMode}, 事件驱动={_eventDrivenMode}, " +
                    "AUTOCONVERTPCM|SRC_DEFAULT_QUALITY=共享模式下启用。",
                    new COMException("AUDCLNT_E_INVALID_STREAM_FLAG", hr));
            }
            if (hr < 0)
            {
                _logger.LogError("IAudioClient.Initialize 失败：HRESULT=0x{HR:X8}", hr);
                Marshal.ThrowExceptionForHR(hr);
            }

            // 4. V2 O8: 事件驱动模式——注册事件句柄
            if (_eventDrivenMode)
            {
                _bufferEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                hr = _audioClientSetEventHandle!(_audioClientPtr, _bufferEvent.SafeWaitHandle.DangerousGetHandle());
                if (hr < 0)
                {
                    _logger.LogWarning("SetEventHandle 失败：HRESULT=0x{HR:X8}，回退到轮询模式", hr);
                    _bufferEvent.Dispose();
                    _bufferEvent = null;
                    _eventDrivenMode = false; // 回退到轮询
                }
                else
                {
                    _logger.LogDebug("WASAPI 事件驱动模式已启用");
                }
            }

            // 5. 获取缓冲区大小
            hr = _audioClientGetBufferSize(_audioClientPtr, out uint bufferFrames);
            Marshal.ThrowExceptionForHR(hr);
            _bufferSize = (int)bufferFrames;
            _primeFrames = _bufferSize;   // 预填目标 = 整设备缓冲（根治起播静默窗：引擎抓取真实数据而非静音）

            // 5.5 🔴 音画同步修复（2026-08-04）：获取「提交→可闻」流延迟，用于主时钟校准。
            // GetStreamLatency 必须在 Initialize 成功后调用（slot 2 已接）。返回值单位 100ns。
            // 失败不影响播放，仅退回 0（主时钟不校准，保持旧行为）。
            _streamLatencySec = 0.0;
            if (_audioClientGetStreamLatency is not null)
            {
                hr = _audioClientGetStreamLatency(_audioClientPtr, out long latency100ns);
                if (hr >= 0)
                {
                    _streamLatencySec = latency100ns / 10_000_000.0;
                    _logger.LogInformation("[WASAPI-LATENCY] GetStreamLatency={LatencyMs:F1}ms（主时钟将减去此值以对齐可闻位置）",
                        _streamLatencySec * 1000.0);
                }
                else
                {
                    _logger.LogWarning("IAudioClient.GetStreamLatency 失败：HRESULT=0x{HR:X8}（主时钟不校准）", hr);
                }
            }

            // 6. 获取 IAudioRenderClient
            var iidRender = WasapiInterop.IID_IAudioRenderClient;
            hr = _audioClientGetService(_audioClientPtr, ref iidRender, out IntPtr pRenderClient);
            if (hr < 0)
            {
                // 显式 COMException 替代 Marshal.ThrowExceptionForHR：后者内部依赖 GetErrorInfo，
                // 在无头/虚拟音频会话（COM 错误子系统不完备）下会抛 InvalidCastException 等诡异异常。
                _logger.LogError("IAudioClient.GetService(IAudioRenderClient) 失败：HRESULT=0x{HR:X8}", hr);
                throw new COMException("IAudioClient.GetService(IAudioRenderClient) 失败。", hr);
            }

            _renderClientPtr = pRenderClient;
            _renderClientGetBuffer = ComVTable.Get<IAudioRenderClient_GetBuffer>(pRenderClient, 0);
            _renderClientReleaseBuffer = ComVTable.Get<IAudioRenderClient_ReleaseBuffer>(pRenderClient, 1);

            // 7. 获取 ISimpleAudioVolume（音量控制）
            var iidVolume = WasapiInterop.IID_ISimpleAudioVolume;
            hr = _audioClientGetService(_audioClientPtr, ref iidVolume, out IntPtr pVolume);
            if (hr >= 0)
            {
                _simpleVolumePtr = pVolume;
                _simpleVolumeSetMasterVolume = ComVTable.Get<ISimpleAudioVolume_SetMasterVolume>(pVolume, 0);
            }
            else
            {
                _logger.LogWarning("无法获取 ISimpleAudioVolume（HRESULT=0x{HR:X8}），音量控制不可用。", hr);
            }

            // 8. 获取 IAudioClock（播放位置查询）
            var iidClock = WasapiInterop.IID_IAudioClock;
            hr = _audioClientGetService(_audioClientPtr, ref iidClock, out IntPtr pClock);
            if (hr >= 0)
            {
                _audioClockPtr = pClock;
                // IAudioClock vtable: IUnknown(0-2) + GetFrequency(slot0) + GetPosition(slot1) + GetCharacteristics(slot2)
                // GetPosition 在 slot 1，索引必须为 1（此前误用 2 会调用 GetCharacteristics 返回垃圾值）
                _audioClockGetFrequency = ComVTable.Get<IAudioClock_GetFrequency>(pClock, 0);
                _audioClockGetPosition = ComVTable.Get<IAudioClock_GetPosition>(pClock, 1);

                // ⚠️ 审计修复（2026-07-31）：频率在流的生命周期内恒定，初始化时取一次即可。
                int freqHr = _audioClockGetFrequency(_audioClockPtr, out ulong clockFrequency);
                if (freqHr >= 0 && clockFrequency > 0)
                {
                    _audioClockFrequency = (long)clockFrequency;
                    _logger.LogDebug("IAudioClock 设备频率={Frequency} units/s（客户端 {SampleRate}Hz/{Channels}ch）",
                        clockFrequency, sampleRate, channels);
                }
                else
                {
                    _audioClockFrequency = 0;
                    _logger.LogWarning(
                        "IAudioClock.GetFrequency 失败（HRESULT=0x{HR:X8}，freq={Frequency}），" +
                        "播放位置将回落到按采样率换算，可能不准确。", freqHr, clockFrequency);
                }
            }
            else
            {
                _logger.LogWarning("无法获取 IAudioClock（HRESULT=0x{HR:X8}），播放位置查询不可用。", hr);
            }

            // 9. 应用初始音量
            if (_simpleVolumePtr != IntPtr.Zero)
            {
                var ec = Guid.Empty;
                hr = _simpleVolumeSetMasterVolume!(_simpleVolumePtr, _volume, ref ec);
                if (hr < 0)
                    _logger.LogWarning("设置初始音量失败：HRESULT=0x{HR:X8}", hr);
            }

            _initialized = true;
            _logger.LogDebug("WASAPI 输出已初始化：{SampleRate}Hz, {Channels}ch, 格式={Format}, 缓冲={BufferSize}帧 ({BufferMs:F1}ms), 事件驱动={EventDriven}",
                sampleRate, channels, _deviceSampleFormat, _bufferSize,
                (double)_bufferSize / sampleRate * 1000, _eventDrivenMode);
        }
        catch
        {
            // Initialize 失败时仅清理 Initialize 创建的 COM 对象（_audioClient/_renderClient/_simpleVolume/_audioClock），
            // 不释放 _device/_enumerator（它们由 InitializeAsync 创建，保留以便用户重试 Initialize）。
            ReleaseInitializeObjects();
            if (_bufferEvent != null)
            {
                _bufferEvent.Dispose();
                _bufferEvent = null;
            }
            _bufferSize = 0;
            _sampleRate = 0;
            _channels = 0;
            _deviceSampleFormat = SampleFormat.F32;
            // 审计修复：重置 _eventDrivenMode 到用户配置值。
            _eventDrivenMode = _options.EventDrivenMode;
            throw;
        }
    }

    /// <summary>
    /// 单帧提交核心逻辑（在渲染线程上下文内执行）。
    /// 仅做原生缓冲区写入，不归还帧所有权（归还由调用方负责）。
    /// 背压超时/参数异常会抛出（调用方据此判定丢帧）。
    /// </summary>
    private void WriteFrame(AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _audioClientPtr == IntPtr.Zero || _renderClientPtr == IntPtr.Zero)
            throw new InvalidOperationException("WASAPI 输出尚未初始化，无法 Submit。");

        // 验证声道数匹配（管线应保证一致，不匹配是管线bug）
        if (frame.Channels != _channels)
        {
            throw new ArgumentException(
                $"音频帧声道数 {frame.Channels} 与输出配置 {_channels} 不匹配。", nameof(frame));
        }

        // 计算每样本字节数（基于输入帧格式）
        int bytesPerSample = frame.SampleFormat switch
        {
            SampleFormat.S16 => 2,
            SampleFormat.S32 => 4,
            SampleFormat.F32 => 4,
            _ => throw new NotSupportedException($"不支持的采样格式：{frame.SampleFormat}")
        };

        int sampleCount = frame.FrameCount * frame.Channels;
        int expectedDataSize = sampleCount * bytesPerSample;

        // 验证数据大小
        if (frame.Data.Length < expectedDataSize)
        {
            throw new ArgumentException(
                $"音频帧数据不足：期望 {expectedDataSize} 字节，实际 {frame.Data.Length} 字节。", nameof(frame));
        }

        // gotBuffer 标记 GetBuffer 是否已成功锁定缓冲区；released 标记是否已正常释放。
        bool gotBuffer = false;
        bool released = false;

        // 等待缓冲区有足够空间（COM 背压）；超时/参数异常计为丢帧（被上层吞掉前统计，用于诊断卡顿根因）
        try
        {
            WaitForBufferSpace((uint)frame.FrameCount);
        }
        catch (Exception ex) when (ex is TimeoutException or ArgumentException)
        {
            _droppedFrames++;
            throw;
        }

        // 获取 WASAPI 缓冲区指针
        int hr = _renderClientGetBuffer!(_renderClientPtr, (uint)frame.FrameCount, out IntPtr pData);
        Marshal.ThrowExceptionForHR(hr);
        gotBuffer = true;

        try
        {
            var validSrc = frame.Data.Span[..expectedDataSize];

            unsafe
            {
                CopyOrConvert(validSrc, pData, sampleCount, frame.SampleFormat, _deviceSampleFormat);
            }

            hr = _renderClientReleaseBuffer!(_renderClientPtr, (uint)frame.FrameCount, 0);
            Marshal.ThrowExceptionForHR(hr);
            released = true;
            _submittedSamples += frame.FrameCount;   // 诊断：累计成功提交的采样帧数
            _submittedFrames++;

            // 治本①（起播静默窗）：preroll 阶段写满设备缓冲后自动启动引擎，
            // 抓取的是真实 PCM 而非静音 → 起播无静默窗。仅触发一次。
            if (_prerollPending && !_prerollStarted && GetCurrentPaddingFrames() >= _primeFrames)
            {
                int startHr = _audioClientStart!(_audioClientPtr);
                if (startHr >= 0)
                {
                    CaptureStartAnchor();
                    _prerollStarted = true;
                    _prerollPending = false;
                    _primeTcs?.TrySetResult(true);
                }
            }
        }
        finally
        {
            // 配对规则（WASAPI 强制）：ReleaseBuffer 必须紧跟成功的 GetBuffer，且仅一次。
            if (gotBuffer && !released)
            {
                try { _renderClientReleaseBuffer!(_renderClientPtr, 0, WasapiInterop.AUDCLNT_BUFFERFLAGS_SILENT); }
                catch { /* 尽力释放，忽略二次异常 */ }
            }
        }
    }

    // ── V2 格式协商方法（O7 独占模式 + O9 多格式直出）──

    /// <summary>
    /// 共享模式格式协商：通过 GetMixFormat 获取设备原生格式。
    /// </summary>
    private WAVEFORMATEX NegotiateSharedFormat(int sampleRate, int channels)
    {
        // 1. 获取设备原生混音格式
        int hr = _audioClientGetMixFormat!(_audioClientPtr, out IntPtr pMixFormat);
        if (hr < 0 || pMixFormat == IntPtr.Zero)
        {
            if (pMixFormat != IntPtr.Zero)
                WasapiInterop.CoTaskMemFree(pMixFormat);
            _logger.LogWarning("GetMixFormat 失败 (HRESULT=0x{HR:X8})，回退到 F32 格式", hr);
            _deviceSampleFormat = SampleFormat.F32;
            // 审计修复（2026-07-31）：mix 参数未知时记为客户端参数，避免后续日志出现 0Hz/0ch 误导。
            _mixSampleRate = sampleRate;
            _mixChannels = channels;
            return BuildWaveFormat(sampleRate, channels, SampleFormat.F32);
        }

        try
        {
            // 2. 解析设备原生 mix format
            var mix = Marshal.PtrToStructure<WAVEFORMATEX>(pMixFormat);
            _mixSampleRate = (int)mix.nSamplesPerSec;
            _mixChannels = mix.nChannels;
            _deviceSampleFormat = ParseSampleFormat(pMixFormat);

            // 3. 如果指定了 PreferredSampleFormat 且与设备格式不同，尝试 IsFormatSupported
            if (_options.PreferredSampleFormat.HasValue &&
                _options.PreferredSampleFormat.Value != _deviceSampleFormat)
            {
                var preferred = _options.PreferredSampleFormat.Value;
                var preferredFormat = BuildWaveFormat(sampleRate, channels, preferred);

                unsafe
                {
                    // 审计修复：ppClosestMatch 传 IntPtr.Zero（按值），避免 WASAPI 分配 CoTaskMem 内存后泄漏
                    hr = _audioClientIsFormatSupported!(
                        _audioClientPtr,
                        WasapiInterop.AUDCLNT_SHAREMODE_SHARED,
                        (IntPtr)(&preferredFormat),
                        IntPtr.Zero);
                }

                if (hr == WasapiInterop.S_OK)
                {
                    _logger.LogDebug("共享模式：设备支持首选格式 {Preferred}（覆盖设备原生格式 {Native}）",
                        preferred, _deviceSampleFormat);
                    _deviceSampleFormat = preferred;
                    return preferredFormat;
                }

                _logger.LogDebug("共享模式：设备不支持首选格式 {Preferred} (HRESULT=0x{HR:X8})，使用设备原生格式 {Native}",
                    preferred, hr, _deviceSampleFormat);
            }

            // 4. 共享模式：以【客户端（解码器）采样率 / 声道数】+ 设备 mix 的采样格式打开。
            // ⚠️ 审计修复（2026-07-31，真 bug）：原返回 BuildWaveFormat(_mixSampleRate, _mixChannels, ...)，
            //    即拿设备 mix format 打开设备，而 Submit 侧按解码器格式写入 → 44.1kHz 解码流被 48kHz 设备按 48kHz 播放
            //    （音高偏高约 8.8%）。修法：格式改回客户端参数 + 共享模式加 AUTOCONVERTPCM|SRC_DEFAULT_QUALITY。
            if (sampleRate != _mixSampleRate || channels != _mixChannels)
            {
                _logger.LogDebug(
                    "共享模式：客户端格式 {SampleRate}Hz/{Channels}ch 与设备 mix {MixRate}Hz/{MixChannels}ch 不一致，" +
                    "将启用 AUTOCONVERTPCM 由音频引擎重采样 / 混音。",
                    sampleRate, channels, _mixSampleRate, _mixChannels);
            }

            return BuildWaveFormat(sampleRate, channels, _deviceSampleFormat);
        }
        finally
        {
            WasapiInterop.CoTaskMemFree(pMixFormat);
        }
    }

    /// <summary>
    /// 独占模式格式协商：通过 IsFormatSupported 逐一尝试格式。
    /// </summary>
    private WAVEFORMATEX NegotiateExclusiveFormat(int sampleRate, int channels)
    {
        var tried = new HashSet<SampleFormat>();
        var formatsToTry = new List<SampleFormat>(4);
        if (_options.PreferredSampleFormat.HasValue)
            formatsToTry.Add(_options.PreferredSampleFormat.Value);
        formatsToTry.Add(SampleFormat.F32);
        formatsToTry.Add(SampleFormat.S32);
        formatsToTry.Add(SampleFormat.S16);

        foreach (var format in formatsToTry)
        {
            if (!tried.Add(format))
                continue;

            var wfx = BuildWaveFormat(sampleRate, channels, format);

            unsafe
            {
                int hr = _audioClientIsFormatSupported!(
                    _audioClientPtr,
                    WasapiInterop.AUDCLNT_SHAREMODE_EXCLUSIVE,
                    (IntPtr)(&wfx),
                    IntPtr.Zero);

                if (hr == WasapiInterop.S_OK)
                {
                    _logger.LogDebug("独占模式：设备支持格式 {Format}", format);
                    _deviceSampleFormat = format;
                    return wfx;
                }
            }
        }

        _deviceSampleFormat = SampleFormat.F32;
        throw new NotSupportedException(
            $"独占模式下设备不支持任何可用格式（F32/S32/S16 {sampleRate}Hz {channels}ch）。" +
            "请尝试共享模式或调整采样率/声道数。");
    }

    /// <summary>
    /// 构建指定格式的 WAVEFORMATEX 结构体。
    /// </summary>
    internal static WAVEFORMATEX BuildWaveFormat(int sampleRate, int channels, SampleFormat format)
    {
        ushort bitsPerSample = format switch
        {
            SampleFormat.S16 => 16,
            SampleFormat.S32 => 32,
            SampleFormat.F32 => 32,
            _ => 32
        };

        ushort formatTag = format switch
        {
            SampleFormat.F32 => WasapiInterop.WAVE_FORMAT_IEEE_FLOAT,
            _ => WasapiInterop.WAVE_FORMAT_PCM
        };

        return new WAVEFORMATEX
        {
            wFormatTag = formatTag,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = bitsPerSample,
            nBlockAlign = (ushort)(channels * (bitsPerSample / 8)),
            nAvgBytesPerSec = (uint)(sampleRate * channels * (bitsPerSample / 8)),
            cbSize = 0
        };
    }

    /// <summary>
    /// 从 WAVEFORMATEX 指针解析采样格式（支持 PCM / IEEE_FLOAT / EXTENSIBLE）。
    /// </summary>
    internal static SampleFormat ParseSampleFormat(IntPtr pFormat)
    {
        if (pFormat == IntPtr.Zero)
            return SampleFormat.F32;

        var wfx = Marshal.PtrToStructure<WAVEFORMATEX>(pFormat);

        if (wfx.wFormatTag == WasapiInterop.WAVE_FORMAT_IEEE_FLOAT)
            return SampleFormat.F32;

        if (wfx.wFormatTag == WasapiInterop.WAVE_FORMAT_PCM)
        {
            return wfx.wBitsPerSample switch
            {
                16 => SampleFormat.S16,
                32 => SampleFormat.S32,
                _ => SampleFormat.F32
            };
        }

        if (wfx.wFormatTag == WasapiInterop.WAVE_FORMAT_EXTENSIBLE && wfx.cbSize >= 22)
        {
            var wfex = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(pFormat);
            if (wfex.SubFormat == WasapiInterop.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                return SampleFormat.F32;
            if (wfex.SubFormat == WasapiInterop.KSDATAFORMAT_SUBTYPE_PCM)
            {
                return wfx.wBitsPerSample switch
                {
                    16 => SampleFormat.S16,
                    32 => SampleFormat.S32,
                    _ => SampleFormat.F32
                };
            }
        }

        return SampleFormat.F32;
    }

    // ── V2 PCM 拷贝/转换方法（O9 多格式直出）──

    /// <summary>
    /// 将源 PCM 数据拷贝或转换到 WASAPI 缓冲区。格式匹配时零转换直接拷贝。
    /// </summary>
    internal static unsafe void CopyOrConvert(
        ReadOnlySpan<byte> src, IntPtr dstPtr, int sampleCount,
        SampleFormat srcFormat, SampleFormat dstFormat)
    {
        if (srcFormat == dstFormat)
        {
            var dst = new Span<byte>((void*)dstPtr, sampleCount * GetBytesPerSample(dstFormat));
            src.CopyTo(dst);
            return;
        }

        if (dstFormat == SampleFormat.F32)
        {
            var dst = new Span<float>((void*)dstPtr, sampleCount);
            if (srcFormat == SampleFormat.S16)
            {
                var srcTyped = MemoryMarshal.Cast<byte, short>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = srcTyped[i] / 32768.0f;
            }
            else
            {
                var srcTyped = MemoryMarshal.Cast<byte, int>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = srcTyped[i] / 2147483648.0f;
            }
        }
        else if (dstFormat == SampleFormat.S16)
        {
            var dst = new Span<short>((void*)dstPtr, sampleCount);
            if (srcFormat == SampleFormat.F32)
            {
                var srcTyped = MemoryMarshal.Cast<byte, float>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = (short)Math.Clamp(srcTyped[i] * 32768f, -32768f, 32767f);
            }
            else
            {
                var srcTyped = MemoryMarshal.Cast<byte, int>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = (short)(srcTyped[i] >> 16);
            }
        }
        else
        {
            var dst = new Span<int>((void*)dstPtr, sampleCount);
            if (srcFormat == SampleFormat.F32)
            {
                var srcTyped = MemoryMarshal.Cast<byte, float>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = (int)Math.Clamp(srcTyped[i] * 2147483648.0, -2147483648.0, 2147483647.0);
            }
            else
            {
                var srcTyped = MemoryMarshal.Cast<byte, short>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = srcTyped[i] << 16;
            }
        }
    }

    /// <summary>获取采样格式的每样本字节数。</summary>
    internal static int GetBytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.S16 => 2,
        SampleFormat.S32 => 4,
        SampleFormat.F32 => 4,
        _ => 4
    };

    /// <summary>
    /// 通过 IAudioClient2.SetClientProperties 设置音频会话分类，防止 Windows 挂起后台/非前台/隐藏窗口的会话。
    /// 必须在 IAudioClient.Initialize 之前调用。任何失败均记录日志并静默跳过，不影响正常初始化。
    /// </summary>
    /// <remarks>
    /// IAudioClient2 由 IAudioClient 指针 QI 获得（vtable 绝对槽 0 = IUnknown.QueryInterface）。
    /// 🔴 vtable 绝对槽位（逐方法照抄 audioclient.h，勿凭记忆推算）：
    ///   IUnknown      : QueryInterface(0) AddRef(1) Release(2)
    ///   IAudioClient  : Initialize(3) GetBufferSize(4) GetStreamLatency(5) GetCurrentPadding(6)
    ///                   IsFormatSupported(7) GetMixFormat(8) GetDevicePeriod(9) Start(10)
    ///                   Stop(11) Reset(12) SetEventHandle(13) GetService(14)   —— 共 12 个方法
    ///   IAudioClient2 : IsOffloadCapable(15) SetClientProperties(16) GetBufferSizeLimits(17)
    /// 故 SetClientProperties 绝对槽 = 16，ComVTable slotIndex = 16 - 3 = 13。
    /// ⚠️ 曾误写为 slotIndex=12（绝对槽 15），实际调到 IsOffloadCapable —— 它有 3 个参数
    /// (self, Category, BOOL* pbOffloadCapable)，我们只传 2 个，x64 下 R8 是未初始化垃圾值，
    /// 原生侧向该野地址写 BOOL ⇒ 确定性 0xC0000005。官方 [ComImport] 探针九个分类全 S_OK 已反证 driver 无恙。
    /// 释放时调用 IUnknown.Release（绝对槽 2）。
    /// </remarks>
    private void TrySetSessionCategory(IntPtr audioClientPtr)
    {
        try
        {
            if (_options.SessionCategory == AudioClientCategory.Other)
                return; // 用户显式选择不设置

            // QI 用 BCL Marshal.QueryInterface（稳健，避免手搓 vtable 调用出错）。
            // 仅在 IAudioClient2 可用时继续；否则静默跳过（退回旧行为）。
            var iid2 = WasapiInterop.IID_IAudioClient2;
            int hrQi = Marshal.QueryInterface(audioClientPtr, in iid2, out IntPtr pClient2);
            if (hrQi < 0 || pClient2 == IntPtr.Zero)
            {
                _logger.LogDebug("IAudioClient2 不可用（HRESULT=0x{HR:X8}），跳过会话分类设置", hrQi);
                return;
            }

            try
            {
                // 🔴 SetClientProperties 绝对槽 = 16（IUnknown 3 + IAudioClient 12 方法占 3..14
                // + IAudioClient2 首方法 IsOffloadCapable 占 15），故 slotIndex = 16 - 3 = 13。
                // 绝不能是 12（=IsOffloadCapable，参数个数不同，误调 ⇒ 野指针写 ⇒ 0xC0000005）。
                // 取函数指针后判空：若槽位异常（理论上不会），跳过而非崩进程。
                IntPtr setPropsPtr = ComVTable.GetMethodPointer(pClient2, 13);
                if (setPropsPtr == IntPtr.Zero)
                {
                    _logger.LogWarning("IAudioClient2.SetClientProperties 槽位为空，跳过会话分类设置");
                    return;
                }
                var setProps = Marshal.GetDelegateForFunctionPointer<IAudioClient2_SetClientProperties>(setPropsPtr);

                // 候选分类链：优先用户配置值，其次同族媒体类兜底（某些 driver 只认部分分类）。
                // 历史注记：曾因 vtable 槽位算错（调到 IsOffloadCapable）导致任意分类都 0xC0000005，
                // 一度误判为「driver 对 BackgroundCapableMedia 损坏」并将其排除；槽位修正后
                // 官方 COM 探针九个分类全部 S_OK，该规避已撤销。
                var candidates = new System.Collections.Generic.List<AudioClientCategory>(4)
                {
                    _options.SessionCategory
                };
                foreach (var m in new[] { AudioClientCategory.BackgroundCapableMedia, AudioClientCategory.Movie, AudioClientCategory.Media })
                    if (!candidates.Contains(m)) candidates.Add(m);

                bool categorySet = false;
                foreach (var cat in candidates)
                {
                    var props = new AudioClientProperties
                    {
                        cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(), // = 16（含 bIsOffload）
                        bIsOffload = 0, // 🔴 必须 FALSE：本库走常规共享模式；置 TRUE 会申请硬件卸载流并崩溃
                        eCategory = cat,
                        eStreamOptions = 0
                    };
                    _logger.LogDebug("[DIAG-SETCLIENT] 试设会话分类：pClient2=0x{P2:X}, setPropsPtr=0x{SP:X}, cbSize={CB}, bIsOffload={OFF}, eCategory={CAT}",
                        pClient2, setPropsPtr, props.cbSize, props.bIsOffload, props.eCategory);
                    int hrSet = setProps(pClient2, ref props);
                    if (hrSet < 0)
                    {
                        _logger.LogWarning("SetClientProperties({Category}) 失败（HRESULT=0x{HR:X8}），尝试下一个候选", cat, hrSet);
                        continue;
                    }
                    _logger.LogDebug("WASAPI 会话分类已设为 {Category}", cat);
                    categorySet = true;
                    break;
                }
                if (!categorySet)
                    _logger.LogWarning("所有候选会话分类均失败，会话分类未设置（后台会话被挂起时声音可能中断）");
            }
            finally
            {
                // 释放 QI 增加的引用：BCL Marshal.Release（IUnknown.Release）。
                Marshal.Release(pClient2);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "设置 WASAPI 会话分类异常，已跳过");
        }
    }

    /// <summary>
    /// 等待 WASAPI 缓冲区有足够空间（COM 背压）。事件驱动模式使用 EventWaitHandle.WaitOne 替代 Sleep 轮询。
    /// </summary>
    /// <summary>
    /// 读取设备缓冲里「尚未渲染到 DAC」的帧数（= 真实可闻延迟的帧表示）。
    /// 主时钟据此减去 pending 帧对应的秒数，得到用户此刻实际听到的位置。
    /// </summary>
    private uint GetCurrentPaddingFrames()
    {
        var clientPtr = Volatile.Read(ref _audioClientPtr);
        var getPadding = Volatile.Read(ref _audioClientGetCurrentPadding);
        if (clientPtr == IntPtr.Zero || getPadding is null)
            return 0;
        int hr = getPadding(clientPtr, out uint padding);
        return hr < 0 ? 0 : padding;
    }

    private void WaitForBufferSpace(uint requiredFrames, CancellationToken ct = default)
    {
        if (_audioClientPtr == IntPtr.Zero) return;

        if (requiredFrames > (uint)_bufferSize)
        {
            throw new ArgumentException(
                $"音频帧大小（{requiredFrames} 帧）超过 WASAPI 缓冲区总大小（{_bufferSize} 帧），" +
                "请减小帧大小或增大缓冲区时长。");
        }

        var sw = Stopwatch.StartNew();
        const int timeoutMs = 2000;

        while (true)
        {
            // 关闭中：立即放弃缓冲等待，残留帧在关闭期被跳过（Dispose 不再为残留帧卡 2s 超时）
            if (_shutdownEvent.IsSet)
                throw new OperationCanceledException("WASAPI 渲染线程关闭中，跳过缓冲等待");

            int hr = _audioClientGetCurrentPadding!(_audioClientPtr, out uint padding);
            Marshal.ThrowExceptionForHR(hr);

            uint available = (uint)_bufferSize - padding;
            if (available >= requiredFrames)
                return;

            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException(
                    $"WASAPI 缓冲区等待超时（{timeoutMs}ms），音频设备可能已停止或卡死。" +
                    $"需要 {requiredFrames} 帧，可用 {available} 帧。");
            }

            if (_bufferEvent != null)
            {
                int remainingMs = (int)(timeoutMs - sw.ElapsedMilliseconds);
                if (remainingMs <= 0)
                    break;
                // 同时等待缓冲事件与关闭信号：关闭置位时立即返回（上方检查抛出异常）
                WaitHandle.WaitAny(new WaitHandle[] { _bufferEvent, _shutdownEvent.WaitHandle }, remainingMs);
            }
            else
            {
                Thread.Sleep(1);
            }
        }

        int hrFinal = _audioClientGetCurrentPadding!(_audioClientPtr, out uint finalPadding);
        Marshal.ThrowExceptionForHR(hrFinal);
        if ((uint)_bufferSize - finalPadding < requiredFrames)
        {
            throw new TimeoutException(
                $"WASAPI 缓冲区等待超时（{timeoutMs}ms），音频设备可能已停止或卡死。" +
                $"需要 {requiredFrames} 帧，可用 {(uint)_bufferSize - finalPadding} 帧。");
        }
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

    /// <summary>
    /// 仅释放 Initialize 创建的 COM 对象（不含 _device/_enumerator），用于 Initialize 失败清理。
    /// </summary>
    private void ReleaseInitializeObjects()
    {
        if (_audioClientPtr != IntPtr.Zero && _audioClientStop is not null)
        {
            try { _audioClientStop(_audioClientPtr); }
            catch { }
        }

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

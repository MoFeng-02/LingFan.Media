using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// WASAPI 渲染线程（STA 独占），<see cref="WasapiOutput"/> 的实际执行体：
/// 把全部 WASAPI COM 调用收进一个常驻 STA 线程，消除逐帧 <c>RunOnSta</c> 跨线程封送开销。
/// </summary>
/// <remarks>
/// <para><b>线程模型</b>：构造后由 <see cref="InitializeAsync"/> 启动唯一 STA 线程（COINIT_APARTMENTTHREADED）。
/// 该线程在 <c>CoInitializeEx</c> 之后进入渲染循环：消费控制消息（Initialize/Pause/...）与音频帧，
/// 所有 COM 调用（CoCreateInstance/GetMixFormat/Initialize/GetBuffer/ReleaseBuffer/GetPosition）均在同一线程。
/// WASAPI 要求 IAudioClient 在 STA 公寓创建与使用，MTA 下 GetMixFormat/Initialize 会触发原生访问违规（ACCESS_VIOLATION），
/// 故 STA 线程唯一性不可破坏。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，内部启动 STA 线程 + 在渲染线程执行 InitializeCore，返回 <see cref="Task.CompletedTask"/>。
/// CoInitializeEx + COM 设备枚举均为同步 COM 调用，无 I/O 可 await，非伪异步。</item>
/// <item><see cref="Initialize"/>：同步（纯 COM 调用，无 I/O），在渲染线程执行 IAudioClient + IAudioRenderClient 创建 +
/// 格式协商（GetMixFormat / IsFormatSupported）+ 事件驱动初始化（SetEventHandle）。</item>
/// <item><see cref="Submit"/>/<see cref="SubmitBatch"/>：同步边界（原生 COM 背压），将帧交给渲染线程消费（阻塞等待该帧写入完成，
/// 故调用方在 Submit 返回后可安全归还帧所有权）；缓冲满时由渲染线程的 COM 背压等待，
/// 调用方无需跨线程往返。</item>
/// <item><see cref="Pause"/>/<see cref="Resume"/>/<see cref="Flush"/>：同步，渲染线程执行 IAudioClient.Stop/Start/Reset。</item>
/// <item><see cref="GetPlaybackPosition"/>：同步，渲染线程执行 IAudioClock.GetPosition。</item>
/// <item><see cref="Dispose"/>：同步快速释放，向渲染线程投递 Shutdown 消息触发 COM 释放 + CoUninitialize，释放事件句柄。</item>
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
    // 改为以墙钟为锚：起播瞬态过后（Clock 段 CalibMinSec = 300ms）锁定 bias = rawSec - wallElapsed（引擎领先+常偏），
    // 主时钟减此值即得真实可闻位置（≈墙钟，与视频 PTS 同源）。三者跨线程（渲染线程写、时钟线程读）均用 Volatile 访问。
    private System.Diagnostics.Stopwatch? _startStopwatch;
    private double _calibratedBias;
    private bool _biasLatched;
    // 重播（Ended→Playing）主时钟武装标志：音频尚未为本遍播放 Start 前，GetPlaybackPositionDirect 须返回 0
    // （设备尚未起跑，此时读出的游标是上一遍播放残留的陈旧值）。Resume/BeginStreaming 自动 Start 时经
    // CaptureStartAnchor 置 true；重播起点经 ResetPlaybackClock 置 false。缺此标志时，重播预滚动/门控窗口
    // 会沿用第一遍播放的 Start 锚点返回陈旧值，导致重播 PTS=0 首帧被错判落后、遭 Drop，画面冻结较长时间才切入。
    private volatile bool _clockArmed;
    // 音频时钟只暴露纯可闻位置，不做任何前移补偿；
    // LINGFAN_SYNC_LEAD_MS 的提前量补偿由 VideoPipeline 作用在「呈现延迟」变量上。
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
    // 仅用于诊断日志与 AUTOCONVERTPCM 判断；初始化 WAVEFORMATEX 一律用客户端（解码器）
    // 采样率/声道数——若改用设备速率，解码器格式的帧会被按错误速率播出。详见 NegotiateSharedFormat。
    private int _mixSampleRate;
    private int _mixChannels;

    // IAudioClock 的设备频率（units/秒）。GetPosition 的返回值须除以它才是秒数，
    // 单位由设备定义（共享模式常见为字节/秒 = nSamplesPerSec * nBlockAlign），不等于采样率。
    // 0 表示不可用（GetFrequency 失败），此时 GetPlaybackPosition 回落到采样率换算并告警。
    private long _audioClockFrequency;

    // STA 渲染线程基础设施：常驻渲染循环，避免每次调用 RunOnSta 的跨线程封送
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
                // 渲染线程得以快速处理完队列并进入 Shutdown，避免退出时长时间等待。
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
        // 经 RenderThreadProc finally → CoUninitialize 正确反初始化，避免 COM 单元状态跨实例泄漏。

        if (_submittedFrames > 0 || _droppedFrames > 0)
        {
            double approxSec = _sampleRate > 0 ? (double)_submittedSamples / _sampleRate : 0.0;
            _logger.LogWarning(
                "[WASAPI-DIAG] submittedFrames={SubmittedFrames} submittedSamples={SubmittedSamples} approxSeconds={ApproxSeconds:F2} droppedFrames={DroppedFrames} bufferSize={BufferSize} sampleRate={SampleRate}",
                _submittedFrames, _submittedSamples, approxSec, _droppedFrames, _bufferSize, _sampleRate);
            // 双输出：ILogger 未接入可见输出通道时，用 Console 兜底确保诊断信息可见
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

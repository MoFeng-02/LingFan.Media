using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// WASAPI 音频输出。通过 P/Invoke 直接调用 Windows WASAPI COM 接口。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <see cref="Task.CompletedTask"/>。
/// CoInitializeEx + COM 设备枚举均为同步 COM 调用，无 I/O 可 await，非伪异步。</item>
/// <item><see cref="Initialize"/>：同步（sync 分类），IAudioClient + IAudioRenderClient 创建 +
/// V2 格式协商（GetMixFormat / IsFormatSupported）+ V2 事件驱动初始化（SetEventHandle）。</item>
/// <item><see cref="Submit"/>：同步边界（native 分类），COM GetBuffer + 拷贝/转换 + ReleaseBuffer，缓冲满时阻塞（COM 背压）。
/// V2 多格式直出：帧格式与设备格式匹配时零转换直拷。</item>
/// <item><see cref="Pause"/>/<see cref="Resume"/>/<see cref="Flush"/>：同步（sync 分类），IAudioClient.Stop/Start/Reset。</item>
/// <item><see cref="GetPlaybackPosition"/>：同步（sync 分类），IAudioClock.GetPosition。</item>
/// <item><see cref="Dispose"/>：同步快速释放（sync 分类），封送释放 COM 对象到内部 STA 线程，并停止 STA 队列触发 CoUninitialize，释放事件句柄。</item>
/// <item><see cref="DisposeAsync"/>：接口契约，委托 <see cref="Dispose"/> + <see cref="ValueTask.CompletedTask"/>。
/// COM 释放为快速同步调用，无 I/O 可 await，非伪异步。</item>
/// </list>
    /// <para><b>线程安全</b>：非线程安全。Submit 在音频管线线程调用，Pause/Resume/Flush 在控制线程调用，
    /// 不可并发。所有 COM 调用经内部专用 STA 工作线程（COINIT_APARTMENTTHREADED）封送——WASAPI 要求
    /// IAudioClient 在 STA 公寓创建与使用，MTA 下 GetMixFormat/Initialize 会触发 native AV（0xC0000005）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，采用原始 vtable P/Invoke（ComVTable 委托封送），不使用 [ComImport]/RCW，NativeAOT 兼容。</para>
/// <para><b>资源所有权</b>：IMMDeviceEnumerator/IMMDevice/IAudioClient/IAudioRenderClient/ISimpleAudioVolume/IAudioClock
/// 的原生指针均由本类持有（Session 级），Dispose 时通过 Marshal.Release(IntPtr) 逆序释放。
/// V2 事件句柄（EventWaitHandle）由本类创建并持有，Dispose 时释放。</para>
/// <para><b>Submit 所有权</b>：V2 变更——Submit 不再接管帧所有权，不 Dispose 帧。
/// 调用方（AudioPipeline）负责 Return 到 FramePool 或 Dispose。</para>
/// <para><b>V2 增强（Task-V2-13）</b>：</para>
/// <list type="bullet">
/// <item>O7 独占模式完善：IsFormatSupported 格式协商 + AUDCLNT_E_DEVICE_IN_USE/UNSUPPORTED_FORMAT 错误处理</item>
/// <item>O8 事件驱动模式：AUDCLNT_STREAMFLAGS_EVENTCALLBACK + SetEventHandle + EventWaitHandle.WaitOne 替代 Thread.Sleep 轮询</item>
/// <item>O9 多格式直出：GetMixFormat 检测设备原生格式 + S16/S32/F32 直出（匹配时零转换拷贝）</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WasapiOutput : IAudioOutput
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

    // 状态
    private bool _initialized;
    private bool _disposed;
    private int _bufferSize;      // WASAPI 缓冲区大小（帧数）
    private int _sampleRate;
    private int _channels;
    private float _volume = 1.0f;

    // V2: 事件驱动模式
    private EventWaitHandle? _bufferEvent;

    // V2: 设备原生采样格式（Initialize 时检测，Submit 时用于直出判断）
    private SampleFormat _deviceSampleFormat = SampleFormat.F32;

    // V2: 设备原生 mix format 的采样率/声道数（GetMixFormat 检测，共享模式初始化用）。
    // 共享模式下系统负责重采样到该格式，故初始化 WAVEFORMATEX 必须用它而非解码器输出格式。
    private int _mixSampleRate;
    private int _mixChannels;

    // STA 线程封送（WASAPI 要求 IAudioClient 在 STA 公寓使用；MTA 下 GetMixFormat/Initialize 会触发 native AV）。
    // 所有 COM 调用经内部 STA 工作线程封送，对外接口签名不变（MediaPlayer 调用方无需感知线程模型）。
    private Thread? _staThread;
    private BlockingCollection<StaWorkItem>? _staQueue;
    private readonly ManualResetEventSlim _staStarted = new(false);

    /// <summary>
    /// 初始化 <see cref="WasapiOutput"/> 的新实例。
    /// </summary>
    /// <param name="options">WASAPI 配置选项。</param>
    /// <param name="logger">日志器。</param>
    internal WasapiOutput(WasapiOptions options, ILogger<WasapiOutput> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _exclusiveMode = options.ExclusiveMode;
        _eventDrivenMode = options.EventDrivenMode;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：CoInitializeEx + COM 设备枚举均为同步 COM 调用，无 I/O 可 await。
    /// 同步执行后返回 <see cref="Task.CompletedTask"/>，非伪异步。
    /// </remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureStaThread();
        RunOnSta(InitializeCore);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels)
    {
        RunOnSta(() =>
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
            // ComVTable.Get 的 slotIndex 为「相对 IUnknown 的方法索引」（IUnknown 占用 vtable 绝对槽位 0-2，故绝对槽位 = 3 + slotIndex）。
            // 标准 IAudioClient vtable（audioclient.h 官方声明顺序，IUnknown 之后相对索引）：
            //   0 Initialize | 1 GetBufferSize | 2 GetStreamLatency(未使用) | 3 GetCurrentPadding
            //   | 4 IsFormatSupported | 5 GetMixFormat | 6 GetDevicePeriod(未使用)
            //   | 7 Start | 8 Stop | 9 Reset | 10 SetEventHandle | 11 GetService
            // ⚠️ 审计修复（2026-07-30 第二轮，真机 DIAG 探针坐实）：此前基线注释抄漏了相对槽 2 的
            //    GetStreamLatency，导致 GetCurrentPadding 起整体 -1 错位——GetMixFormat(误取槽4)
            //    实际调到 IsFormatSupported，x64 下垃圾 pFormat 被解引用 → 原生 AV 0xC0000005。
            //    探针证据：同一 this 上 GetBufferSize(槽1，映射正确) 正常返回 0x88890001，
            //    GetMixFormat 一调即崩，锁定为槽位错位而非线程/封送问题。
            _audioClientInitialize = ComVTable.Get<IAudioClient_Initialize>(pAudioClient, 0);
            _audioClientGetBufferSize = ComVTable.Get<IAudioClient_GetBufferSize>(pAudioClient, 1);
            // 跳过未使用的 GetStreamLatency（相对 slot 2）
            _audioClientGetCurrentPadding = ComVTable.Get<IAudioClient_GetCurrentPadding>(pAudioClient, 3);
            _audioClientIsFormatSupported = ComVTable.Get<IAudioClient_IsFormatSupported>(pAudioClient, 4);
            _audioClientGetMixFormat = ComVTable.Get<IAudioClient_GetMixFormat>(pAudioClient, 5);
            // 跳过未使用的 GetDevicePeriod（相对 slot 6）
            _audioClientStart = ComVTable.Get<IAudioClient_Start>(pAudioClient, 7);
            _audioClientStop = ComVTable.Get<IAudioClient_Stop>(pAudioClient, 8);
            _audioClientReset = ComVTable.Get<IAudioClient_Reset>(pAudioClient, 9);
            _audioClientSetEventHandle = ComVTable.Get<IAudioClient_SetEventHandle>(pAudioClient, 10);
            _audioClientGetService = ComVTable.Get<IAudioClient_GetService>(pAudioClient, 11);

            // 2. V2 格式协商（O7 独占模式 + O9 多格式直出）
            WAVEFORMATEX format;
            if (_exclusiveMode)
            {
                format = NegotiateExclusiveFormat(sampleRate, channels);
            }
            else
            {
                format = NegotiateSharedFormat(sampleRate, channels);
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
                    "请尝试共享模式（自动使用设备原生格式）或调整 WasapiOptions.PreferredSampleFormat。");
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
                _audioClockGetPosition = ComVTable.Get<IAudioClock_GetPosition>(pClock, 1);
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
            // SetEventHandle 失败时会将 _eventDrivenMode 改为 false（回退轮询），
            // 若不重置，重试 Initialize 时会使用轮询模式而非用户配置的事件驱动模式。
            _eventDrivenMode = _options.EventDrivenMode;
            throw;
        }
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// V2 变更：Submit 不再接管帧所有权，不 Dispose 帧。
    /// 调用方（AudioPipeline）负责 Return 到 FramePool 或 Dispose。
    /// V2 O9 多格式直出：帧格式与设备格式匹配时零转换直拷。
    /// </remarks>
    public void Submit(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        RunOnSta(() =>
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

        // 等待缓冲区有足够空间（COM 背压）
        WaitForBufferSpace((uint)frame.FrameCount);

        // 获取 WASAPI 缓冲区指针
        int hr = _renderClientGetBuffer!(_renderClientPtr, (uint)frame.FrameCount, out IntPtr pData);
        Marshal.ThrowExceptionForHR(hr);

        // GetBuffer 成功后必须调用 ReleaseBuffer，否则缓冲区永久锁定
        // 即使拷贝失败也要释放（用0帧+SILENT标记）
        bool releaseBufferCalled = false;
        try
        {
            // 拷贝/转换 PCM 数据到 WASAPI 缓冲区
            // 使用 Slice 确保对齐——frame.Data.Length 可能大于 expectedDataSize 且非 sizeof(T) 的倍数，
            // MemoryMarshal.Cast 对非对齐长度会抛 ArgumentException
            var validSrc = frame.Data.Span[..expectedDataSize];

            unsafe
            {
                CopyOrConvert(validSrc, pData, sampleCount, frame.SampleFormat, _deviceSampleFormat);
            }

            // 释放 WASAPI 缓冲区（写入完成）
            hr = _renderClientReleaseBuffer!(_renderClientPtr, (uint)frame.FrameCount, 0);
            releaseBufferCalled = true;
            Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            // 如果拷贝或ReleaseBuffer抛异常，必须用0帧+SILENT释放缓冲区
            if (!releaseBufferCalled)
            {
                try { _renderClientReleaseBuffer!(_renderClientPtr, 0, WasapiInterop.AUDCLNT_BUFFERFLAGS_SILENT); }
                catch { /* 尽力释放，忽略二次异常 */ }
            }
        }
        });
    }

    /// <inheritdoc/>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClientPtr == IntPtr.Zero) return;

        RunOnSta(() =>
        {
            int hr = _audioClientStop!(_audioClientPtr);
            // 审计修复：0x88890004 实际是 AUDCLNT_E_DEVICE_INVALIDATED（设备移除），非 AUDCLNT_E_NOT_INITIALIZED（0x88890001）。
            // 两者在 Stop() 上下文中均可安全忽略：设备已移除时 Stop 无意义，未初始化时 Stop 也无意义。
            if (hr < 0
                && hr != WasapiInterop.AUDCLNT_E_DEVICE_INVALIDATED
                && hr != WasapiInterop.AUDCLNT_E_NOT_INITIALIZED)
            {
                _logger.LogWarning("IAudioClient.Stop 失败：HRESULT=0x{HR:X8}", hr);
            }
        });
    }

    /// <inheritdoc/>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClientPtr == IntPtr.Zero) return;

        RunOnSta(() =>
        {
            int hr = _audioClientStart!(_audioClientPtr);
            if (hr < 0)
                _logger.LogWarning("IAudioClient.Start 失败：HRESULT=0x{HR:X8}", hr);
        });
    }

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClientPtr == IntPtr.Zero) return;

        RunOnSta(() =>
        {
            int hr = _audioClientReset!(_audioClientPtr);
            if (hr < 0)
                _logger.LogWarning("IAudioClient.Reset 失败：HRESULT=0x{HR:X8}", hr);
        });
    }

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPosition()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_audioClockPtr == IntPtr.Zero)
            return TimeSpan.Zero;

        return RunOnSta(() =>
        {
            int hr = _audioClockGetPosition!(_audioClockPtr, out ulong devicePosition, out _);
            if (hr < 0)
                return TimeSpan.Zero;

            // devicePosition 是已播放的帧数，转换为 TimeSpan
            if (_sampleRate <= 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds((double)devicePosition / _sampleRate);
        });
    }

    /// <inheritdoc/>
    public TimeSpan Latency
    {
        get
        {
            if (!_initialized || _sampleRate <= 0 || _bufferSize <= 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds((double)_bufferSize / _sampleRate);
        }
    }

    /// <inheritdoc/>
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
                RunOnSta(() =>
                {
                    var ec = Guid.Empty;
                    int hr = _simpleVolumeSetMasterVolume!(_simpleVolumePtr, clamped, ref ec);
                    if (hr < 0)
                        _logger.LogWarning("SetMasterVolume 失败：HRESULT=0x{HR:X8}", hr);
                });
            }
        }
    }

    /// <summary>缓冲区大小（帧数，运行时从 WASAPI 获取）。</summary>
    public int BufferSize => _bufferSize;

    /// <summary>是否独占模式（从 WasapiOptions 配置，初始化后只读）。</summary>
    public bool ExclusiveMode => _exclusiveMode;

    /// <summary>是否事件驱动模式（V2，初始化后只读）。</summary>
    public bool EventDrivenMode => _eventDrivenMode;

    /// <summary>设备原生采样格式（V2，Initialize 后可用）。</summary>
    public SampleFormat DeviceSampleFormat => _deviceSampleFormat;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 封送 COM 对象释放到 STA 线程（与创建/使用同一公寓），并停止 STA 队列触发 CoUninitialize。
        if (_staThread is not null)
        {
            try { RunOnSta(ReleaseComObjects); }
            catch { /* 释放时忽略错误 */ }

            try { _staQueue!.CompleteAdding(); } catch { }
            try { _staThread.Join(); } catch { }
            try { _staQueue!.Dispose(); } catch { }
            _staQueue = null;
            _staThread = null;
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

        // STA 公寓（CoInitializeEx(COINIT_APARTMENTTHREADED)）由内部 STA 工作线程在 Dispose 时
        // 经 CompleteAdding → StaThreadProc finally → CoUninitialize 正确反初始化，不再有跨实例/测试
        // 污染 COM 单元的问题（旧版 MTA + 无条件 CoUninitialize 曾引发全量测试原生崩溃 0xC0000005）。

        _initialized = false;
        _logger.LogDebug("WASAPI 输出已释放");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：COM 释放为快速同步调用，无 I/O 可 await。
    /// 委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ── 内部方法 ──

    // STA 线程封送基础设施（WASAPI 要求 IAudioClient 在 STA 公寓使用，MTA 下 GetMixFormat/Initialize 会触发 native AV）
    private sealed class StaWorkItem
    {
        private readonly Action? _action;
        private readonly Func<object?>? _func;
        public object? Result;
        public Exception? Exception;
        public readonly ManualResetEventSlim Done = new(false);

        public StaWorkItem(Action action) => _action = action;
        public StaWorkItem(Func<object?> func) => _func = func;

        public void Run()
        {
            if (_action is not null) _action();
            else Result = _func!();
        }
    }

    private void EnsureStaThread()
    {
        if (_staThread is not null) return;
        _staQueue = new BlockingCollection<StaWorkItem>();
        _staThread = new Thread(StaThreadProc) { IsBackground = true, Name = "WasapiSta" };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
        _staStarted.Wait();
    }

    private void RunOnSta(Action action)
    {
        var queue = _staQueue;
        if (queue is null || queue.IsAddingCompleted)
            throw new ObjectDisposedException(nameof(WasapiOutput));
        var item = new StaWorkItem(action);
        queue.Add(item);
        item.Done.Wait();
        if (item.Exception is not null)
            ExceptionDispatchInfo.Throw(item.Exception);
    }

    private T RunOnSta<T>(Func<T> func)
    {
        var queue = _staQueue;
        if (queue is null || queue.IsAddingCompleted)
            throw new ObjectDisposedException(nameof(WasapiOutput));
        var item = new StaWorkItem(() => func()!);
        queue.Add(item);
        item.Done.Wait();
        if (item.Exception is not null)
            ExceptionDispatchInfo.Throw(item.Exception);
        return (T)(item.Result ?? default(T))!;
    }

    private void StaThreadProc()
    {
        WasapiInterop.CoInitializeEx(IntPtr.Zero, WasapiInterop.COINIT_APARTMENTTHREADED);
        _staStarted.Set();
        try
        {
            foreach (var item in _staQueue!.GetConsumingEnumerable())
            {
                try { item.Run(); }
                catch (Exception ex) { item.Exception = ex; }
                finally { item.Done.Set(); }
            }
        }
        finally
        {
            WasapiInterop.CoUninitialize();
        }
    }

    /// <summary>
    /// 初始化 COM 单元并获取默认音频渲染设备。
    /// </summary>
    private void InitializeCore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_devicePtr != IntPtr.Zero)
            throw new InvalidOperationException("InitializeAsync 已调用，请勿重复调用。");

        // 注意：COM 单元（STA）的 CoInitializeEx 已在内部 STA 工作线程（StaThreadProc）内完成。
        // 本方法通过 RunOnSta 在 STA 线程上执行，故此处不再初始化/反初始化 COM 单元。

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

            // 3. 获取默认音频渲染设备
            hr = _enumeratorGetDefault(
                _enumeratorPtr,
                WasapiInterop.EDataFlow_Render,
                WasapiInterop.ERole_Console,
                out IntPtr pDevice);
            Marshal.ThrowExceptionForHR(hr);

            _devicePtr = pDevice;   // 持有 GetDefaultAudioEndpoint 返回的引用
            _deviceActivate = ComVTable.Get<IMMDevice_Activate>(pDevice, 0);
        }
        catch
        {
            // 初始化失败，清理已创建的 COM 对象（CoUninitialize 由 STA 线程 proc 的 finally 负责）
            ReleaseComObjects();
            throw;
        }

        _logger.LogDebug("WASAPI 设备枚举器已创建，默认渲染设备已获取。");
    }

    // ── V2 格式协商方法（O7 独占模式 + O9 多格式直出）──

    /// <summary>
    /// 共享模式格式协商：通过 GetMixFormat 获取设备原生格式。
    /// </summary>
    /// <param name="sampleRate">请求的采样率。</param>
    /// <param name="channels">请求的声道数。</param>
    /// <returns>用于 IAudioClient::Initialize 的 WAVEFORMATEX。</returns>
    private WAVEFORMATEX NegotiateSharedFormat(int sampleRate, int channels)
    {
        // 1. 获取设备原生混音格式
        int hr = _audioClientGetMixFormat!(_audioClientPtr, out IntPtr pMixFormat);
        if (hr < 0 || pMixFormat == IntPtr.Zero)
        {
            // 审计修复：GetMixFormat 失败时仍可能已分配内存（极端情况），需安全释放
            if (pMixFormat != IntPtr.Zero)
                WasapiInterop.CoTaskMemFree(pMixFormat);
            _logger.LogWarning("GetMixFormat 失败 (HRESULT=0x{HR:X8})，回退到 F32 格式", hr);
            _deviceSampleFormat = SampleFormat.F32;
            return BuildWaveFormat(sampleRate, channels, SampleFormat.F32);
        }

        try
        {
            // 2. 解析设备原生 mix format（采样率/声道数/格式标签均取自 GetMixFormat）
            var mix = Marshal.PtrToStructure<WAVEFORMATEX>(pMixFormat);
            _mixSampleRate = (int)mix.nSamplesPerSec;
            _mixChannels = mix.nChannels;
            _deviceSampleFormat = ParseSampleFormat(pMixFormat);

            // 3. 如果指定了 PreferredSampleFormat 且与设备格式不同，尝试 IsFormatSupported
            if (_options.PreferredSampleFormat.HasValue &&
                _options.PreferredSampleFormat.Value != _deviceSampleFormat)
            {
                var preferred = _options.PreferredSampleFormat.Value;
                var preferredFormat = BuildWaveFormat(_mixSampleRate, _mixChannels, preferred);

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

            // 4. 共享模式：系统负责重采样到设备原生 mix format，故初始化格式直接采用
            //    GetMixFormat 的采样率/声道数/格式标签，而非解码器输出格式（如 44100），
            //    避免与设备 mix format 不匹配导致 AUDCLNT_E_UNSUPPORTED_FORMAT（部分驱动对采样率严格）。
            return BuildWaveFormat(_mixSampleRate, _mixChannels, _deviceSampleFormat);
        }
        finally
        {
            WasapiInterop.CoTaskMemFree(pMixFormat);
        }
    }

    /// <summary>
    /// 独占模式格式协商：通过 IsFormatSupported 逐一尝试格式。
    /// </summary>
    /// <param name="sampleRate">请求的采样率。</param>
    /// <param name="channels">请求的声道数。</param>
    /// <returns>用于 IAudioClient::Initialize 的 WAVEFORMATEX。</returns>
    /// <remarks>
    /// 独占模式下 IsFormatSupported 的 ppClosestMatch 必须为 NULL（不支持最接近格式）。
    /// 返回 S_OK 表示支持，AUDCLNT_E_UNSUPPORTED_FORMAT 表示不支持。
    /// </remarks>
    private WAVEFORMATEX NegotiateExclusiveFormat(int sampleRate, int channels)
    {
        // 构建尝试顺序：PreferredSampleFormat（若有）→ F32 → S32 → S16
        // 审计修复：使用 HashSet 去重，替代 IndexOf!=LastIndexOf（后者跳过所有重复实例而非仅后续重复）
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
                continue; // 跳过已尝试的格式（PreferredSampleFormat 可能与 F32/S32/S16 重复）

            var wfx = BuildWaveFormat(sampleRate, channels, format);

            unsafe
            {
                // 独占模式：ppClosestMatch 必须为 NULL（传 IntPtr.Zero 按值）
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

        // 所有格式都不支持
        _deviceSampleFormat = SampleFormat.F32;
        throw new NotSupportedException(
            $"独占模式下设备不支持任何可用格式（F32/S32/S16 {sampleRate}Hz {channels}ch）。" +
            "请尝试共享模式或调整采样率/声道数。");
    }

    /// <summary>
    /// 构建指定格式的 WAVEFORMATEX 结构体。
    /// </summary>
    /// <param name="sampleRate">采样率。</param>
    /// <param name="channels">声道数。</param>
    /// <param name="format">采样格式。</param>
    /// <returns>WAVEFORMATEX 结构体。</returns>
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
    /// 从 WAVEFORMATEX 指针解析采样格式。
    /// 支持 WAVE_FORMAT_PCM、WAVE_FORMAT_IEEE_FLOAT 和 WAVE_FORMAT_EXTENSIBLE。
    /// </summary>
    /// <param name="pFormat">WAVEFORMATEX 指针（由 CoTaskMemAlloc 分配）。</param>
    /// <returns>解析出的采样格式，无法识别时返回 F32。</returns>
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
            // 读取 WAVEFORMATEXTENSIBLE 的 SubFormat GUID
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

        // 未知格式，默认 F32
        return SampleFormat.F32;
    }

    // ── V2 PCM 拷贝/转换方法（O9 多格式直出）──

    /// <summary>
    /// 将源 PCM 数据拷贝或转换到 WASAPI 缓冲区。
    /// 当源格式与目标格式相同时，零转换直接拷贝。
    /// </summary>
    /// <param name="src">源数据 Span（已对齐到 expectedDataSize）。</param>
    /// <param name="dstPtr">WASAPI 缓冲区指针。</param>
    /// <param name="sampleCount">样本总数（frameCount * channels）。</param>
    /// <param name="srcFormat">源采样格式。</param>
    /// <param name="dstFormat">目标（设备）采样格式。</param>
    internal static unsafe void CopyOrConvert(
        ReadOnlySpan<byte> src, IntPtr dstPtr, int sampleCount,
        SampleFormat srcFormat, SampleFormat dstFormat)
    {
        // 快速路径：格式匹配，零转换直接拷贝
        if (srcFormat == dstFormat)
        {
            var dst = new Span<byte>((void*)dstPtr, sampleCount * GetBytesPerSample(dstFormat));
            src.CopyTo(dst);
            return;
        }

        // 转换路径：按目标格式分类
        if (dstFormat == SampleFormat.F32)
        {
            var dst = new Span<float>((void*)dstPtr, sampleCount);
            if (srcFormat == SampleFormat.S16)
            {
                // S16 → F32
                var srcTyped = MemoryMarshal.Cast<byte, short>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = srcTyped[i] / 32768.0f;
            }
            else // S32 → F32
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
                // F32 → S16（审计修复：缩放因子从 32767 改为 32768，与 S16→F32 的 1/32768 对称，确保往返无损）
                var srcTyped = MemoryMarshal.Cast<byte, float>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = (short)Math.Clamp(srcTyped[i] * 32768f, -32768f, 32767f);
            }
            else // S32 → S16
            {
                var srcTyped = MemoryMarshal.Cast<byte, int>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = (short)(srcTyped[i] >> 16);
            }
        }
        else // dstFormat == S32
        {
            var dst = new Span<int>((void*)dstPtr, sampleCount);
            if (srcFormat == SampleFormat.F32)
            {
                // F32 → S32（审计修复：使用 double 字面量避免 float 精度问题——2147483647f 实际为 2^31 导致溢出）
                var srcTyped = MemoryMarshal.Cast<byte, float>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = (int)Math.Clamp(srcTyped[i] * 2147483648.0, -2147483648.0, 2147483647.0);
            }
            else // S16 → S32
            {
                var srcTyped = MemoryMarshal.Cast<byte, short>(src);
                for (int i = 0; i < sampleCount; i++)
                    dst[i] = srcTyped[i] << 16;
            }
        }
    }

    /// <summary>
    /// 获取采样格式的每样本字节数。
    /// </summary>
    internal static int GetBytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.S16 => 2,
        SampleFormat.S32 => 4,
        SampleFormat.F32 => 4,
        _ => 4
    };

    /// <summary>
    /// 等待 WASAPI 缓冲区有足够空间（COM 背压）。
    /// V2 O8：事件驱动模式下使用 EventWaitHandle.WaitOne 替代 Thread.Sleep 轮询。
    /// </summary>
    /// <param name="requiredFrames">需要的帧数。</param>
    private void WaitForBufferSpace(uint requiredFrames)
    {
        if (_audioClientPtr == IntPtr.Zero) return;

        // 快速失败：请求帧数超过缓冲区总大小，永远无法满足
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
                // V2 O8 事件驱动：等待 WASAPI 内核事件通知缓冲区可写
                // 比 Thread.Sleep(1) 轮询更高效——事件触发即唤醒，无空转轮询
                int remainingMs = (int)(timeoutMs - sw.ElapsedMilliseconds);
                if (remainingMs <= 0)
                    break;
                _bufferEvent.WaitOne(remainingMs);
            }
            else
            {
                // V1 轮询模式
                Thread.Sleep(1);
            }
        }

        // 超时后最终检查
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
    /// 释放所有 COM 对象（逆序释放：子对象先于父对象）。
    /// 包括 _device 和 _enumerator（由 InitializeAsync 创建），用于 Dispose 和 InitializeCore 失败清理。
    /// </summary>
    private void ReleaseComObjects()
    {
        // 停止音频客户端（防止后续 COM 调用阻塞）
        if (_audioClientPtr != IntPtr.Zero && _audioClientStop is not null)
        {
            try { _audioClientStop(_audioClientPtr); }
            catch { /* 忽略释放时的错误 */ }
        }

        // 逆序释放（原生指针 Marshal.Release，清空委托缓存）
        ReleaseComPtr(ref _audioClockPtr);
        _audioClockGetPosition = null;

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
    /// 仅释放 Initialize 方法创建的 COM 对象（不含 _device/_enumerator）。
    /// 用于 Initialize 失败时的清理，保留 _device/_enumerator 以便用户重试。
    /// </summary>
    private void ReleaseInitializeObjects()
    {
        if (_audioClientPtr != IntPtr.Zero && _audioClientStop is not null)
        {
            try { _audioClientStop(_audioClientPtr); }
            catch { /* 忽略释放时的错误 */ }
        }

        ReleaseComPtr(ref _audioClockPtr);
        _audioClockGetPosition = null;

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

    /// <summary>
    /// 安全释放单个原生 COM 指针（Marshal.Release 减引用计数并置零）。
    /// </summary>
    private static void ReleaseComPtr(ref IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        try
        {
            Marshal.Release(ptr);
        }
        catch { /* 忽略释放时的错误 */ }
        ptr = IntPtr.Zero;
    }
}

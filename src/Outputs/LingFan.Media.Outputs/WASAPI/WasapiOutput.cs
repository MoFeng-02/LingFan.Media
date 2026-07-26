using System.Diagnostics;
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
/// <item><see cref="Initialize"/>：同步（sync 分类），IAudioClient + IAudioRenderClient 创建。</item>
/// <item><see cref="Submit"/>：同步边界（native 分类），COM GetBuffer + 拷贝 + ReleaseBuffer，缓冲满时阻塞（COM 背压）。</item>
/// <item><see cref="Pause"/>/<see cref="Resume"/>/<see cref="Flush"/>：同步（sync 分类），IAudioClient.Stop/Start/Reset。</item>
/// <item><see cref="GetPlaybackPosition"/>：同步（sync 分类），IAudioClock.GetPosition。</item>
/// <item><see cref="Dispose"/>：同步快速释放（sync 分类），释放 COM 对象 + CoUninitialize。</item>
/// <item><see cref="DisposeAsync"/>：接口契约，委托 <see cref="Dispose"/> + <see cref="ValueTask.CompletedTask"/>。
/// V1 无回调线程，COM 释放为快速同步调用，无 I/O 可 await，非伪异步。</item>
/// </list>
/// <para><b>线程安全</b>：非线程安全。Submit 在音频管线线程调用，Pause/Resume/Flush 在控制线程调用，
/// 不可并发。COM 使用 MTA（COINIT_MULTITHREADED），允许跨线程调用。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，采用原始 vtable P/Invoke（ComVTable 委托封送），不使用 [ComImport]/RCW，NativeAOT 兼容。</para>
/// <para><b>资源所有权</b>：IMMDeviceEnumerator/IMMDevice/IAudioClient/IAudioRenderClient/ISimpleAudioVolume/IAudioClock
/// 的原生指针均由本类持有（Session 级），Dispose 时通过 Marshal.Release(IntPtr) 逆序释放。</para>
/// <para><b>Submit 所有权</b>：V2 变更——Submit 不再接管帧所有权，不 Dispose 帧。
/// 调用方（AudioPipeline）负责 Return 到 FramePool 或 Dispose。</para>
/// <para><b>V1 限制</b>：仅支持共享模式 + 32 位浮点输出。S16/S32 输入会转换为 F32。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WasapiOutput : IAudioOutput
{
    private readonly WasapiOptions _options;
    private readonly ILogger<WasapiOutput> _logger;
    private readonly bool _exclusiveMode;

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
    private IAudioClient_Start? _audioClientStart;
    private IAudioClient_Stop? _audioClientStop;
    private IAudioClient_Reset? _audioClientReset;
    private IAudioClient_GetService? _audioClientGetService;
    private IAudioRenderClient_GetBuffer? _renderClientGetBuffer;
    private IAudioRenderClient_ReleaseBuffer? _renderClientReleaseBuffer;
    private ISimpleAudioVolume_SetMasterVolume? _simpleVolumeSetMasterVolume;
    private IAudioClock_GetPosition? _audioClockGetPosition;

    // 状态
    private bool _comInitialized;
    private bool _initialized;
    private bool _disposed;
    private int _bufferSize;      // WASAPI 缓冲区大小（帧数）
    private int _sampleRate;
    private int _channels;
    private float _volume = 1.0f;

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
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：CoInitializeEx + COM 设备枚举均为同步 COM 调用，无 I/O 可 await。
    /// 同步执行后返回 <see cref="Task.CompletedTask"/>，非伪异步。
    /// </remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        InitializeCore();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels)
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
            _audioClientInitialize = ComVTable.Get<IAudioClient_Initialize>(pAudioClient, 0);
            _audioClientGetBufferSize = ComVTable.Get<IAudioClient_GetBufferSize>(pAudioClient, 1);
            _audioClientGetCurrentPadding = ComVTable.Get<IAudioClient_GetCurrentPadding>(pAudioClient, 3);
            _audioClientStart = ComVTable.Get<IAudioClient_Start>(pAudioClient, 7);
            _audioClientStop = ComVTable.Get<IAudioClient_Stop>(pAudioClient, 8);
            _audioClientReset = ComVTable.Get<IAudioClient_Reset>(pAudioClient, 9);
            _audioClientGetService = ComVTable.Get<IAudioClient_GetService>(pAudioClient, 11);

            // 2. 构建 WAVEFORMATEX（32 位浮点）
            var format = new WAVEFORMATEX
            {
                wFormatTag = WasapiInterop.WAVE_FORMAT_IEEE_FLOAT,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = 32,
                nBlockAlign = (ushort)(channels * 4),
                nAvgBytesPerSec = (uint)(sampleRate * channels * 4),
                cbSize = 0
            };

            // 3. 初始化 IAudioClient
            int shareMode = _exclusiveMode
                ? WasapiInterop.AUDCLNT_SHAREMODE_EXCLUSIVE
                : WasapiInterop.AUDCLNT_SHAREMODE_SHARED;

            long bufferDurationHns = (long)(_options.BufferDuration.TotalSeconds * WasapiInterop.ReftimesPerSec);

            var sessionGuid = Guid.Empty;
            unsafe
            {
                hr = _audioClientInitialize(
                    _audioClientPtr,
                    shareMode,
                    0,                     // 无流标志（V1 不用事件驱动）
                    bufferDurationHns,
                    _exclusiveMode ? bufferDurationHns : 0, // 独占模式需指定 periodicity，共享模式 = 0
                    (IntPtr)(&format),
                    ref sessionGuid);
            }

            if (hr < 0)
            {
                _logger.LogError("IAudioClient.Initialize 失败：HRESULT=0x{HR:X8}", hr);
                Marshal.ThrowExceptionForHR(hr);
            }

            // 4. 获取缓冲区大小
            hr = _audioClientGetBufferSize(_audioClientPtr, out uint bufferFrames);
            Marshal.ThrowExceptionForHR(hr);
            _bufferSize = (int)bufferFrames;

            // 5. 获取 IAudioRenderClient
            var iidRender = WasapiInterop.IID_IAudioRenderClient;
            hr = _audioClientGetService(_audioClientPtr, ref iidRender, out IntPtr pRenderClient);
            Marshal.ThrowExceptionForHR(hr);

            _renderClientPtr = pRenderClient;
            _renderClientGetBuffer = ComVTable.Get<IAudioRenderClient_GetBuffer>(pRenderClient, 0);
            _renderClientReleaseBuffer = ComVTable.Get<IAudioRenderClient_ReleaseBuffer>(pRenderClient, 1);

            // 6. 获取 ISimpleAudioVolume（音量控制）
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

            // 7. 获取 IAudioClock（播放位置查询）
            var iidClock = WasapiInterop.IID_IAudioClock;
            hr = _audioClientGetService(_audioClientPtr, ref iidClock, out IntPtr pClock);
            if (hr >= 0)
            {
                _audioClockPtr = pClock;
                _audioClockGetPosition = ComVTable.Get<IAudioClock_GetPosition>(pClock, 2);
            }
            else
            {
                _logger.LogWarning("无法获取 IAudioClock（HRESULT=0x{HR:X8}），播放位置查询不可用。", hr);
            }

            // 8. 应用初始音量
            if (_simpleVolumePtr != IntPtr.Zero)
            {
                var ec = Guid.Empty;
                hr = _simpleVolumeSetMasterVolume!(_simpleVolumePtr, _volume, ref ec);
                if (hr < 0)
                    _logger.LogWarning("设置初始音量失败：HRESULT=0x{HR:X8}", hr);
            }

            _initialized = true;
            _logger.LogDebug("WASAPI 输出已初始化：{SampleRate}Hz, {Channels}ch, 缓冲={BufferSize}帧 ({BufferMs:F1}ms)",
                sampleRate, channels, _bufferSize, (double)_bufferSize / sampleRate * 1000);
        }
        catch
        {
            // Initialize 失败时仅清理 Initialize 创建的 COM 对象（_audioClient/_renderClient/_simpleVolume/_audioClock），
            // 不释放 _device/_enumerator（它们由 InitializeAsync 创建，保留以便用户重试 Initialize）。
            ReleaseInitializeObjects();
            _bufferSize = 0;
            _sampleRate = 0;
            _channels = 0;
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// V2 变更：Submit 不再接管帧所有权，不 Dispose 帧。
    /// 调用方（AudioPipeline）负责 Return 到 FramePool 或 Dispose。
    /// </remarks>
    public void Submit(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_initialized || _audioClientPtr == IntPtr.Zero || _renderClientPtr == IntPtr.Zero)
                throw new InvalidOperationException("WASAPI 输出尚未初始化，无法 Submit。");

            // 验证声道数匹配（管线应保证一致，不匹配是管线bug）
            if (frame.Channels != _channels)
            {
                throw new ArgumentException(
                    $"音频帧声道数 {frame.Channels} 与输出配置 {_channels} 不匹配。", nameof(frame));
            }

            // 计算每样本字节数
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
                    var dstPtr = (float*)pData;

                    if (frame.SampleFormat == SampleFormat.F32)
                    {
                        // F32 → F32 直接拷贝
                        var src = MemoryMarshal.Cast<byte, float>(validSrc);
                        var dst = new Span<float>(dstPtr, sampleCount);
                        src.CopyTo(dst);
                    }
                    else if (frame.SampleFormat == SampleFormat.S16)
                    {
                        // S16 → F32 转换
                        var src = MemoryMarshal.Cast<byte, short>(validSrc);
                        var dst = new Span<float>(dstPtr, sampleCount);
                        for (int i = 0; i < sampleCount; i++)
                            dst[i] = src[i] / 32768.0f;
                    }
                    else if (frame.SampleFormat == SampleFormat.S32)
                    {
                        // S32 → F32 转换
                        var src = MemoryMarshal.Cast<byte, int>(validSrc);
                        var dst = new Span<float>(dstPtr, sampleCount);
                        for (int i = 0; i < sampleCount; i++)
                            dst[i] = src[i] / 2147483648.0f;
                    }
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
    }

    /// <inheritdoc/>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClientPtr == IntPtr.Zero) return;

        int hr = _audioClientStop!(_audioClientPtr);
        if (hr < 0 && hr != unchecked((int)0x88890004)) // AUDCLNT_E_NOT_INITIALIZED 可忽略
            _logger.LogWarning("IAudioClient.Stop 失败：HRESULT=0x{HR:X8}", hr);
    }

    /// <inheritdoc/>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClientPtr == IntPtr.Zero) return;

        int hr = _audioClientStart!(_audioClientPtr);
        if (hr < 0)
            _logger.LogWarning("IAudioClient.Start 失败：HRESULT=0x{HR:X8}", hr);
    }

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClientPtr == IntPtr.Zero) return;

        int hr = _audioClientReset!(_audioClientPtr);
        if (hr < 0)
            _logger.LogWarning("IAudioClient.Reset 失败：HRESULT=0x{HR:X8}", hr);
    }

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPosition()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_audioClockPtr == IntPtr.Zero)
            return TimeSpan.Zero;

        int hr = _audioClockGetPosition!(_audioClockPtr, out ulong devicePosition, out _);
        if (hr < 0)
            return TimeSpan.Zero;

        // devicePosition 是已播放的帧数，转换为 TimeSpan
        if (_sampleRate <= 0)
            return TimeSpan.Zero;

        return TimeSpan.FromSeconds((double)devicePosition / _sampleRate);
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
                var ec = Guid.Empty;
                int hr = _simpleVolumeSetMasterVolume!(_simpleVolumePtr, clamped, ref ec);
                if (hr < 0)
                    _logger.LogWarning("SetMasterVolume 失败：HRESULT=0x{HR:X8}", hr);
            }
        }
    }

    /// <summary>缓冲区大小（帧数，运行时从 WASAPI 获取）。</summary>
    public int BufferSize => _bufferSize;

    /// <summary>是否独占模式（从 WasapiOptions 配置，初始化后只读）。</summary>
    public bool ExclusiveMode => _exclusiveMode;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseComObjects();

        if (_comInitialized)
        {
            WasapiInterop.CoUninitialize();
            _comInitialized = false;
        }

        _initialized = false;
        _logger.LogDebug("WASAPI 输出已释放");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：V1 无回调线程，COM 释放为快速同步调用，无 I/O 可 await。
    /// 委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ── 内部方法 ──

    /// <summary>
    /// 初始化 COM 单元并获取默认音频渲染设备。
    /// </summary>
    private void InitializeCore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_devicePtr != IntPtr.Zero)
            throw new InvalidOperationException("InitializeAsync 已调用，请勿重复调用。");

        // 1. CoInitializeEx（MTA）
        int hr = WasapiInterop.CoInitializeEx(IntPtr.Zero, WasapiInterop.COINIT_MULTITHREADED);
        if (hr == WasapiInterop.RPC_E_CHANGED_MODE)
        {
            // 线程已初始化为不同模式，不调用 CoUninitialize
            _comInitialized = false;
            _logger.LogDebug("COM 已初始化为不同模式（RPC_E_CHANGED_MODE），跳过 CoUninitialize。");
        }
        else if (hr >= 0) // S_OK(0) 或 S_FALSE(1)
        {
            // 成功，需要 CoUninitialize 平衡
            _comInitialized = true;
        }
        else
        {
            // 其他失败 HRESULT（如 E_OUTOFMEMORY）——COM 未初始化，不应继续
            _comInitialized = false;
            _logger.LogError("CoInitializeEx 失败：HRESULT=0x{HR:X8}", hr);
            throw new COMException("CoInitializeEx 失败，无法初始化 COM 单元。", hr);
        }

        try
        {
            // 2. 创建 IMMDeviceEnumerator
            var clsid = WasapiInterop.CLSID_MMDeviceEnumerator;
            var iid = WasapiInterop.IID_IMMDeviceEnumerator;
            hr = WasapiInterop.CoCreateInstance(
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
            // 初始化失败，清理已创建的 COM 对象
            ReleaseComObjects();
            if (_comInitialized)
            {
                WasapiInterop.CoUninitialize();
                _comInitialized = false;
            }
            throw;
        }

        _logger.LogDebug("WASAPI 设备枚举器已创建，默认渲染设备已获取。");
    }

    /// <summary>
    /// 等待 WASAPI 缓冲区有足够空间（COM 背压）。
    /// </summary>
    /// <param name="requiredFrames">需要的帧数。</param>
    private void WaitForBufferSpace(uint requiredFrames)
    {
        if (_audioClientPtr == IntPtr.Zero) return;

        // 快速失败：请求帧数超过缓冲区总大小，永远无法满足，避免空转2秒超时
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

            Thread.Sleep(1);
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
        _audioClientStart = null;
        _audioClientStop = null;
        _audioClientReset = null;
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
        _audioClientStart = null;
        _audioClientStop = null;
        _audioClientReset = null;
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

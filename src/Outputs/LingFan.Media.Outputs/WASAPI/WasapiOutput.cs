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
/// <para><b>AOT 兼容</b>：sealed 类，无反射，COM 接口使用 [ComImport] 编译期生成存根。</para>
/// <para><b>资源所有权</b>：IMMDeviceEnumerator/IMMDevice/IAudioClient/IAudioRenderClient/ISimpleAudioVolume/IAudioClock
/// 均由本类持有（Session 级），Dispose 时通过 Marshal.ReleaseComObject 释放。</para>
/// <para><b>Submit 所有权</b>：调用后 WasapiOutput 拥有 frame 所有权，内部拷贝到 WASAPI 缓冲后立即 Dispose。</para>
/// <para><b>V1 限制</b>：仅支持共享模式 + 32 位浮点输出。S16/S32 输入会转换为 F32。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WasapiOutput : IAudioOutput
{
    private readonly WasapiOptions _options;
    private readonly ILogger<WasapiOutput> _logger;
    private readonly bool _exclusiveMode;

    // COM 对象（Session 级，Dispose 时释放）
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioClient? _audioClient;
    private IAudioRenderClient? _renderClient;
    private ISimpleAudioVolume? _simpleVolume;
    private IAudioClock? _audioClock;

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

        if (_device is null)
            throw new InvalidOperationException("InitializeAsync 尚未调用，无法 Initialize。");

        _sampleRate = sampleRate;
        _channels = channels;

        try
        {
            // 1. 激活 IAudioClient
            var iid = WasapiInterop.IID_IAudioClient;
            int hr = _device.Activate(ref iid, WasapiInterop.CLSCTX_ALL, IntPtr.Zero, out IntPtr pAudioClient);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                _audioClient = (IAudioClient)Marshal.GetObjectForIUnknown(pAudioClient);
            }
            finally
            {
                Marshal.Release(pAudioClient);
            }

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

            unsafe
            {
                hr = _audioClient.Initialize(
                    shareMode,
                    0,                     // 无流标志（V1 不用事件驱动）
                    bufferDurationHns,
                    _exclusiveMode ? bufferDurationHns : 0, // 独占模式需指定 periodicity，共享模式 = 0
                    (IntPtr)(&format),
                    Guid.Empty);
            }

            if (hr < 0)
            {
                _logger.LogError("IAudioClient.Initialize 失败：HRESULT=0x{HR:X8}", hr);
                Marshal.ThrowExceptionForHR(hr);
            }

            // 4. 获取缓冲区大小
            hr = _audioClient.GetBufferSize(out uint bufferFrames);
            Marshal.ThrowExceptionForHR(hr);
            _bufferSize = (int)bufferFrames;

            // 5. 获取 IAudioRenderClient
            var iidRender = WasapiInterop.IID_IAudioRenderClient;
            hr = _audioClient.GetService(ref iidRender, out IntPtr pRenderClient);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                _renderClient = (IAudioRenderClient)Marshal.GetObjectForIUnknown(pRenderClient);
            }
            finally
            {
                Marshal.Release(pRenderClient);
            }

            // 6. 获取 ISimpleAudioVolume（音量控制）
            var iidVolume = WasapiInterop.IID_ISimpleAudioVolume;
            hr = _audioClient.GetService(ref iidVolume, out IntPtr pVolume);
            if (hr >= 0)
            {
                try
                {
                    _simpleVolume = (ISimpleAudioVolume)Marshal.GetObjectForIUnknown(pVolume);
                }
                finally
                {
                    Marshal.Release(pVolume);
                }
            }
            else
            {
                _logger.LogWarning("无法获取 ISimpleAudioVolume（HRESULT=0x{HR:X8}），音量控制不可用。", hr);
            }

            // 7. 获取 IAudioClock（播放位置查询）
            var iidClock = WasapiInterop.IID_IAudioClock;
            hr = _audioClient.GetService(ref iidClock, out IntPtr pClock);
            if (hr >= 0)
            {
                try
                {
                    _audioClock = (IAudioClock)Marshal.GetObjectForIUnknown(pClock);
                }
                finally
                {
                    Marshal.Release(pClock);
                }
            }
            else
            {
                _logger.LogWarning("无法获取 IAudioClock（HRESULT=0x{HR:X8}），播放位置查询不可用。", hr);
            }

            // 8. 应用初始音量
            if (_simpleVolume is not null)
            {
                hr = _simpleVolume.SetMasterVolume(_volume, Guid.Empty);
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
    public void Submit(AudioFrame frame)
    {
        // ArgumentNullException 必须在最前——null frame 无法 Dispose
        ArgumentNullException.ThrowIfNull(frame);

        // 接口契约：Submit 一旦被调用（frame 非 null）即取走 frame 所有权，
        // 无论后续验证是否通过、是否抛异常，都必须 Dispose frame。
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_initialized || _audioClient is null || _renderClient is null)
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
            int hr = _renderClient.GetBuffer((uint)frame.FrameCount, out IntPtr pData);
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
                hr = _renderClient.ReleaseBuffer((uint)frame.FrameCount, 0);
                releaseBufferCalled = true;
                Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                // 如果拷贝或ReleaseBuffer抛异常，必须用0帧+SILENT释放缓冲区
                if (!releaseBufferCalled)
                {
                    try { _renderClient.ReleaseBuffer(0, WasapiInterop.AUDCLNT_BUFFERFLAGS_SILENT); }
                    catch { /* 尽力释放，忽略二次异常 */ }
                }
            }
        }
        finally
        {
            // 取走所有权，立即释放——覆盖所有异常路径（含早期验证失败）
            frame.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClient is null) return;

        int hr = _audioClient.Stop();
        if (hr < 0 && hr != unchecked((int)0x88890004)) // AUDCLNT_E_NOT_INITIALIZED 可忽略
            _logger.LogWarning("IAudioClient.Stop 失败：HRESULT=0x{HR:X8}", hr);
    }

    /// <inheritdoc/>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClient is null) return;

        int hr = _audioClient.Start();
        if (hr < 0)
            _logger.LogWarning("IAudioClient.Start 失败：HRESULT=0x{HR:X8}", hr);
    }

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioClient is null) return;

        int hr = _audioClient.Reset();
        if (hr < 0)
            _logger.LogWarning("IAudioClient.Reset 失败：HRESULT=0x{HR:X8}", hr);
    }

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPosition()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_audioClock is null)
            return TimeSpan.Zero;

        int hr = _audioClock.GetPosition(out ulong devicePosition, out _);
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

            if (_simpleVolume is not null)
            {
                int hr = _simpleVolume.SetMasterVolume(clamped, Guid.Empty);
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

        if (_device is not null)
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

            try
            {
                _enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pEnumerator);
            }
            finally
            {
                Marshal.Release(pEnumerator);
            }

            // 3. 获取默认音频渲染设备
            hr = _enumerator.GetDefaultAudioEndpoint(
                WasapiInterop.EDataFlow_Render,
                WasapiInterop.ERole_Console,
                out IntPtr pDevice);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                _device = (IMMDevice)Marshal.GetObjectForIUnknown(pDevice);
            }
            finally
            {
                Marshal.Release(pDevice);
            }
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
        if (_audioClient is null) return;

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
            int hr = _audioClient.GetCurrentPadding(out uint padding);
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
        if (_audioClient is not null)
        {
            try { _audioClient.Stop(); }
            catch { /* 忽略释放时的错误 */ }
        }

        // 逆序释放
        ReleaseComObject(ref _audioClock);
        ReleaseComObject(ref _simpleVolume);
        ReleaseComObject(ref _renderClient);
        ReleaseComObject(ref _audioClient);
        ReleaseComObject(ref _device);
        ReleaseComObject(ref _enumerator);
    }

    /// <summary>
    /// 仅释放 Initialize 方法创建的 COM 对象（不含 _device/_enumerator）。
    /// 用于 Initialize 失败时的清理，保留 _device/_enumerator 以便用户重试。
    /// </summary>
    private void ReleaseInitializeObjects()
    {
        if (_audioClient is not null)
        {
            try { _audioClient.Stop(); }
            catch { /* 忽略释放时的错误 */ }
        }

        ReleaseComObject(ref _audioClock);
        ReleaseComObject(ref _simpleVolume);
        ReleaseComObject(ref _renderClient);
        ReleaseComObject(ref _audioClient);
    }

    /// <summary>
    /// 安全释放单个 COM 对象。
    /// </summary>
    private void ReleaseComObject<T>(ref T? obj) where T : class
    {
        if (obj is null) return;
        try
        {
            Marshal.ReleaseComObject(obj);
        }
        catch { /* 忽略释放时的错误 */ }
        obj = null;
    }
}

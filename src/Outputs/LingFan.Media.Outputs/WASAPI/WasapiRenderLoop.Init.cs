using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

// WasapiRenderLoop 的 Init 职责段（部分类，由 WasapiRenderLoop.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Submit/Pause/Resume/时钟/Dispose/STA 线程控制）在主文件；
// 全部 COM 调用收在唯一 STA 渲染线程，本段方法均经 RunControl 封送或仅在该线程执行。
internal sealed partial class WasapiRenderLoop
{
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
            // 此前基线注释抄漏了相对槽 2 的
            //    GetStreamLatency，导致 GetCurrentPadding 起整体 -1 错位——GetMixFormat(误取槽4)
            //    实际调到 IsFormatSupported，x64 下垃圾 pFormat 被解引用 → 原生访问违规。
            _audioClientInitialize = ComVTable.Get<IAudioClient_Initialize>(pAudioClient, 0);
            _audioClientGetBufferSize = ComVTable.Get<IAudioClient_GetBufferSize>(pAudioClient, 1);
            // 补上此前未接入的 slot 2 GetStreamLatency（不扰动后续槽位，
            // 因为 GetCurrentPadding 仍在 slot 3，相对索引未变）。用于把主时钟从「设备渲染游标」校准到
            // 「真实可闻位置」——之前不减数百 ms 延迟导致视频比听到的声音整体提前。
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

            // 1.5 O10：在 Initialize 之前，通过 IAudioClient2.SetClientProperties 设置会话分类，
            // 防止 Windows 将后台/非前台/隐藏窗口的音频会话在播放片刻后挂起（声音中断）。
            // 全程 try/guard：任何不支持/失败都只记日志，不影响后续正常 Initialize（最坏退回旧行为）。
            // 此处槽位须为 13（绝对槽 16），算错一格会直接导致原生访问违规：若误取 12（绝对槽 15），
            // 实际调到 IAudioClient2::IsOffloadCapable —— 它多一个 BOOL* 出参，向未初始化寄存器指向的野地址写入。
            // 槽位已修正为 13，COM 探针九个分类均 S_OK，调用本身不再崩。
            // 但 EnableBackgroundCapableSession 仍默认 false：启用它的原始动机（防 OS 挂起后台会话）未被证实，
            // 且启用后出现「长时间静音（数十秒级）后才出声」的现象。详见 WasapiOptions 上的说明。
            if (_options.EnableBackgroundCapableSession)
                TrySetSessionCategory(pAudioClient);

            // 2. 格式协商（独占模式 + 多格式直出）
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

            // 事件驱动模式
            int streamFlags = _eventDrivenMode
                ? WasapiInterop.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                : 0;

            // 共享模式下必须显式要求音频引擎
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

            // 独占模式错误处理
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
            // 给出可诊断的错误，而不是笼统的 HRESULT。
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

            // 4. 事件驱动模式——注册事件句柄
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

            // 5.5 音画同步校准：获取「提交→可闻」流延迟，用于主时钟校准。
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

                // 频率在流的生命周期内恒定，初始化时取一次即可。
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
            // 修复：重置 _eventDrivenMode 到用户配置值。
            _eventDrivenMode = _options.EventDrivenMode;
            throw;
        }
    }

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
            // mix 参数未知时记为客户端参数，避免后续日志出现 0Hz/0ch 误导。
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
                    // 修复：ppClosestMatch 传 IntPtr.Zero（按值），避免 WASAPI 分配 CoTaskMem 内存后泄漏
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
            // 原返回 BuildWaveFormat(_mixSampleRate, _mixChannels, ...)，
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
    /// 通过 IAudioClient2.SetClientProperties 设置音频会话分类，防止 Windows 挂起后台/非前台/隐藏窗口的会话。
    /// 必须在 IAudioClient.Initialize 之前调用。任何失败均记录日志并静默跳过，不影响正常初始化。
    /// </summary>
    /// <remarks>
    /// IAudioClient2 由 IAudioClient 指针 QI 获得（vtable 绝对槽 0 = IUnknown.QueryInterface）。
    /// vtable 绝对槽位（逐方法照抄 audioclient.h，勿凭记忆推算）：
    ///   IUnknown      : QueryInterface(0) AddRef(1) Release(2)
    ///   IAudioClient  : Initialize(3) GetBufferSize(4) GetStreamLatency(5) GetCurrentPadding(6)
    ///                   IsFormatSupported(7) GetMixFormat(8) GetDevicePeriod(9) Start(10)
    ///                   Stop(11) Reset(12) SetEventHandle(13) GetService(14)   —— 共 12 个方法
    ///   IAudioClient2 : IsOffloadCapable(15) SetClientProperties(16) GetBufferSizeLimits(17)
    /// 故 SetClientProperties 绝对槽 = 16，ComVTable slotIndex = 16 - 3 = 13。
        /// 若误写为 slotIndex=12（绝对槽 15），实际调到 IsOffloadCapable —— 它有 3 个参数
        /// (self, Category, BOOL* pbOffloadCapable)，本处只传 2 个，x64 下 R8 是未初始化垃圾值，
        /// 原生侧向该野地址写 BOOL ⇒ 确定性原生访问违规。
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
                // SetClientProperties 绝对槽 = 16（IUnknown 3 + IAudioClient 12 方法占 3..14
                // + IAudioClient2 首方法 IsOffloadCapable 占 15），故 slotIndex = 16 - 3 = 13。
                // 绝不能是 12（=IsOffloadCapable，参数个数不同，误调 ⇒ 野指针写 ⇒ 原生访问违规）。
                // 取函数指针后判空：若槽位异常（理论上不会），跳过而非崩进程。
                IntPtr setPropsPtr = ComVTable.GetMethodPointer(pClient2, 13);
                if (setPropsPtr == IntPtr.Zero)
                {
                    _logger.LogWarning("IAudioClient2.SetClientProperties 槽位为空，跳过会话分类设置");
                    return;
                }
                var setProps = Marshal.GetDelegateForFunctionPointer<IAudioClient2_SetClientProperties>(setPropsPtr);

                // 候选分类链：优先用户配置值，其次同族媒体类兜底（某些 driver 只认部分分类）。
                // 注意：SetClientProperties 槽位若算错（误调 IsOffloadCapable），任意分类都会触发原生访问违规；
                // 上文已固定为 slotIndex 13，可规避此问题。
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
                        bIsOffload = 0, // 必须 FALSE：本库走常规共享模式；置 TRUE 会申请硬件卸载流并崩溃
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
    /// 仅释放 Initialize 创建的 COM 对象（不含 _device/_enumerator），用于 Initialize 失败清理。
    /// </summary>
    private void ReleaseInitializeObjects()
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
    }
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

// WasapiRenderLoop 的格式协商与会话分类职责段（部分类，按职责拆分，编译期等价）。
// 共享/独占模式 WAVEFORMATEX 协商、IAudioClient2 会话分类、初始化对象释放；
// 设备枚举与客户端创建在 Init 主段。全部在 STA 渲染线程执行。
internal sealed partial class WasapiRenderLoop
{
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

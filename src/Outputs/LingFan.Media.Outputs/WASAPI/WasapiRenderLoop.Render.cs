using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

// WasapiRenderLoop 的 Render 职责段（部分类，由 WasapiRenderLoop.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Submit/Pause/Resume/时钟/Dispose/STA 线程控制）在主文件；
// 全部 COM 调用收在唯一 STA 渲染线程，本段方法均经 RunControl 封送或仅在该线程执行。
internal sealed partial class WasapiRenderLoop
{
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
                        {
                            // 惰性初始化（采样率延迟填充场景，如 FFmpeg+AAC）：OpenAsync 时刻
                            // OutputSampleRate 可能为 0，无法提前打开 WASAPI 设备。首个音频帧到达渲染
                            // 线程时按帧的真实采样率/声道数打开设备。必须在渲染线程内直接调用
                            // InitializeImpl（禁止 RunControl，否则自死锁）。
                            var f = item.Frame!;
                            if (!_initialized)
                            {
                                bool lazyOk = false;
                                try
                                {
                                    InitializeImpl(f.SampleRate, f.Channels);
                                    lazyOk = true;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "[WASAPI] 惰性初始化失败（首帧 rate={Rate} ch={Ch}），丢弃该帧",
                                        f.SampleRate, f.Channels);
                                }
                                if (!lazyOk) continue; // finally 仍会 item.Done.Set()
                            }
                            WriteFrame(f);
                        }
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
                // 事件驱动模式下必须同时等待引擎缓冲事件(_bufferEvent)，否则引擎欠载时
                // 发出的缓冲信号被错过 → 引擎暂停拉取 → 缓冲恒满 → 音频饥饿死锁。轮询模式(_bufferEvent==null)仅等新帧。
                if (_bufferEvent != null)
                    WaitHandle.WaitAny(new WaitHandle[] { _workAvailable, _bufferEvent });
                else
                    _workAvailable.WaitOne();
            }
        }
        finally
        {
            WasapiInterop.CoUninitialize();
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

        // 分段写入 + 缓冲事件续写，既不丢样本也不死锁。要点：
        //  (1)若要求「整帧连续空间」并硬阻塞，配合 BeginStreamingAsync 在 !_initialized 时 early-return
        //    （preroll 从未 arm）→ 设备永不 Start → 引擎不消费 → 缓冲恒满 → 每帧超时 → 音频饥饿死锁；
        //    且启动锚点永不捕获 → 主时钟恒 0 → 视频管线永久 Wait（现象：present=1、dropped=0，画面全程冻结）。
        //  (2)若改为「只写 available、写不下的直接丢弃」：解了死锁，却把背压变成了采样丢弃 →
        //    持续跳样 → 电音 / 音频加速（submitted 远小于 played 即此症状）。
        //  (3)本实现：循环分段写入直至整帧写完；空间不足时在渲染线程内等一次引擎缓冲事件后续写。
        //    Submit 本就阻塞等待渲染线程完成，故此举等价于「把音频管线节流到设备实时速率」——正确的背压形态，
        //    一个采样都不丢。等待有 BufferWaitTimeoutMs 上限 + _shutdownEvent 立即放弃，绝不退化为无界阻塞。
        //    帧大小可超过设备缓冲区（分段天然支持），故不再限制 frame.FrameCount <= _bufferSize。
        int totalFrames = frame.FrameCount;
        int writtenFrames = 0;

        while (writtenFrames < totalFrames)
        {
            uint padding = GetCurrentPaddingFrames();
            uint available = padding >= (uint)_bufferSize ? 0u : (uint)_bufferSize - padding;

            if (available == 0)
            {
                // 缓冲满。先确保引擎已在消费，否则等待必然全部超时：
                // (1)preroll 已 arm → 正常自动启动路径；(2)调用方未走 BeginStreaming/Resume → 兜底强制启动。
                TryStartPreroll();
                EnsureDeviceStarted();

                if (!WaitForBufferSpaceOnce())
                {
                    // 设备停摆或正在关闭：放弃本帧剩余部分（计数可观测），绝不无界阻塞渲染线程。
                    _droppedFrames++;
                    break;
                }
                continue;
            }

            uint toWrite = Math.Min((uint)(totalFrames - writtenFrames), available);
            int toWriteSamples = (int)toWrite * frame.Channels;
            int toWriteBytes = toWriteSamples * bytesPerSample;
            int srcByteOffset = writtenFrames * frame.Channels * bytesPerSample;

            // 获取 WASAPI 缓冲区指针
            int hr = _renderClientGetBuffer!(_renderClientPtr, toWrite, out IntPtr pData);
            Marshal.ThrowExceptionForHR(hr);

            // gotBuffer 标记 GetBuffer 是否已成功锁定缓冲区；released 标记是否已正常释放。
            bool gotBuffer = true;
            bool released = false;
            try
            {
                var validSrc = frame.Data.Span.Slice(srcByteOffset, toWriteBytes);

                unsafe
                {
                    CopyOrConvert(validSrc, pData, toWriteSamples, frame.SampleFormat, _deviceSampleFormat);
                }

                hr = _renderClientReleaseBuffer!(_renderClientPtr, toWrite, 0);
                Marshal.ThrowExceptionForHR(hr);
                released = true;
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

            writtenFrames += (int)toWrite;
            _submittedSamples += toWrite;   // 诊断：累计成功提交的采样帧数

            // 消除起播静默窗：首段真实 PCM 落入设备缓冲后立即启动引擎，抓取的是真实数据而非静音。
            // 在每段写入后检查（而非整帧写完后），保证后续分段等待时引擎已在消费，等待必有进展。
            TryStartPreroll();
        }

        if (writtenFrames > 0)
            _submittedFrames++;
    }

    /// <summary>
    /// preroll 阶段的自动启动：首段真实 PCM 写入设备缓冲后立即 Start，引擎抓取真实数据 → 无起播静默窗。
    /// 幂等（仅首次生效），必须在渲染线程内调用。
    /// </summary>
    private void TryStartPreroll()
    {
        if (!_prerollPending || _prerollStarted) return;

        int hr = _audioClientStart!(_audioClientPtr);
        if (hr < 0)
        {
            _logger.LogWarning("preroll 自动启动失败：IAudioClient.Start HRESULT=0x{HR:X8}", hr);
            return;
        }

        _deviceRunning = true;
        CaptureStartAnchor();
        _prerollStarted = true;
        _prerollPending = false;
        _primeTcs?.TrySetResult(true);
    }

    /// <summary>
    /// 兜底保障：设备从未启动过时强制 Start 并捕获启动锚点。
    /// </summary>
    /// <remarks>
    /// 主时钟（<see cref="GetPlaybackPositionDirect"/>）依赖启动锚点 <c>_startStopwatch</c>；锚点为 null 时
    /// 恒返回 0，视频管线据此判定「每帧都还没到时间」→ 永久等待 → 画面冻结。三个启动点（Resume /
    /// preroll 自动启动 / prime 超时兜底）均会捕获锚点，故 <c>_startStopwatch == null</c> 精确等价于
    /// 「设备从未启动」。仅在缓冲满且无人消费时触发，正常路径永不进入。
    /// </remarks>
    private void EnsureDeviceStarted()
    {
        if (_startStopwatch != null || _audioClientPtr == IntPtr.Zero || _audioClientStart is null)
            return;

        int hr = _audioClientStart(_audioClientPtr);
        if (hr < 0 && hr != WasapiInterop.AUDCLNT_E_NOT_STOPPED)
        {
            _logger.LogWarning("兜底启动失败：IAudioClient.Start HRESULT=0x{HR:X8}", hr);
            return;
        }

        _deviceRunning = true;
        CaptureStartAnchor();
        _logger.LogWarning(
            "[WASAPI] 设备缓冲已满但引擎从未启动，已强制 Start 并捕获锚点" +
            "（调用方未经 BeginStreamingAsync/Resume 即提交音频）。");
    }

    /// <summary>
    /// 缓冲区已满时等待音频引擎腾出空间（至多一个等待周期）。
    /// </summary>
    /// <returns>
    /// <see langword="true"/> 表示可以重试写入；<see langword="false"/> 表示设备停摆或正在关闭，
    /// 调用方应放弃剩余数据（记 droppedFrames），避免阻塞渲染线程上的控制消息。
    /// </returns>
    private bool WaitForBufferSpaceOnce()
    {
        if (_shutdownEvent.IsSet) return false;
        // 引擎未在消费（Pause/Stop 后）：空间永不会释放，等待纯属空耗，立即放弃。
        if (!_deviceRunning) return false;

        if (_bufferEvent != null)
        {
            // 事件驱动：引擎每个周期（通常 ~10ms）发一次缓冲信号；关闭信号同等优先，任一到达即返回。
            int idx = WaitHandle.WaitAny(
                new WaitHandle[] { _bufferEvent, _shutdownEvent.WaitHandle }, BufferWaitTimeoutMs);
            return idx == 0;
        }

        // 轮询模式（设备不支持事件驱动）：短睡后重试；关闭信号到达则放弃。
        return !_shutdownEvent.Wait(BufferPollIntervalMs);
    }

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
}

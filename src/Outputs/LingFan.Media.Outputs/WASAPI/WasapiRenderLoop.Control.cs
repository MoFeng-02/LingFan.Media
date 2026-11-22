using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

// WasapiRenderLoop 的播放控制职责段（部分类，按职责拆分，编译期等价）。
// Pause/Resume/BeginStreaming（preroll 起播）/Flush，全部经 RunControl 封送到 STA 渲染线程；
// 字段与提交编排在主文件。
internal sealed partial class WasapiRenderLoop
{
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
}

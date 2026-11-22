using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

// WasapiRenderLoop 的播放时钟职责段（部分类，按职责拆分，编译期等价）。
// IAudioClock 位置读取（QPC 插值 + 引擎领先校准）与主时钟武装/归零；
// 字段与提交/控制编排在主文件。IAudioClock.GetPosition 可由任意线程调用（MSDN）。
internal sealed partial class WasapiRenderLoop
{
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
}

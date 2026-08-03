using System.Diagnostics;

namespace LingFan.Media.Core;

/// <summary>
/// 播放节奏（frame pacing）与时钟稳定性诊断。
/// </summary>
/// <remarks>
/// <para>环境变量门控，默认全关、零开销（<see cref="Enabled"/> 为 false 时所有记录点都是一次布尔判断）。</para>
/// <list type="bullet">
/// <item><c>LINGFAN_PACING_DIAG=1</c> —— 启用</item>
/// <item><c>LINGFAN_PACING_EVERY=N</c> —— 每 N 次 Present 输出一次滚动报告（默认 100）</item>
/// </list>
/// <para>诊断记录器为进程级静态单例：同时运行多个播放器时统计会混合。
/// 诊断场景恒为单播放器，此简化可接受；生产路径不受影响（默认关闭）。</para>
/// </remarks>
internal static class PacingDiagnostics
{
    /// <summary>是否启用节奏诊断。</summary>
    internal static readonly bool Enabled = ParseBool("LINGFAN_PACING_DIAG");

    /// <summary>每多少次 Present 输出一次滚动报告。</summary>
    internal static readonly int ReportEvery = ParseInt("LINGFAN_PACING_EVERY", 100);

    /// <summary>
    /// 报告的时间兜底间隔（毫秒，默认 2000）。
    /// </summary>
    /// <remarks>
    /// 仅按 Present 次数触发会有致命盲区：若视频帧被整批 <c>Drop</c>（正是我们要抓的症状），
    /// <c>OnPresent</c> 不被调用 ⇒ 永远不出报告 ⇒ 现场最严重的时刻反而无输出。
    /// 故 Wait / Drop 分支也轮询本间隔强制出报告。
    /// </remarks>
    internal static readonly int ReportIntervalMs = ParseInt("LINGFAN_PACING_INTERVAL_MS", 2000);

    /// <summary>时钟跳变记录器（由 <c>MediaClock.SyncTo</c> 写入）。</summary>
    internal static readonly ClockJumpRecorder Clock = new();

    /// <summary>呈现节奏记录器（由 <c>VideoPipeline.ProcessFrame</c> 写入）。</summary>
    internal static readonly PresentPacingRecorder Present = new();

    private static bool ParseBool(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return raw is "1" or "true" or "TRUE" or "yes";
    }

    private static int ParseInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var v) && v > 0 ? v : fallback;
    }
}

/// <summary>
/// 时钟跳变记录器。度量 <c>SyncTo</c> 造成的位置不连续量。
/// </summary>
/// <remarks>
/// <para><b>raw drift</b> = 主时钟位置 − 本地推算位置，即"若硬跳会跳多少"。
/// 它度量的是**主时钟（音频提交进度）本身的突发程度**，与是否启用软同步无关。</para>
/// <para><b>applied</b> = 真正施加到时钟的位置变化量。硬跳模式下 applied == raw；
/// 软同步模式下 applied 被低通 + 钳位后应远小于 raw —— 两者之差就是被滤掉的抖动。</para>
/// <para><b>applied &lt; 0 即时钟回退</b>——视频帧的 delta 会突然变大，
/// 已判 Present 的时间点重新变成 Wait，肉眼即「画面回退／停顿」。</para>
/// </remarks>
internal sealed class ClockJumpRecorder
{
    private readonly object _lock = new();
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private int _count;
    private bool _audioDriven;           // 音频真实播放游标已接管主时钟（旁路批内 SyncTo）
    private int _hardSnaps;

    // 相邻两次 SyncTo 的墙钟间隔 —— 用于判定"批内突发"假设
    private double _lastSyncMs = -1;
    private int _gapBurst;          // 间隔 < 1ms：同一批内被连续调用
    private double _maxGapMs;
    private double _sumGap;
    private int _gapCount;

    // 实际施加量
    private int _backward;      // applied < -1ms 的次数
    private int _forward;       // applied > +1ms 的次数
    private double _maxBackwardMs;
    private double _maxForwardMs;
    private double _sumAbs;
    private double _sumSq;

    // 主时钟原始偏差
    private int _rawBackward;
    private double _rawMaxBackwardMs;
    private double _rawMaxForwardMs;
    private double _rawSumAbs;

    /// <summary>记录一次同步。</summary>
    /// <param name="appliedMs">实际施加到时钟的位置变化量（负数 = 时钟回退）。</param>
    /// <param name="rawDriftMs">主时钟与本地推算的原始偏差（硬跳模式下等于 <paramref name="appliedMs"/>）。</param>
    /// <param name="hardSnap">本次是否走了硬赋值。</param>
    internal void Record(double appliedMs, double rawDriftMs, bool hardSnap)
    {
        lock (_lock)
        {
            _count++;
            if (hardSnap) _hardSnaps++;

            double nowMs = _wall.Elapsed.TotalMilliseconds;
            if (_lastSyncMs >= 0)
            {
                double gap = nowMs - _lastSyncMs;
                _gapCount++;
                _sumGap += gap;
                if (gap < 1.0) _gapBurst++;
                if (gap > _maxGapMs) _maxGapMs = gap;
            }
            _lastSyncMs = nowMs;

            _sumAbs += Math.Abs(appliedMs);
            _sumSq += appliedMs * appliedMs;

            if (appliedMs < -1.0)
            {
                _backward++;
                if (appliedMs < _maxBackwardMs) _maxBackwardMs = appliedMs;
            }
            else if (appliedMs > 1.0)
            {
                _forward++;
                if (appliedMs > _maxForwardMs) _maxForwardMs = appliedMs;
            }

            _rawSumAbs += Math.Abs(rawDriftMs);
            if (rawDriftMs < -1.0)
            {
                _rawBackward++;
                if (rawDriftMs < _rawMaxBackwardMs) _rawMaxBackwardMs = rawDriftMs;
            }
            else if (rawDriftMs > _rawMaxForwardMs)
            {
                _rawMaxForwardMs = rawDriftMs;
            }
        }
    }

    /// <summary>标记音频时钟已接管（旁路批内 SyncTo）。仅用于快照文案如实反映状态。</summary>
    internal void MarkAudioDriven()
    {
        lock (_lock) _audioDriven = true;
    }

    /// <summary>生成快照文本（不清零，累计统计）。</summary>
    internal string Snapshot()
    {
        lock (_lock)
        {
            if (_count == 0)
                // 注意：_count==0 仅在"批内 SyncTo 未被调用"时出现。音频时钟接管时这是预期行为
                // （OnAudioFrameSubmitted 直接返回），故此处如实区分，不再误报"音频未驱动时钟"。
                return _audioDriven
                    ? "SyncTo=0（音频时钟已驱动：批内SyncTo已旁路，无锯齿）"
                    : "SyncTo=0（时钟未活动：尚无音频提交）";

            double meanAbs = _sumAbs / _count;
            double rms = Math.Sqrt(_sumSq / _count);
            double backPct = 100.0 * _backward / _count;
            double rawMeanAbs = _rawSumAbs / _count;
            double rawBackPct = 100.0 * _rawBackward / _count;

            string verdict = _backward == 0
                ? "时钟单调"
                : backPct > 20.0
                    ? $"★时钟频繁回退（{backPct:F1}%）⇒ 画面回退/停顿的直接来源★"
                    : $"时钟偶发回退（{backPct:F1}%）";

            string mode = ClockTuning.SmoothSync
                ? $"软同步(f={ClockTuning.SmoothFactor:F2} 步长≤{ClockTuning.MaxStepMs:F1}ms 硬跳={_hardSnaps}次)"
                : "硬跳变(基线)";

            // 批内突发判定：SubmitBatch 的第一个 foreach 是纯内存操作（变换链+SyncTo+sink），
            // 真正的 _output.Submit 在其后才执行。若假设成立，同一批内的 SyncTo 会以 <1ms 间隔连发。
            string burstText;
            if (_gapCount == 0)
            {
                burstText = "SyncTo间隔=样本不足";
            }
            else
            {
                double burstPct = 100.0 * _gapBurst / _gapCount;
                double gapMean = _sumGap / _gapCount;
                string burstVerdict = burstPct > 50.0
                    ? "★批内突发确认：时钟被整批预支★"
                    : burstPct > 10.0
                        ? "部分突发"
                        : "提交节奏均匀（背压主导）";
                burstText = $"SyncTo间隔: 突发(<1ms)={_gapBurst}/{_gapCount}({burstPct:F1}%) " +
                            $"均值={gapMean:F1}ms 最大={_maxGapMs:F1}ms => {burstVerdict}";
            }

            return $"[{mode}] SyncTo={_count} | " +
                   $"施加: 回退={_backward}次({backPct:F1}%) 最大回退={_maxBackwardMs:F1}ms " +
                   $"前跳={_forward}次 最大前跳={_maxForwardMs:F1}ms |applied|均值={meanAbs:F1}ms RMS={rms:F1}ms | " +
                   $"主时钟原始偏差: 回退={rawBackPct:F1}% 最大回退={_rawMaxBackwardMs:F1}ms " +
                   $"最大前跳={_rawMaxForwardMs:F1}ms |drift|均值={rawMeanAbs:F1}ms | " +
                   $"{burstText} => {verdict}";
        }
    }
}

/// <summary>
/// 呈现节奏记录器。度量相邻两次 Present 的真实墙钟间隔与 PTS 间隔之差。
/// </summary>
/// <remarks>
/// <para>理想节奏：墙钟间隔 ≈ PTS 间隔，jitter ≈ 0。</para>
/// <para><b>突发</b>（间隔 &lt; 5ms）= 多帧被攒住后批量放行 ⇒ 肉眼「突然向前」。</para>
/// <para><b>停滞</b>（间隔 &gt; 2 倍标称帧距）= 帧没赶上呈现时刻 ⇒ 肉眼「卡住」。</para>
/// <para><b>PTS 倒退</b> = 真实乱序（队列/线程竞态），与时钟无关，性质更严重。</para>
/// </remarks>
internal sealed class PresentPacingRecorder
{
    private readonly object _lock = new();
    private readonly Stopwatch _wall = Stopwatch.StartNew();

    private long _presentCount;
    private long _waitSpins;
    private long _drops;

    private double _lastWallMs = -1;
    private TimeSpan _lastPts = TimeSpan.MinValue;

    // 滚动窗口统计（每次报告后清零）
    private int _winCount;
    private double _winSumGap;
    private double _winSumGapSq;
    private double _winMaxGap;
    private double _winMinGap = double.MaxValue;
    private double _winSumPtsGap;
    private int _winBurst;      // 墙钟间隔 < 5ms
    private int _winStall;      // 墙钟间隔 > 2x 标称
    private int _winPtsBackward;
    private long _winWaitBase;
    private long _winDropBase;
    private int _winQueueSum;

    // Thread.Sleep(1) 实测耗时（累计，不随窗口清零）
    private long _sleepCount;
    private double _sleepSum;
    private double _sleepMax;

    // 呈现误差（相对"最早可呈现时刻"偏移，毫秒）：解耦后理想 ≈ 0~2ms。
    // 累计量（不随窗口清零）用于观察全局最坏情况。
    private double _sumErr;
    private double _sumErrSq;
    private double _maxErr;
    private double _winSumErr;
    private double _winSumErrSq;

    // 上次出报告的墙钟时刻（时间兜底用）
    private double _lastReportMs;

    /// <summary>记录一次 Present。返回非 null 时表示应输出滚动报告。</summary>
    /// <param name="pts">帧显示时间戳。</param>
    /// <param name="queueDepth">呈现时刻的帧队列深度（>0 表示有前向缓冲，解码已解耦）。</param>
    /// <param name="errMs">呈现误差（相对最早可呈现时刻，毫秒）：理想 ≈ 0~2ms。</param>
    internal string? OnPresent(TimeSpan pts, int queueDepth, double errMs)
    {
        lock (_lock)
        {
            _presentCount++;
            double nowMs = _wall.Elapsed.TotalMilliseconds;
            _winQueueSum += queueDepth;

            _sumErr += errMs;
            _sumErrSq += errMs * errMs;
            if (errMs > _maxErr) _maxErr = errMs;

            if (_lastWallMs >= 0)
            {
                double gap = nowMs - _lastWallMs;
                double ptsGap = (pts - _lastPts).TotalMilliseconds;

                _winCount++;
                _winSumGap += gap;
                _winSumGapSq += gap * gap;
                _winSumPtsGap += ptsGap;
                _winSumErr += errMs;
                _winSumErrSq += errMs * errMs;
                if (gap > _winMaxGap) _winMaxGap = gap;
                if (gap < _winMinGap) _winMinGap = gap;
                if (gap < 5.0) _winBurst++;
                if (ptsGap < 0) _winPtsBackward++;
                // 标称帧距用 PTS 间隔近似；PTS 异常时回退到 33ms
                double nominal = ptsGap > 1.0 && ptsGap < 200.0 ? ptsGap : 33.0;
                if (gap > nominal * 2.0) _winStall++;
            }

            _lastWallMs = nowMs;
            _lastPts = pts;

            if (_presentCount % PacingDiagnostics.ReportEvery != 0)
                return null;

            return BuildReportLocked(nowMs);
        }
    }

    /// <summary>
    /// 时间兜底轮询：距上次报告超过 <see cref="PacingDiagnostics.ReportIntervalMs"/> 则强制出一份。
    /// </summary>
    /// <remarks>
    /// 由 Wait / Drop 分支调用。画面冻结时 <see cref="OnPresent"/> 不被调用，
    /// 只有本方法能把"窗口内 Present=0"这一最关键的现场打出来。
    /// </remarks>
    internal string? PollReport()
    {
        lock (_lock)
        {
            double nowMs = _wall.Elapsed.TotalMilliseconds;
            if (nowMs - _lastReportMs < PacingDiagnostics.ReportIntervalMs)
                return null;

            return BuildReportLocked(nowMs);
        }
    }

    private string BuildReportLocked(double nowMs)
    {
        _lastReportMs = nowMs;

        long waits = _waitSpins - _winWaitBase;
        long drops = _drops - _winDropBase;

        string sleepText = _sleepCount > 0
            ? $"等待耗时 均值={_sleepSum / _sleepCount:F2}ms 最大={_sleepMax:F1}ms"
            : "等待耗时未触发";

        string report;
        if (_winCount == 0)
        {
            // 窗口内一次 Present 都没有 —— 这本身就是"画面冻结"的直接证据
            report =
                $"present={_presentCount} 本窗口 Present=0 | Wait自旋={waits} Drop={drops} | {sleepText} " +
                $"=> ★画面冻结（{(drops > waits ? "帧被丢弃：时钟超前于视频" : "帧在等待：时钟落后于视频")}）★";
        }
        else
        {
            double mean = _winSumGap / _winCount;
            double var = Math.Max(0, _winSumGapSq / _winCount - mean * mean);
            double std = Math.Sqrt(var);
            double ptsMean = _winSumPtsGap / _winCount;
            double avgQueue = (double)_winQueueSum / (_winCount + 1);
            double errMean = _winSumErr / _winCount;
            double errRms = Math.Sqrt(Math.Max(0, _winSumErrSq / _winCount - errMean * errMean));

            string verdict = _winPtsBackward > 0
                ? $"★PTS 倒退 {_winPtsBackward} 次 = 真实乱序★"
                : std > mean * 0.35
                    ? $"★节奏抖动严重（标准差/均值={std / mean:P0}）★"
                    : std > mean * 0.18
                        ? "节奏抖动偏大"
                        : "节奏平稳";

            // 解码解耦判据：队列深度均值 > 0 即证明呈现侧永不被饿死（前帧已在缓冲中）。
            string decodeText = avgQueue > 0.5
                ? $"队列深度均值={avgQueue:F1}(解码已解耦)"
                : $"队列深度均值={avgQueue:F1}(★仍贴空：解码可能仍饿死呈现★)";

            report =
                $"present={_presentCount} 窗口={_winCount} | 墙钟间隔 均值={mean:F1}ms 标准差={std:F1}ms " +
                $"最小={_winMinGap:F1}ms 最大={_winMaxGap:F1}ms | PTS间隔均值={ptsMean:F1}ms | " +
                $"突发(<5ms)={_winBurst} 停滞(>2x)={_winStall} PTS倒退={_winPtsBackward} | " +
                $"Wait自旋={waits} Drop={drops} | {decodeText} | " +
                $"呈现误差 均值={errMean:F1}ms RMS={errRms:F1}ms 全局最大={_maxErr:F1}ms | " +
                $"{sleepText} => {verdict}";
        }

        // 清零滚动窗口（累计量 _sumErr/_sumErrSq/_maxErr 保留以观察全局最坏）
        _winCount = 0;
        _winSumGap = 0;
        _winSumGapSq = 0;
        _winSumPtsGap = 0;
        _winMaxGap = 0;
        _winMinGap = double.MaxValue;
        _winBurst = 0;
        _winStall = 0;
        _winPtsBackward = 0;
        _winQueueSum = 0;
        _winSumErr = 0;
        _winSumErrSq = 0;
        _winWaitBase = _waitSpins;
        _winDropBase = _drops;

        return report;
    }

    /// <summary>记录一次 Wait 自旋。</summary>
    internal void OnWait() => Interlocked.Increment(ref _waitSpins);

    /// <summary>记录一次丢帧。</summary>
    internal void OnDrop() => Interlocked.Increment(ref _drops);

    /// <summary>
    /// 记录一次 <c>Thread.Sleep(1)</c> 的实测耗时（毫秒）。
    /// Windows 默认定时器分辨率 15.6ms，实测值远大于 1ms 即证明自旋等待精度不可用。
    /// </summary>
    internal void OnSleepMeasured(double ms)
    {
        lock (_lock)
        {
            _sleepCount++;
            _sleepSum += ms;
            if (ms > _sleepMax) _sleepMax = ms;
        }
    }
}

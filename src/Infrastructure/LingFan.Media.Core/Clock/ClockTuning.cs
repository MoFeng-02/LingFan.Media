namespace LingFan.Media.Core;

/// <summary>
/// 媒体时钟同步策略调优开关（环境变量门控，用于 A/B 对照验证）。
/// </summary>
/// <remarks>
/// <para><b>背景</b>：<c>AudioPipeline.SubmitBatch</c> 在一个紧凑 foreach 内逐帧调用
/// <c>Synchronizer.OnAudioFrameSubmitted</c> → <c>MediaClock.SyncTo</c>。批内几乎无时间流逝，
/// 而每帧携带的媒体时间各差一个帧时长，于是时钟在极短墙钟时间内被连续硬跳前进整批时长；
/// 随后靠 Stopwatch 自由运行超调，下一批首帧再把它猛拽回来 —— 形成锯齿波。</para>
/// <para>时钟回退会让 <c>Synchronizer.CheckVideoFrame</c> 的 delta 突然变大，
/// 已到期的帧重新判为 Wait，肉眼即「画面停住/回退」；随后时钟猛跳前进又让多帧同时到期，
/// 批量 Present，肉眼即「突然向前」。</para>
/// <para><b>软同步</b>：把 <c>SyncTo</c> 从"硬赋值"改为一阶低通逼近 —— 每次只吸收一小部分偏差。
/// 突发被滤掉，时钟保持单调平滑；仅当偏差超过硬跳阈值（seek / 严重失步）才退回硬赋值。</para>
/// </remarks>
internal static class ClockTuning
{
    /// <summary>是否启用软同步（一阶低通逼近）。默认 false = 保持原硬跳变行为。</summary>
    internal static readonly bool SmoothSync = ParseBool("LINGFAN_CLOCK_SMOOTH");

    /// <summary>
    /// 是否启用高精度系统定时器（<c>timeBeginPeriod(1)</c>）。默认 true。
    /// <para>设为 <c>0</c> 关闭：<see cref="System.Threading.Thread.Sleep"/> 退回默认 15.6ms 分辨率，
    /// 视频帧精确等待的粗粒度睡眠会重新被量化成 ±15ms 抖动（仅用于对照/排错）。</para>
    /// </summary>
    internal static readonly bool HighPrecisionTimer = ParseBool("LINGFAN_HP_TIMER", true);

    /// <summary>
    /// 是否用真实音频播放位置作为视频主时钟（替代批提交内的逐帧 SyncTo 突发）。默认 false。
    /// <para><b>这是根治方案</b>：时钟由设备真实播放游标驱动（平滑、单调、缓冲耗尽时自然停摆），
    /// 而非"已提交末端时间"在批内被瞬间预支。配合 <see cref="IAudioOutput.GetPlaybackPositionDirect"/> 零封送读取。</para>
    /// </summary>
    internal static readonly bool UseAudioPlaybackClock = ParseBool("LINGFAN_CLOCK_AUDIO_POS");

    /// <summary>
    /// 低通系数（0~1）。每次 SyncTo 只吸收 <c>drift × factor</c> 的偏差。
    /// 越小越平滑但收敛越慢；默认 0.08。
    /// </summary>
    internal static readonly double SmoothFactor = ParseDouble("LINGFAN_CLOCK_SMOOTH_FACTOR", 0.08, 0.001, 1.0);

    /// <summary>
    /// 硬跳阈值（毫秒）。偏差绝对值超过此值时直接赋值（seek / 起播 / 严重失步）。默认 250ms。
    /// </summary>
    internal static readonly double HardSnapMs = ParseDouble("LINGFAN_CLOCK_SNAP_MS", 250.0, 10.0, 10000.0);

    /// <summary>
    /// 单次调整上限（毫秒，slew rate limit）。低通算出的吸收量再被夹到 ±此值。默认 4ms。
    /// </summary>
    /// <remarks>
    /// <para>低通系数只按比例缩小偏差，遇到 700ms 级突发时 8% 仍有 ~59ms —— 依旧肉眼可见。
    /// 因此再叠一层绝对幅度钳位：无论偏差多大（只要没到硬跳阈值），每次最多挪 4ms，
    /// 小于半个帧间隔，视觉上完全不可察觉。</para>
    /// <para>收敛能力 = 4ms × 每批 SyncTo 次数（PrerollFrames=16 ⇒ 64ms/批），
    /// 足以在数批内吃掉 100~200ms 级的稳态偏差。</para>
    /// </remarks>
    internal static readonly double MaxStepMs = ParseDouble("LINGFAN_CLOCK_MAX_STEP_MS", 4.0, 0.1, 100.0);

    private static bool ParseBool(string name, bool fallback = false)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (raw is "1" or "true" or "TRUE" or "yes")
            return true;
        // 未设置时回落到 fallback（用于"默认开启"类开关，如 LINGFAN_HP_TIMER）
        return fallback && raw is null;
    }

    private static double ParseDouble(string name, double fallback, double min, double max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return fallback;
        return v < min || v > max ? fallback : v;
    }
}

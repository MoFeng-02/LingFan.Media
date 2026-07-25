namespace LingFan.Media.Audio;

/// <summary>
/// 音量/静音控制器。支持平滑音量渐变（避免爆音）。
/// </summary>
/// <remarks>
/// <para><see cref="ApplyRamp"/> 在指定时长内从当前音量平滑过渡到目标音量。
/// 渐变期间 <see cref="Volume"/> 属性返回实时插值结果。</para>
/// <para>使用 <see cref="Environment.TickCount64"/> 做时间基准，AOT 友好。</para>
/// <para>非线程安全（音量设置由 UI 线程调用，<see cref="Volume"/> 读取由音频处理线程调用。
/// V1 接受此限制；V2 可用 <see cref="Interlocked"/> 或 volatile 改进）。</para>
/// </remarks>
public sealed class VolumeControl
{
    private float _volume = 1.0f;
    private float _rampStartVolume;
    private float _rampTargetVolume;
    private long _rampStartMs;
    private long _rampDurationMs;
    private bool _isRamping;

    /// <summary>
    /// 当前音量（0.0~1.0）。渐变进行中时返回实时插值结果。
    /// </summary>
    public float Volume
    {
        get
        {
            if (!_isRamping)
                return _volume;
            return ComputeRampedVolume();
        }
        set
        {
            _isRamping = false;
            _volume = Math.Clamp(value, 0f, 1f);
        }
    }

    /// <summary>是否静音。</summary>
    public bool IsMuted { get; set; }

    /// <summary>
    /// 音量渐变。在指定时长内从当前音量平滑过渡到目标音量，避免爆音。
    /// </summary>
    /// <param name="targetVolume">目标音量（0.0~1.0）。</param>
    /// <param name="duration">渐变时长。</param>
    public void ApplyRamp(float targetVolume, TimeSpan duration)
    {
        targetVolume = Math.Clamp(targetVolume, 0f, 1f);

        if (duration <= TimeSpan.Zero)
        {
            _volume = targetVolume;
            _isRamping = false;
            return;
        }

        _rampStartVolume = Volume;
        _rampTargetVolume = targetVolume;
        _rampStartMs = Environment.TickCount64;
        _rampDurationMs = (long)duration.TotalMilliseconds;
        _isRamping = true;
    }

    /// <summary>
    /// 获取有效音量（考虑静音状态）。
    /// </summary>
    /// <returns>静音时返回 0，否则返回当前音量。</returns>
    public float GetEffectiveVolume()
    {
        return IsMuted ? 0f : Volume;
    }

    private float ComputeRampedVolume()
    {
        var elapsedMs = Environment.TickCount64 - _rampStartMs;

        if (elapsedMs >= _rampDurationMs)
        {
            _volume = _rampTargetVolume;
            _isRamping = false;
            return _volume;
        }

        var progress = (float)elapsedMs / _rampDurationMs;
        return _rampStartVolume + (_rampTargetVolume - _rampStartVolume) * progress;
    }
}

namespace LingFan.Media.Audio;

/// <summary>
/// 音量/静音控制器。支持平滑音量渐变（避免爆音）。
/// </summary>
/// <remarks>
/// <para><see cref="ApplyRamp"/> 在指定时长内从当前音量平滑过渡到目标音量。
/// 渐变期间 <see cref="Volume"/> 属性返回实时插值结果。</para>
/// <para>使用 <see cref="Environment.TickCount64"/> 做时间基准，AOT 友好。</para>
/// <para><b>V2（AU4）线程安全</b>：<see cref="_volume"/>/<see cref="_isRamping"/>/<see cref="_isMuted"/>
/// 使用 volatile 保证跨线程可见性（UI 线程写、音频线程读）。
/// volatile float/bool 字段在 .NET 内存模型中保证 32 位对齐读写的原子性和可见性。
/// 不使用 <c>Interlocked.Exchange(ref _volume, ...)</c>——volatile 字段传 ref 会产生 CS0420 警告。</para>
/// </remarks>
public sealed class VolumeControl
{
    private volatile float _volume = 1.0f;
    private float _rampStartVolume;
    private float _rampTargetVolume;
    private long _rampStartMs;
    private long _rampDurationMs;
    private volatile bool _isRamping;
    private volatile bool _isMuted;

    /// <summary>
    /// 当前音量（0.0~1.0）。渐变进行中时返回实时插值结果。
    /// </summary>
    /// <remarks>
    /// UI 线程写（volatile 写），音频线程读（volatile 读），保证可见性。
    /// setter 直接 volatile 写 _volume 和 _isRamping（不使用 Interlocked，避免 CS0420）。
    /// </remarks>
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
            _volume = Math.Clamp(value, 0f, 1f);
            _isRamping = false;
        }
    }

    /// <summary>是否静音。</summary>
    /// <remarks>volatile 读写保证跨线程可见性（UI 线程写，音频线程读）。</remarks>
    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    /// <summary>
    /// 音量渐变。在指定时长内从当前音量平滑过渡到目标音量，避免爆音。
    /// </summary>
    /// <param name="targetVolume">目标音量（0.0~1.0）。</param>
    /// <param name="duration">渐变时长。</param>
    /// <remarks>
    /// ramp 参数由 UI 线程设置，音频线程读取——volatile 保证可见性。
    /// _isRamping 最后设置（volatile 写），确保前面的 ramp 参数写入对音频线程可见。
    /// </remarks>
    public void ApplyRamp(float targetVolume, TimeSpan duration)
    {
        targetVolume = Math.Clamp(targetVolume, 0f, 1f);

        if (duration <= TimeSpan.Zero)
        {
            _volume = targetVolume;
            _isRamping = false;
            return;
        }

        _rampStartVolume = _volume;
        _rampTargetVolume = targetVolume;
        _rampStartMs = Environment.TickCount64;
        _rampDurationMs = (long)duration.TotalMilliseconds;
        _isRamping = true;  // 最后设置，volatile 写保证前面的写入可见
    }

    /// <summary>
    /// 获取有效音量（考虑静音状态）。
    /// </summary>
    /// <returns>静音时返回 0，否则返回当前音量。</returns>
    public float GetEffectiveVolume()
    {
        return _isMuted ? 0f : Volume;
    }

    /// <summary>
    /// 计算渐变期间的实时音量。
    /// </summary>
    /// <remarks>
    /// 从音频线程调用。ramp 参数由 UI 线程通过 <see cref="ApplyRamp"/> 设置，
    /// volatile 读 _isRamping 保证获取到 ramp 参数的最新值。
    /// 渐变完成时写 _volume 和 _isRamping（volatile 写），与 UI 线程的写竞态可接受——
    /// 最终都会收敛到正确的音量值。
    /// </remarks>
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

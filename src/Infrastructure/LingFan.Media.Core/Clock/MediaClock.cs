using System.Diagnostics;

namespace LingFan.Media.Core;

/// <summary>
/// 媒体时钟实现。音视频同步的核心组件，提供统一的时间基准。
/// </summary>
/// <remarks>
/// <para>纯内存操作，无 I/O 等待。线程安全：使用 <see cref="Stopwatch"/> 做高精度计时，</para>
/// <para>所有属性和方法通过 <c>lock</c> 保证线程安全（视频管线和音频管线并发访问）。</para>
/// <para>Clock 不能注册 Singleton——多播放器会抢同一时钟。</para>
/// </remarks>
public sealed class MediaClock : IMediaClock
{
    private readonly object _lock = new();
    private readonly Stopwatch _stopwatch = new();

    private TimeSpan _basePosition = TimeSpan.Zero;
    private float _speed = 1.0f;
    private bool _isRunning;
    private ClockSyncSource _syncSource = ClockSyncSource.Audio;
    private TimeSpan _syncThreshold = TimeSpan.FromMilliseconds(50);
    private TimeSpan _dropThreshold = TimeSpan.FromMilliseconds(200);

    /// <inheritdoc />
    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                if (!_isRunning)
                    return _basePosition;

                return _basePosition + TimeSpan.FromSeconds(_stopwatch.Elapsed.TotalSeconds * _speed);
            }
        }
    }

    /// <inheritdoc />
    public float Speed
    {
        get
        {
            lock (_lock) { return _speed; }
        }
        set
        {
            lock (_lock)
            {
                if (Math.Abs(_speed - value) < float.Epsilon)
                    return;

                // 修改 Speed 时重新计算 _basePosition 并重置 Stopwatch
                if (_isRunning)
                    _basePosition = Position;

                _speed = value;
                _stopwatch.Restart();
            }
        }
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_lock) { return _isRunning; }
        }
    }

    /// <inheritdoc />
    public ClockSyncSource SyncSource
    {
        get
        {
            lock (_lock) { return _syncSource; }
        }
        set
        {
            lock (_lock) { _syncSource = value; }
        }
    }

    /// <inheritdoc />
    public TimeSpan SyncThreshold
    {
        get
        {
            lock (_lock) { return _syncThreshold; }
        }
        set
        {
            lock (_lock) { _syncThreshold = value; }
        }
    }

    /// <inheritdoc />
    public TimeSpan DropThreshold
    {
        get
        {
            lock (_lock) { return _dropThreshold; }
        }
        set
        {
            lock (_lock) { _dropThreshold = value; }
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning)
                return;

            _isRunning = true;
            _stopwatch.Restart();
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        lock (_lock)
        {
            if (!_isRunning)
                return;

            // 冻结当前位置
            _basePosition = Position;
            _isRunning = false;
            _stopwatch.Stop();
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_lock)
        {
            _basePosition = TimeSpan.Zero;
            _isRunning = false;
            _stopwatch.Reset();
        }
    }

    /// <inheritdoc />
    public void SeekTo(TimeSpan position)
    {
        lock (_lock)
        {
            _basePosition = position;
            if (_isRunning)
                _stopwatch.Restart();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para><b>默认（硬跳变）语义</b>：直接把位置赋为主时钟位置并重置计时。
    /// 主时钟（音频提交进度）本身是突发的阶梯信号时，此处会把突发**原样**传导给
    /// <c>Synchronizer.CheckVideoFrame</c> 的 delta 判定 —— 跳变为负即时钟回退。</para>
    /// <para><b>软同步</b>（<c>LINGFAN_CLOCK_SMOOTH=1</c>）：偏差先按
    /// <see cref="ClockTuning.SmoothFactor"/> 低通、再被 <see cref="ClockTuning.MaxStepMs"/> 钳位，
    /// 只有超过 <see cref="ClockTuning.HardSnapMs"/> 的大偏差（seek / 起播 / 严重失步）才退回硬赋值。</para>
    /// <para>用 <c>LINGFAN_PACING_DIAG=1</c> 可同时量化「主时钟原始突发」与「实际施加的跳变」，
    /// 见 <see cref="PacingDiagnostics"/>。</para>
    /// </remarks>
    public void SyncTo(TimeSpan masterPosition)
    {
        bool diag = PacingDiagnostics.Enabled;
        bool smooth = ClockTuning.SmoothSync;

        // 生产快路径：两个开关都关时保持原始语义，不做任何额外计算（连 projected 的 QPC 读取都省掉）
        if (!smooth && !diag)
        {
            lock (_lock)
            {
                _basePosition = masterPosition;
                if (_isRunning)
                    _stopwatch.Restart();
            }
            return;
        }

        double driftMs;      // 主时钟与本地推算的原始偏差（= 硬跳模式下的跳变量）
        double appliedMs;    // 实际施加到时钟的位置变化量
        bool hardSnap;

        lock (_lock)
        {
            // 跳变前按当前计时推算出的位置（即"若不同步会读到的值"）
            var projected = _isRunning
                ? _basePosition + TimeSpan.FromSeconds(_stopwatch.Elapsed.TotalSeconds * _speed)
                : _basePosition;

            driftMs = (masterPosition - projected).TotalMilliseconds;

            if (!smooth || Math.Abs(driftMs) > ClockTuning.HardSnapMs)
            {
                // 硬赋值：默认行为，或软同步下的大偏差兜底（seek / 起播 / 严重失步）
                hardSnap = true;
                appliedMs = driftMs;
                _basePosition = masterPosition;
            }
            else
            {
                // 一阶低通 + slew 钳位：突发被吃掉，时钟保持平滑
                hardSnap = false;
                appliedMs = Math.Clamp(
                    driftMs * ClockTuning.SmoothFactor,
                    -ClockTuning.MaxStepMs,
                    ClockTuning.MaxStepMs);
                _basePosition = projected + TimeSpan.FromMilliseconds(appliedMs);
            }

            if (_isRunning)
                _stopwatch.Restart();
        }

        // 记录点放在锁外，避免诊断拖长时钟临界区
        if (diag)
            PacingDiagnostics.Clock.Record(appliedMs, driftMs, hardSnap);
    }
}

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
    public void SyncTo(TimeSpan masterPosition)
    {
        lock (_lock)
        {
            _basePosition = masterPosition;
            if (_isRunning)
                _stopwatch.Restart();
        }
    }
}

using System.Diagnostics;
using LingFan.Media.Abstractions;

namespace MediaSourceProbe;

/// <summary>一次状态跃迁记录（相对探针启动的时刻）。</summary>
internal readonly record struct StateTransition(double AtMs, MediaState From, MediaState To);

/// <summary>定位链路判定结论。用于区分「定位没生效」「定位生效但无帧」「定位后播放器终止」。</summary>
internal enum SeekVerdict
{
    /// <summary>本次运行未请求定位。</summary>
    NotRequested,

    /// <summary>本次跳过定位（源不可定位，线性播放兜底场景）。</summary>
    Skipped,

    /// <summary>位置落在目标附近且定位后有新帧交付。</summary>
    Pass,

    /// <summary>位置未落到目标附近（定位未生效或落点偏差过大）。</summary>
    PositionMismatch,

    /// <summary>位置正确，但定位后没有任何新帧交付（管线停摆）。</summary>
    PositionOkNoFrames,

    /// <summary>定位后播放器转入 Ended/Stopped/Error，且未交付新帧（播放器在定位后终止）。</summary>
    TerminatedAfterSeek,

    /// <summary>定位调用本身抛出异常（如不可定位源）。</summary>
    SeekThrew,
}

/// <summary>
/// 运行期观测量：帧计数、状态跃迁时间线、定位分段数据。
/// </summary>
/// <remarks>
/// <para>线程安全：帧事件在管线线程触发，只做 <see cref="Interlocked"/> 自增与时刻记录；
/// 状态跃迁与错误用短临界区追加，不阻塞管线。</para>
/// <para>定位只记录一次（一次运行最多注入一次定位）。时刻均相对探针启动的毫秒数，
/// 便于与逐行采样表的时间列对照。</para>
/// </remarks>
internal sealed class Observation
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly List<StateTransition> _states = new();
    private readonly List<string> _errors = new();

    private long _videoFrames;
    private long _audioFrames;
    private double _lastVideoFrameMs = -1;
    private double _lastAudioFrameMs = -1;

    private bool _seekIssued;
    private double _seekIssuedAtMs = -1;
    private double _seekReturnedAtMs = -1;
    private TimeSpan _seekTarget;
    private TimeSpan _positionAtIssue;
    private TimeSpan _positionAtReturn;
    private long _videoFramesAtIssue;
    private long _audioFramesAtIssue;
    private double _firstVideoFrameAfterSeekMs = -1;
    private double _firstAudioFrameAfterSeekMs = -1;
    private string? _seekError;
    private bool _terminalAfterSeek;
    private MediaState _terminalAfterSeekState;
    private double _terminalAfterSeekAtMs = -1;
    private bool _endedObserved;
    private double _endedAtMs = -1;

    /// <summary>探针启动至今的毫秒数。</summary>
    public double ElapsedMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>累计交付的视频帧数。</summary>
    public long VideoFrames => Interlocked.Read(ref _videoFrames);

    /// <summary>累计交付的音频帧数。</summary>
    public long AudioFrames => Interlocked.Read(ref _audioFrames);

    /// <summary>最后一次视频帧交付时刻（毫秒）；-1 表示从未交付。</summary>
    public double LastVideoFrameMs { get { lock (_gate) return _lastVideoFrameMs; } }

    /// <summary>最后一次音频帧交付时刻（毫秒）；-1 表示从未交付。</summary>
    public double LastAudioFrameMs { get { lock (_gate) return _lastAudioFrameMs; } }

    /// <summary>是否已发起过定位。</summary>
    public bool SeekIssued { get { lock (_gate) return _seekIssued; } }

    /// <summary>定位发起时刻（毫秒）。</summary>
    public double SeekIssuedAtMs { get { lock (_gate) return _seekIssuedAtMs; } }

    /// <summary>定位返回时刻（毫秒）；-1 表示未返回（抛出或未执行）。</summary>
    public double SeekReturnedAtMs { get { lock (_gate) return _seekReturnedAtMs; } }

    /// <summary>定位目标。</summary>
    public TimeSpan SeekTarget { get { lock (_gate) return _seekTarget; } }

    /// <summary>定位发起时的播放位置。</summary>
    public TimeSpan PositionAtIssue { get { lock (_gate) return _positionAtIssue; } }

    /// <summary>定位返回时的播放位置。</summary>
    public TimeSpan PositionAtReturn { get { lock (_gate) return _positionAtReturn; } }

    /// <summary>定位发起前已交付的视频帧数。</summary>
    public long VideoFramesAtIssue { get { lock (_gate) return _videoFramesAtIssue; } }

    /// <summary>定位发起前已交付的音频帧数。</summary>
    public long AudioFramesAtIssue { get { lock (_gate) return _audioFramesAtIssue; } }

    /// <summary>定位后首个视频帧的时刻（毫秒）；-1 表示定位后没有新视频帧。</summary>
    public double FirstVideoFrameAfterSeekMs { get { lock (_gate) return _firstVideoFrameAfterSeekMs; } }

    /// <summary>定位后首个音频帧的时刻（毫秒）；-1 表示定位后没有新音频帧。</summary>
    public double FirstAudioFrameAfterSeekMs { get { lock (_gate) return _firstAudioFrameAfterSeekMs; } }

    /// <summary>定位调用抛出的异常文本；null 表示未抛出。</summary>
    public string? SeekError { get { lock (_gate) return _seekError; } }

    /// <summary>定位后是否发生过转入 Ended/Stopped/Error 的跃迁。</summary>
    public bool TerminalAfterSeek { get { lock (_gate) return _terminalAfterSeek; } }

    /// <summary>定位后转到的终态；仅在 <see cref="TerminalAfterSeek"/> 为 true 时有意义。</summary>
    public MediaState TerminalAfterSeekState { get { lock (_gate) return _terminalAfterSeekState; } }

    /// <summary>定位后转入终态的时刻（毫秒）。</summary>
    public double TerminalAfterSeekAtMs { get { lock (_gate) return _terminalAfterSeekAtMs; } }

    /// <summary>整段播放中是否出现过 Ended（自然播放完成在公共接口上的可观测表现）。</summary>
    public bool EndedObserved { get { lock (_gate) return _endedObserved; } }

    /// <summary>首次转入 Ended 的时刻（毫秒）；-1 表示未出现。</summary>
    public double EndedAtMs { get { lock (_gate) return _endedAtMs; } }

    /// <summary>定位后交付的视频帧增量。</summary>
    public long VideoFramesAfterSeek => SeekIssued ? VideoFrames - VideoFramesAtIssue : 0;

    /// <summary>定位后交付的音频帧增量。</summary>
    public long AudioFramesAfterSeek => SeekIssued ? AudioFrames - AudioFramesAtIssue : 0;

    /// <summary>状态跃迁时间线快照（按发生顺序）。</summary>
    public IReadOnlyList<StateTransition> StateTimeline
    {
        get { lock (_gate) return _states.ToArray(); }
    }

    /// <summary>错误事件快照。</summary>
    public IReadOnlyList<string> Errors
    {
        get { lock (_gate) return _errors.ToArray(); }
    }

    /// <summary>记录一次视频帧交付（管线线程调用，只做自增与时刻记录）。</summary>
    public void OnVideoFrame()
    {
        Interlocked.Increment(ref _videoFrames);
        lock (_gate)
        {
            _lastVideoFrameMs = _clock.Elapsed.TotalMilliseconds;
            if (_seekIssued && _firstVideoFrameAfterSeekMs < 0)
                _firstVideoFrameAfterSeekMs = _lastVideoFrameMs;
        }
    }

    /// <summary>记录一次音频帧交付（管线线程调用）。</summary>
    public void OnAudioFrame()
    {
        Interlocked.Increment(ref _audioFrames);
        lock (_gate)
        {
            _lastAudioFrameMs = _clock.Elapsed.TotalMilliseconds;
            if (_seekIssued && _firstAudioFrameAfterSeekMs < 0)
                _firstAudioFrameAfterSeekMs = _lastAudioFrameMs;
        }
    }

    /// <summary>记录一次状态跃迁，并标记 Ended 首次出现与定位后的终态。</summary>
    public void OnStateChanged(MediaState from, MediaState to)
    {
        lock (_gate)
        {
            double atMs = _clock.Elapsed.TotalMilliseconds;
            _states.Add(new StateTransition(atMs, from, to));

            // 自然播放完成（PlaybackCompleted）在公共接口上只表现为转入 Ended，记录首次时刻供汇总判读。
            if (!_endedObserved && to == MediaState.Ended)
            {
                _endedObserved = true;
                _endedAtMs = atMs;
            }

            // 定位后转入 Ended/Stopped/Error 是「播放器已死」的判据，必须与「定位后无帧」区分开。
            if (_seekIssued && !_terminalAfterSeek && to is MediaState.Ended or MediaState.Stopped or MediaState.Error)
            {
                _terminalAfterSeek = true;
                _terminalAfterSeekState = to;
                _terminalAfterSeekAtMs = atMs;
            }
        }
    }

    /// <summary>记录一次错误事件。</summary>
    public void OnError(MediaErrorEventArgs e)
    {
        lock (_gate)
        {
            _errors.Add($"{e.ErrorCode}: {e.Message}{(e.Exception is null ? "" : $"（{e.Exception.GetType().Name}）")}");
        }
    }

    /// <summary>定位发起前调用：记下目标、位置与当前帧基线。</summary>
    public void MarkSeekIssued(TimeSpan target, TimeSpan position)
    {
        lock (_gate)
        {
            _seekIssued = true;
            _seekIssuedAtMs = _clock.Elapsed.TotalMilliseconds;
            _seekTarget = target;
            _positionAtIssue = position;
            _videoFramesAtIssue = Interlocked.Read(ref _videoFrames);
            _audioFramesAtIssue = Interlocked.Read(ref _audioFrames);
        }
    }

    /// <summary>定位返回后调用：记下返回时刻与位置。</summary>
    public void MarkSeekReturned(TimeSpan position)
    {
        lock (_gate)
        {
            _seekReturnedAtMs = _clock.Elapsed.TotalMilliseconds;
            _positionAtReturn = position;
        }
    }

    /// <summary>定位抛出异常时调用。</summary>
    public void MarkSeekFailed(string message)
    {
        lock (_gate)
        {
            _seekReturnedAtMs = _clock.Elapsed.TotalMilliseconds;
            _seekError = message;
        }
    }
}

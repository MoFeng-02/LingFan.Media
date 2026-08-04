namespace LingFan.Media.Core;

/// <summary>
/// 播放流程状态机控制器。管理 Open → Buffer → Play → Pause → Stop 状态转换，
/// 确保状态转换的合法性和原子性。
/// </summary>
/// <remarks>
/// <para>线程安全：状态转换通过 <c>lock</c> 保证原子性。</para>
/// <para>管线线程可能触发自然结束状态转换，与 UI 线程的 Play/Pause/Stop 并发。</para>
/// </remarks>
public sealed class PlaybackController
{
    private readonly object _lock = new();
    private MediaState _currentState = MediaState.Idle;

    /// <summary>
    /// 初始化 <see cref="PlaybackController"/> 的新实例。
    /// </summary>
    public PlaybackController()
    {
    }

    /// <summary>当前状态。</summary>
    public MediaState CurrentState
    {
        get { lock (_lock) return _currentState; }
    }

    /// <summary>是否可进入播放（State == Idle/Paused/Ended）。</summary>
    /// <remarks>MediaState 枚举无 Ready 值，缓冲完成后回到 Idle 状态。</remarks>
    public bool CanPlay
    {
        get
        {
            lock (_lock)
            {
                return _currentState is MediaState.Idle
                    or MediaState.Paused
                    or MediaState.Ended;
            }
        }
    }

    /// <summary>是否可定位（非直播流且非 Opening/Error 状态）。</summary>
    public bool CanSeek
    {
        get
        {
            lock (_lock)
            {
                return _currentState is not MediaState.Opening
                    and not MediaState.Error;
            }
        }
    }

    /// <summary>
    /// 状态转换，校验合法性后执行。
    /// </summary>
    /// <param name="newState">目标状态。</param>
    /// <returns>转换是否成功（非法转换返回 false）。</returns>
    public bool TransitionTo(MediaState newState)
    {
        lock (_lock)
        {
            if (!IsValidTransition(_currentState, newState))
                return false;

            _currentState = newState;
            return true;
        }
    }

    /// <summary>
    /// 错误处理，决定是否可恢复。
    /// </summary>
    /// <param name="e">错误事件参数。</param>
    public void OnError(MediaErrorEventArgs e)
    {
        lock (_lock)
        {
            _currentState = MediaState.Error;
        }
    }

    /// <summary>
    /// 重置到 Idle 状态（从 Error/Stopped 恢复）。
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _currentState = MediaState.Idle;
        }
    }

    private static bool IsValidTransition(MediaState from, MediaState to)
    {
        // 任何状态 → Error
        if (to == MediaState.Error)
            return true;

        // Error → Idle
        if (from == MediaState.Error && to == MediaState.Idle)
            return true;

        return (from, to) switch
        {
            // Idle → Opening
            (MediaState.Idle, MediaState.Opening) => true,

            // Opening → Buffering / Error
            (MediaState.Opening, MediaState.Buffering) => true,

            // Buffering → Idle (缓冲完成，就绪)
            (MediaState.Buffering, MediaState.Idle) => true,

            // Idle → Playing (从就绪状态播放)
            (MediaState.Idle, MediaState.Playing) => true,

            // Playing → Paused / Stopped / Ended
            (MediaState.Playing, MediaState.Paused) => true,
            (MediaState.Playing, MediaState.Stopped) => true,
            (MediaState.Playing, MediaState.Ended) => true,

            // Paused → Playing / Stopped / Ended
            (MediaState.Paused, MediaState.Playing) => true,
            (MediaState.Paused, MediaState.Stopped) => true,
            // 末尾恰好暂停时流已排干，自然完成仍应登记为 Ended（不被卡在 Paused）
            (MediaState.Paused, MediaState.Ended) => true,

            // Ended → Playing (重新播放) / Stopped (结束后停止，合法)
            (MediaState.Ended, MediaState.Playing) => true,
            (MediaState.Ended, MediaState.Stopped) => true,

            // Stopped → Idle (重置)
            (MediaState.Stopped, MediaState.Idle) => true,

            // 同状态（幂等）
            _ when from == to => true,

            _ => false
        };
    }
}

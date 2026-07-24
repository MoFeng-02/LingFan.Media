namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体播放状态。
/// </summary>
public enum MediaState : int
{
    /// <summary>空闲，尚未打开媒体源。</summary>
    Idle,
    /// <summary>正在打开媒体源。</summary>
    Opening,
    /// <summary>正在缓冲，等待数据就绪。</summary>
    Buffering,
    /// <summary>正在播放。</summary>
    Playing,
    /// <summary>已暂停。</summary>
    Paused,
    /// <summary>已停止，可重新打开。</summary>
    Stopped,
    /// <summary>自然播放结束。</summary>
    Ended,
    /// <summary>错误状态。</summary>
    Error
}

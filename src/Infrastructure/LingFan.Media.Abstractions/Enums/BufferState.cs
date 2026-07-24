namespace LingFan.Media.Abstractions;

/// <summary>
/// 缓冲状态。
/// </summary>
public enum BufferState : int
{
    /// <summary>空，尚未开始缓冲。</summary>
    Empty,
    /// <summary>正在缓冲。</summary>
    Buffering,
    /// <summary>缓冲就绪，可开始播放。</summary>
    Ready,
    /// <summary>缓冲不足，需要重新缓冲。</summary>
    Starved
}

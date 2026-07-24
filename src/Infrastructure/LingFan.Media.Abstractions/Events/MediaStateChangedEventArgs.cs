namespace LingFan.Media.Abstractions;

/// <summary>
/// 播放状态变更事件参数。
/// </summary>
public sealed class MediaStateChangedEventArgs : EventArgs
{
    /// <summary>变更前的状态。</summary>
    public MediaState OldState { get; }

    /// <summary>变更后的状态。</summary>
    public MediaState NewState { get; }

    /// <summary>
    /// 初始化 <see cref="MediaStateChangedEventArgs"/> 的新实例。
    /// </summary>
    /// <param name="oldState">变更前的状态。</param>
    /// <param name="newState">变更后的状态。</param>
    public MediaStateChangedEventArgs(MediaState oldState, MediaState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}

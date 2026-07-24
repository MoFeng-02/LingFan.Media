namespace LingFan.Media.Abstractions;

/// <summary>
/// 轨道切换事件参数。
/// </summary>
public sealed class TrackChangedEventArgs : EventArgs
{
    /// <summary>轨道类型。</summary>
    public TrackType TrackType { get; }

    /// <summary>旧轨道（可能为 null）。</summary>
    public MediaTrack? OldTrack { get; }

    /// <summary>新轨道（可能为 null）。</summary>
    public MediaTrack? NewTrack { get; }

    /// <summary>
    /// 初始化 <see cref="TrackChangedEventArgs"/> 的新实例。
    /// </summary>
    public TrackChangedEventArgs(TrackType trackType, MediaTrack? oldTrack, MediaTrack? newTrack)
    {
        TrackType = trackType;
        OldTrack = oldTrack;
        NewTrack = newTrack;
    }
}

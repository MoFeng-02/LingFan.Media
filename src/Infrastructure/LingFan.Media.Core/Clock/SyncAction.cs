namespace LingFan.Media.Core;

/// <summary>
/// 同步动作。视频帧同步检查的决策结果。
/// </summary>
public enum SyncAction : int
{
    /// <summary>立即呈现。</summary>
    Present,

    /// <summary>等待（视频超前时钟）。</summary>
    Wait,

    /// <summary>丢弃（视频严重落后）。</summary>
    Drop
}

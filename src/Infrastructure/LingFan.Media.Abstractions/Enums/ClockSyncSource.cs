namespace LingFan.Media.Abstractions;

/// <summary>
/// 时钟同步源。
/// </summary>
public enum ClockSyncSource : int
{
    /// <summary>音频时钟为主（IAudioOutput.PlaybackPosition 驱动）。</summary>
    Audio,
    /// <summary>视频时钟为主（VideoFrame.Timestamp 驱动）。</summary>
    Video,
    /// <summary>系统时钟为主（Stopwatch 驱动，无声时降级）。</summary>
    System
}

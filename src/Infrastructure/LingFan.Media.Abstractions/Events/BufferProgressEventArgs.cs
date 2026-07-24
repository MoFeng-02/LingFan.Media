namespace LingFan.Media.Abstractions;

/// <summary>
/// 缓冲进度事件参数。
/// </summary>
public sealed class BufferProgressEventArgs : EventArgs
{
    /// <summary>当前缓冲状态。</summary>
    public BufferState State { get; }

    /// <summary>已缓冲时长。</summary>
    public TimeSpan BufferedDuration { get; }

    /// <summary>已缓冲字节数。</summary>
    public long BufferedBytes { get; }

    /// <summary>缓冲进度（0.0~1.0，仅网络流有意义）。</summary>
    public float Progress { get; }

    /// <summary>
    /// 初始化 <see cref="BufferProgressEventArgs"/> 的新实例。
    /// </summary>
    public BufferProgressEventArgs(BufferState state, TimeSpan bufferedDuration, long bufferedBytes, float progress)
    {
        State = state;
        BufferedDuration = bufferedDuration;
        BufferedBytes = bufferedBytes;
        Progress = progress;
    }
}

namespace LingFan.Media.Avalonia;

/// <summary>
/// 拖拽 Seek 事件参数。由 <see cref="ProgressBar"/> 在拖拽完成时触发。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：config 分类——纯数据载体，无 I/O。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class SeekEventArgs : EventArgs
{
    /// <summary>进度 (0.0~1.0)。</summary>
    public double Progress { get; }

    /// <summary>计算后的时间位置（Progress × Duration）。</summary>
    public TimeSpan Position { get; }

    /// <summary>
    /// 创建 SeekEventArgs。
    /// </summary>
    /// <param name="progress">进度 (0.0~1.0)。</param>
    /// <param name="position">计算后的时间位置。</param>
    public SeekEventArgs(double progress, TimeSpan position)
    {
        Progress = progress;
        Position = position;
    }
}

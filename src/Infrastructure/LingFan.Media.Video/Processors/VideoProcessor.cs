namespace LingFan.Media.Video;

/// <summary>
/// 视频后处理链。按顺序依次执行所有已注册的 <see cref="IVideoProcessor"/>。
/// </summary>
/// <remarks>
/// <para><b>所有权转移语义</b>：对每个处理器依次调用 <see cref="IVideoProcessor.Process"/>，
/// 前一个处理器的输出帧作为下一个处理器的输入帧。
/// 每个处理器负责 Dispose 输入帧并返回新帧。</para>
/// <para>当处理器 <see cref="IVideoProcessor.IsEnabled"/> 为 false 时，该处理器透传（跳过处理）。</para>
/// <para>非线程安全（处理器链在播放启动前配置，运行时不可修改）。</para>
/// </remarks>
public sealed class VideoProcessor
{
    private readonly List<IVideoProcessor> _processors = [];

    /// <summary>处理器链（只读视图）。</summary>
    public IReadOnlyList<IVideoProcessor> Processors => _processors;

    /// <summary>
    /// 添加处理器到链末尾。
    /// </summary>
    /// <param name="processor">视频处理器。</param>
    /// <exception cref="ArgumentNullException">processor 为 null。</exception>
    public void AddProcessor(IVideoProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _processors.Add(processor);
    }

    /// <summary>
    /// 从链中移除处理器。
    /// </summary>
    /// <param name="processor">要移除的处理器。</param>
    public void RemoveProcessor(IVideoProcessor processor)
    {
        _processors.Remove(processor);
    }

    /// <summary>
    /// 依次执行所有处理器。
    /// </summary>
    /// <param name="frame">输入帧。</param>
    /// <returns>处理后的帧。</returns>
    /// <remarks>
    /// 对每个处理器依次调用 <see cref="IVideoProcessor.Process"/>，
    /// 输入帧被 Dispose，返回新帧传入下一个处理器。
    /// 禁用的处理器透传输入帧。
    /// </remarks>
    public VideoFrame? Process(VideoFrame frame)
    {
        VideoFrame? current = frame;
        foreach (var processor in _processors)
        {
            current = processor.Process(current);
            if (current is null) return null; // 处理器丢弃帧（已 Dispose 输入帧）
        }
        return current;
    }

    /// <summary>
    /// 重置整条处理器链（Seek/Flush 后调用）。
    /// </summary>
    /// <remarks>依次调用每个处理器的 <see cref="IVideoProcessor.Reset"/>。</remarks>
    public void Reset()
    {
        foreach (var processor in _processors)
            processor?.Reset();
    }
}

namespace LingFan.Media.Video;

/// <summary>
/// 视频统计信息。线程安全，使用 <see cref="Interlocked"/> 更新。
/// </summary>
/// <remarks>
/// <para>统计值在运行时不断更新，通过 <see cref="Interlocked"/> 保证线程安全。</para>
/// <para>统计场景：</para>
/// <list type="bullet">
/// <item><see cref="RecordDecoded"/> — 每帧解码完成后调用</item>
/// <item><see cref="RecordDropped"/> — 丢帧时调用（同步器判定 Drop）</item>
/// <item><see cref="RecordRendered"/> — 每帧渲染完成后调用</item>
/// <item><see cref="UpdateFrameRate"/> — 定期更新实际帧率</item>
/// </list>
/// </remarks>
public sealed class VideoStats
{
    private long _decodedFrames;
    private long _droppedFrames;
    private long _renderedFrames;
    private long _totalDecodeTimeTicks;
    private long _totalRenderTimeTicks;
    private long _currentFrameRateTimes100;

    /// <summary>累计解码帧数。</summary>
    public long DecodedFrames => Interlocked.Read(ref _decodedFrames);

    /// <summary>累计丢帧数。</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>累计渲染帧数。</summary>
    public long RenderedFrames => Interlocked.Read(ref _renderedFrames);

    /// <summary>平均单帧解码耗时。</summary>
    public TimeSpan AverageDecodeTime
    {
        get
        {
            var count = Interlocked.Read(ref _decodedFrames);
            if (count == 0)
                return TimeSpan.Zero;
            var total = Interlocked.Read(ref _totalDecodeTimeTicks);
            return TimeSpan.FromTicks(total / count);
        }
    }

    /// <summary>平均单帧渲染耗时。</summary>
    public TimeSpan AverageRenderTime
    {
        get
        {
            var count = Interlocked.Read(ref _renderedFrames);
            if (count == 0)
                return TimeSpan.Zero;
            var total = Interlocked.Read(ref _totalRenderTimeTicks);
            return TimeSpan.FromTicks(total / count);
        }
    }

    /// <summary>实际渲染帧率（FPS）。</summary>
    public float CurrentFrameRate
    {
        get => Interlocked.Read(ref _currentFrameRateTimes100) / 100f;
    }

    /// <summary>
    /// 记录一帧解码完成。
    /// </summary>
    /// <param name="decodeTime">单帧解码耗时。</param>
    public void RecordDecoded(TimeSpan decodeTime)
    {
        Interlocked.Increment(ref _decodedFrames);
        Interlocked.Add(ref _totalDecodeTimeTicks, decodeTime.Ticks);
    }

    /// <summary>
    /// 记录一帧丢弃。
    /// </summary>
    public void RecordDropped()
    {
        Interlocked.Increment(ref _droppedFrames);
    }

    /// <summary>
    /// 记录一帧渲染完成。
    /// </summary>
    /// <param name="renderTime">单帧渲染耗时。</param>
    public void RecordRendered(TimeSpan renderTime)
    {
        Interlocked.Increment(ref _renderedFrames);
        Interlocked.Add(ref _totalRenderTimeTicks, renderTime.Ticks);
    }

    /// <summary>
    /// 更新实际渲染帧率。
    /// </summary>
    /// <param name="fps">当前帧率（FPS）。</param>
    public void UpdateFrameRate(float fps)
    {
        Interlocked.Exchange(ref _currentFrameRateTimes100, (long)(fps * 100));
    }

    /// <summary>
    /// 重置所有统计值。
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _decodedFrames, 0);
        Interlocked.Exchange(ref _droppedFrames, 0);
        Interlocked.Exchange(ref _renderedFrames, 0);
        Interlocked.Exchange(ref _totalDecodeTimeTicks, 0);
        Interlocked.Exchange(ref _totalRenderTimeTicks, 0);
        Interlocked.Exchange(ref _currentFrameRateTimes100, 0);
    }
}

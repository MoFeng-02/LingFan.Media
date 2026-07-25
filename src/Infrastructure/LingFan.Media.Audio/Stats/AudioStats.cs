namespace LingFan.Media.Audio;

/// <summary>
/// 音频统计信息。线程安全，使用 <see cref="Interlocked"/> 更新。
/// </summary>
/// <remarks>
/// <para>统计值在运行时不断更新，通过 <see cref="Interlocked"/> 保证线程安全。</para>
/// <para>统计场景：</para>
/// <list type="bullet">
/// <item><see cref="RecordDecoded"/> — 每帧解码完成后调用</item>
/// <item><see cref="RecordSubmitted"/> — 每帧提交到输出后调用</item>
/// <item><see cref="RecordDropped"/> — 丢帧时调用（缓冲满或同步器判定 Drop）</item>
/// <item><see cref="RecordBufferUnderrun"/> — 缓冲不足时调用</item>
/// <item><see cref="UpdateOutputLatency"/> — 定期更新输出延迟</item>
/// </list>
/// </remarks>
public sealed class AudioStats
{
    private long _decodedFrames;
    private long _submittedFrames;
    private long _droppedFrames;
    private long _totalDecodeTimeTicks;
    private long _outputLatencyTicks;
    private int _bufferUnderrunCount;

    /// <summary>累计解码帧数。</summary>
    public long DecodedFrames => Interlocked.Read(ref _decodedFrames);

    /// <summary>累计提交到输出的帧数。</summary>
    public long SubmittedFrames => Interlocked.Read(ref _submittedFrames);

    /// <summary>累计丢弃帧数。</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

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

    /// <summary>当前输出延迟。</summary>
    public TimeSpan OutputLatency
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _outputLatencyTicks));
    }

    /// <summary>缓冲不足次数。</summary>
    public int BufferUnderrunCount => Volatile.Read(ref _bufferUnderrunCount);

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
    /// 记录一帧提交到输出。
    /// </summary>
    public void RecordSubmitted()
    {
        Interlocked.Increment(ref _submittedFrames);
    }

    /// <summary>
    /// 记录一帧丢弃。
    /// </summary>
    public void RecordDropped()
    {
        Interlocked.Increment(ref _droppedFrames);
    }

    /// <summary>
    /// 记录一次缓冲不足。
    /// </summary>
    public void RecordBufferUnderrun()
    {
        Interlocked.Increment(ref _bufferUnderrunCount);
    }

    /// <summary>
    /// 更新当前输出延迟。
    /// </summary>
    /// <param name="latency">输出延迟。</param>
    public void UpdateOutputLatency(TimeSpan latency)
    {
        Interlocked.Exchange(ref _outputLatencyTicks, latency.Ticks);
    }

    /// <summary>
    /// 重置所有统计值。
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _decodedFrames, 0);
        Interlocked.Exchange(ref _submittedFrames, 0);
        Interlocked.Exchange(ref _droppedFrames, 0);
        Interlocked.Exchange(ref _totalDecodeTimeTicks, 0);
        Interlocked.Exchange(ref _outputLatencyTicks, 0);
        Interlocked.Exchange(ref _bufferUnderrunCount, 0);
    }
}

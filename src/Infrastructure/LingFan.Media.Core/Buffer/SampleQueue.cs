using System.Threading.Channels;

namespace LingFan.Media.Core;

/// <summary>
/// 音频帧队列。Decoder → AudioOutput 之间的有界缓冲区，持有帧的所有权。
/// </summary>
/// <remarks>
/// <para>与 <see cref="FrameQueue"/> 类似，但存放 <see cref="AudioFrame"/>。</para>
/// <para>关键区别：音频不丢帧（丢帧会导致声音断裂）。</para>
/// <para>线程安全：使用 <see cref="System.Threading.Channels.Channel{T}"/> 实现。</para>
/// </remarks>
public sealed class SampleQueue
{
    private Channel<AudioFrame> _channel;
    private readonly int _capacity;
    private readonly TimeSpan _maxDuration;
    private readonly long _maxBytes;

    private long _totalBytes;
    private TimeSpan _firstTimestamp;
    private TimeSpan _lastTimestamp;
    private bool _hasTimestamps;

    /// <summary>
    /// 初始化 <see cref="SampleQueue"/> 的新实例。
    /// </summary>
    /// <param name="capacity">最大容量（帧数），默认 60（音频帧更小更频繁）。</param>
    /// <param name="maxDuration">最大缓冲时长，默认 3 秒。</param>
    /// <param name="maxBytes">最大缓冲字节数，默认 100MB。</param>
    public SampleQueue(
        int capacity = 60,
        TimeSpan? maxDuration = null,
        long? maxBytes = null)
    {
        _capacity = capacity;
        _maxDuration = maxDuration ?? TimeSpan.FromSeconds(3);
        _maxBytes = maxBytes ?? 100 * 1024 * 1024L;

        _channel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>当前队列长度。</summary>
    public int Count => _channel.Reader.Count;

    /// <summary>最大容量（帧数）。</summary>
    public int Capacity => _capacity;

    /// <summary>最大缓冲时长。</summary>
    public TimeSpan MaxDuration => _maxDuration;

    /// <summary>最大缓冲字节数。</summary>
    public long MaxBytes => _maxBytes;

    /// <summary>
    /// 入队（满时异步等待）。
    /// 所有权从生产者转移到队列。
    /// </summary>
    public async ValueTask EnqueueAsync(AudioFrame frame, CancellationToken ct = default)
    {
        UpdateStatsOnEnqueue(frame);
        await _channel.Writer.WriteAsync(frame, ct);
    }

    /// <summary>
    /// 尝试入队（非阻塞，满时返回 false）。
    /// </summary>
    public bool TryEnqueue(AudioFrame frame)
    {
        if (IsFull())
            return false;

        if (_channel.Writer.TryWrite(frame))
        {
            UpdateStatsOnEnqueue(frame);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 异步出队（空时异步等待）。
    /// 所有权从队列转移到消费者。
    /// </summary>
    public async ValueTask<AudioFrame> DequeueAsync(CancellationToken ct = default)
    {
        var frame = await _channel.Reader.ReadAsync(ct);
        UpdateStatsOnDequeue(frame);
        return frame;
    }

    /// <summary>
    /// 尝试出队（非阻塞）。
    /// </summary>
    public bool TryDequeue(out AudioFrame? frame)
    {
        if (_channel.Reader.TryRead(out var f))
        {
            UpdateStatsOnDequeue(f);
            frame = f;
            return true;
        }

        frame = null;
        return false;
    }

    /// <summary>
    /// 查看队首帧（不移除）。无帧时返回 null。
    /// </summary>
    public AudioFrame? Peek()
    {
        return _channel.Reader.TryPeek(out var frame) ? frame : null;
    }

    /// <summary>
    /// 清空队列并 Dispose 所有帧。
    /// </summary>
    /// <param name="pool">帧对象池（V2，可为 null = Dispose 帧而非归还到池）。</param>
    public void Clear(IFramePool<AudioFrame>? pool = null)
    {
        while (_channel.Reader.TryRead(out var frame))
        {
            if (pool != null)
                pool.Return(frame);
            else
                frame.Dispose();
        }

        _totalBytes = 0;
        _firstTimestamp = TimeSpan.Zero;
        _lastTimestamp = TimeSpan.Zero;
        _hasTimestamps = false;
    }

    /// <summary>
    /// 标记队列完成（流结束），解除所有阻塞。
    /// </summary>
    public void Complete()
    {
        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// 重置队列（V2-06 C3）。将已 Complete 的队列恢复为可写入状态。
    /// 用于 Seek after stream end 场景。
    /// </summary>
    /// <remarks>清空并释放所有残留帧（不持有帧对象池引用，直接 Dispose）。纯内存同步操作。</remarks>
    public void Reset()
    {
        while (_channel.Reader.TryRead(out var frame))
            frame.Dispose();

        _channel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _totalBytes = 0;
        _firstTimestamp = TimeSpan.Zero;
        _lastTimestamp = TimeSpan.Zero;
        _hasTimestamps = false;
    }

    private bool IsFull()
    {
        if (Count >= _capacity)
            return true;

        if (_hasTimestamps && (_lastTimestamp - _firstTimestamp) >= _maxDuration)
            return true;

        if (_totalBytes >= _maxBytes)
            return true;

        return false;
    }

    private void UpdateStatsOnEnqueue(AudioFrame frame)
    {
        _totalBytes += frame.Data.Length;

        if (!_hasTimestamps)
        {
            _firstTimestamp = frame.Timestamp;
            _hasTimestamps = true;
        }

        _lastTimestamp = frame.Timestamp;
    }

    private void UpdateStatsOnDequeue(AudioFrame frame)
    {
        _totalBytes -= frame.Data.Length;
        if (_totalBytes < 0) _totalBytes = 0;

        if (_channel.Reader.TryPeek(out var next))
        {
            _firstTimestamp = next.Timestamp;
        }
        else
        {
            _hasTimestamps = false;
        }
    }
}

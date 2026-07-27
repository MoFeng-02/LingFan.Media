using System.Threading.Channels;

namespace LingFan.Media.Core;

/// <summary>
/// 视频帧队列。Decoder → Renderer 之间的有界缓冲区，持有帧的所有权。
/// </summary>
/// <remarks>
/// <para>线程安全：使用 <see cref="System.Threading.Channels.Channel{T}"/> 实现。</para>
/// <para>所有权转移：Enqueue 时所有权从生产者转移到队列，Dequeue 时从队列转移到消费者。</para>
/// <para>Clear 时 Dispose 所有帧，防止 GPU 资源泄漏。</para>
/// <para>三重限制：Capacity（帧数）+ MaxDuration（时长）+ MaxBytes（字节）。</para>
/// </remarks>
public sealed class FrameQueue
{
    private Channel<VideoFrame> _channel;
    private readonly int _capacity;
    private readonly TimeSpan _maxDuration;
    private readonly long _maxBytes;

    private long _totalBytes;
    private TimeSpan _firstTimestamp;
    private TimeSpan _lastTimestamp;
    private bool _hasTimestamps;

    /// <summary>
    /// 初始化 <see cref="FrameQueue"/> 的新实例。
    /// </summary>
    /// <param name="capacity">最大容量（帧数），默认 30。</param>
    /// <param name="maxDuration">最大缓冲时长，默认 5 秒。</param>
    /// <param name="maxBytes">最大缓冲字节数，默认 500MB。</param>
    public FrameQueue(
        int capacity = 30,
        TimeSpan? maxDuration = null,
        long? maxBytes = null)
    {
        _capacity = capacity;
        _maxDuration = maxDuration ?? TimeSpan.FromSeconds(5);
        _maxBytes = maxBytes ?? 500 * 1024 * 1024L;

        _channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(capacity)
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
    public async ValueTask EnqueueAsync(VideoFrame frame, CancellationToken ct = default)
    {
        UpdateStatsOnEnqueue(frame);
        await _channel.Writer.WriteAsync(frame, ct);
    }

    /// <summary>
    /// 尝试入队（非阻塞，满时返回 false）。
    /// 所有权转移（入队成功时）。
    /// </summary>
    public bool TryEnqueue(VideoFrame frame)
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
    public async ValueTask<VideoFrame> DequeueAsync(CancellationToken ct = default)
    {
        var frame = await _channel.Reader.ReadAsync(ct);
        UpdateStatsOnDequeue(frame);
        return frame;
    }

    /// <summary>
    /// 尝试出队（非阻塞）。
    /// 所有权转移（出队成功时）。
    /// </summary>
    public bool TryDequeue(out VideoFrame? frame)
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
    public VideoFrame? Peek()
    {
        return _channel.Reader.TryPeek(out var frame) ? frame : null;
    }

    /// <summary>
    /// 清空队列并 Dispose 所有帧。
    /// </summary>
    /// <param name="pool">帧对象池（V2，可为 null = Dispose 帧而非归还到池）。</param>
    public void Clear(IFramePool<VideoFrame>? pool = null)
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
    /// 用于 Seek after stream end 场景：流结束后队列完成，重置以重新填充。
    /// </summary>
    /// <remarks>清空并释放所有残留帧（本队列不持有帧对象池引用，直接 Dispose）。纯内存同步操作。</remarks>
    public void Reset()
    {
        // 丢弃并释放所有残留帧（队列可能已 Complete）
        while (_channel.Reader.TryRead(out var frame))
            frame.Dispose();

        // 重建有界通道，恢复可写状态
        _channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(_capacity)
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

    private void UpdateStatsOnEnqueue(VideoFrame frame)
    {
        _totalBytes += frame.Width * frame.Height * 4L; // 近似字节数

        if (!_hasTimestamps)
        {
            _firstTimestamp = frame.Timestamp;
            _hasTimestamps = true;
        }

        _lastTimestamp = frame.Timestamp;
    }

    private void UpdateStatsOnDequeue(VideoFrame frame)
    {
        _totalBytes -= frame.Width * frame.Height * 4L;
        if (_totalBytes < 0) _totalBytes = 0;

        // 更新首帧时间戳
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

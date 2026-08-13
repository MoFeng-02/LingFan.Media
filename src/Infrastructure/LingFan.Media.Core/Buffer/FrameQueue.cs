using System.Threading.Channels;

namespace LingFan.Media.Core;

/// <summary>
/// 视频帧队列。Decoder → Renderer 之间的有界缓冲区，持有帧的所有权。
/// </summary>
/// <remarks>
/// <para>线程安全：使用 <see cref="System.Threading.Channels.Channel{T}"/> 实现。</para>
/// <para>所有权转移：Enqueue 时所有权从生产者转移到队列，Dequeue 时从队列转移到消费者。</para>
/// <para>Clear 时 Dispose 所有帧，防止 GPU 资源泄漏。</para>
/// <para>三重限制：Capacity（帧数）+ MaxDuration（时长）+ MaxBytes（字节，按真实像素格式计费）。</para>
/// <para>headless 优先默认：内存有界、可预测（见 <see cref="_defaultCapacity"/> 等）。有头播放如需更大前向缓冲，构造时显式传参覆盖。</para>
/// </remarks>
public sealed class FrameQueue
{
    // headless 优先默认：内存有界。4K 软解单帧（YUV420P）≈12MB、BGRA32≈33MB，
    // 故 Capacity 决定 in-flight 上限、MaxBytes 作为大帧安全网。
    private const int _defaultCapacity = 8;                 // ≥ VideoPipeline.TargetDepth(6)，前向缓冲可维持
    private static readonly TimeSpan _defaultMaxDuration = TimeSpan.FromSeconds(2);
    private const long _defaultMaxBytes = 96 * 1024 * 1024L; // 8 × 4K-YUV(≈12MB)，覆盖 Capacity；4K-BGRA 经此限到 ~2 帧

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
    /// <param name="capacity">最大容量（帧数），默认 <see cref="_defaultCapacity"/>（headless 优先，8 帧）。</param>
    /// <param name="maxDuration">最大缓冲时长，默认 2 秒。</param>
    /// <param name="maxBytes">最大缓冲字节数（按真实像素格式计费），默认 96MB。</param>
    public FrameQueue(
        int capacity = _defaultCapacity,
        TimeSpan? maxDuration = null,
        long? maxBytes = null)
    {
        _capacity = capacity;
        _maxDuration = maxDuration ?? _defaultMaxDuration;
        _maxBytes = maxBytes ?? _defaultMaxBytes;

        _channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>当前队列长度。</summary>
    public int Count => _channel.Reader.Count;

    /// <summary>
    /// 队列是否已完成（生产者已调用 <see cref="Complete"/> 且所有帧已排空）。
    /// 供消费者判断「生产者结束 + 队列空」以收尾。
    /// </summary>
    public bool IsCompleted => _channel.Reader.Completion.IsCompleted;

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
    /// <param name="pool">帧对象池（可为 null = Dispose 帧而非归还到池）。</param>
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
    /// 重置队列。将已 Complete 的队列恢复为可写入状态。
    /// 用于 Seek after stream end / 重播（Ended→Playing）场景：流结束后队列完成，重置以重新填充。
    /// </summary>
    /// <param name="pool">帧对象池（可为 null = Dispose 残留帧而非归还）。</param>
    /// <remarks>
    /// 必须 Pool-aware：残留帧归还到池而非 Dispose，否则重播/多次 Seek 会持续消耗池容量直至饿死
    /// （帧池 maxSize=16，多次 Reset 直接 Dispose 会令后续解码无帧可取）。
    /// </remarks>
    public void Reset(IFramePool<VideoFrame>? pool = null)
    {
        // 丢弃并归还所有残留帧（队列可能已 Complete）
        while (_channel.Reader.TryRead(out var frame))
        {
            if (pool != null)
                pool.Return(frame);
            else
                frame.Dispose();
        }

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

    /// <summary>
    /// 按真实像素格式估算单帧字节数（用于 <see cref="_totalBytes"/> 账簿，使 <see cref="_maxBytes"/> 表示真实内存）。
    /// YUV420P/NV12/NV21 为 1.5 字节/像素，P010/YUV420P10 为 3 字节/像素（10-bit），其余取 4 字节/像素近似。
    /// </summary>
    private static long FrameByteSize(LingFan.Media.Abstractions.PixelFormat format, int width, int height)
    {
        long pixels = (long)width * height;
        return format switch
        {
            LingFan.Media.Abstractions.PixelFormat.BGRA32 or LingFan.Media.Abstractions.PixelFormat.RGBA32 => pixels * 4,
            LingFan.Media.Abstractions.PixelFormat.RGB24 => pixels * 3,
            LingFan.Media.Abstractions.PixelFormat.YUV420P or LingFan.Media.Abstractions.PixelFormat.NV12 or LingFan.Media.Abstractions.PixelFormat.NV21 => pixels * 3 / 2,
            LingFan.Media.Abstractions.PixelFormat.P010 or LingFan.Media.Abstractions.PixelFormat.YUV420P10 => pixels * 3,
            _ => pixels * 4
        };
    }

    private void UpdateStatsOnEnqueue(VideoFrame frame)
    {
        _totalBytes += FrameByteSize(frame.Format, frame.Width, frame.Height);

        if (!_hasTimestamps)
        {
            _firstTimestamp = frame.Timestamp;
            _hasTimestamps = true;
        }

        _lastTimestamp = frame.Timestamp;
    }

    private void UpdateStatsOnDequeue(VideoFrame frame)
    {
        _totalBytes -= FrameByteSize(frame.Format, frame.Width, frame.Height);
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

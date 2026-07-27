using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

/// <summary>
/// 缓冲管理器实现。管理 Demuxer 到 Decoder 之间的数据包缓冲，
/// 监控缓冲状态，触发 Buffering/Ready 状态转换。
/// </summary>
/// <remarks>
/// <para>线程安全：预读取线程与监控线程并发，使用 <see cref="System.Threading.Channels.Channel{T}"/>。</para>
/// <para>FrameBuffer 无 SubtitleFrameQueue——字幕帧仅含文本和时间戳，</para>
/// <para>无 GPU 资源、无需 Dispose。字幕帧由 SubtitleProcessor 内部缓存管理。</para>
/// </remarks>
public sealed class BufferManager : IBufferManager
{
    private readonly IMediaDemuxer _demuxer;
    private readonly ILogger<BufferManager> _logger;
    private Channel<MediaPacket> _videoPacketQueue;
    private Channel<MediaPacket> _audioPacketQueue;
    private readonly SubtitlePacketQueue _subtitlePacketQueue;

    private readonly object _stateLock = new();
    private BufferState _state = BufferState.Empty;
    private TimeSpan _bufferedDuration = TimeSpan.Zero;
    private long _bufferedBytes;
    private bool _isReady;
    private TimeSpan _targetDuration = TimeSpan.FromSeconds(5);
    private TimeSpan _readyThreshold = TimeSpan.FromSeconds(2);
    private TimeSpan _starvedThreshold = TimeSpan.Zero; // 本地文件无 Starved
    private CancellationTokenSource _cts = new();
    private Task? _readerTask;
    private int _videoTrackIndex = -1;
    private int _audioTrackIndex = -1;

    // V2-06 C3: 流结束后包队列已 Complete，标记以便 StartAsync 重建
    private bool _completed;

    /// <summary>
    /// 初始化 <see cref="BufferManager"/> 的新实例。
    /// </summary>
    /// <param name="demuxer">解封装器。</param>
    /// <param name="logger">日志器。</param>
    public BufferManager(IMediaDemuxer demuxer, ILogger<BufferManager> logger)
    {
        _demuxer = demuxer;
        _logger = logger;

        _videoPacketQueue = Channel.CreateBounded<MediaPacket>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

        _audioPacketQueue = Channel.CreateBounded<MediaPacket>(
            new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

        _subtitlePacketQueue = new SubtitlePacketQueue();
    }

    /// <inheritdoc />
    public TimeSpan BufferedDuration
    {
        get { lock (_stateLock) return _bufferedDuration; }
    }

    /// <inheritdoc />
    public long BufferedBytes
    {
        get { lock (_stateLock) return _bufferedBytes; }
    }

    /// <inheritdoc />
    public bool IsReady
    {
        get { lock (_stateLock) return _isReady; }
    }

    /// <inheritdoc />
    public BufferState State
    {
        get { lock (_stateLock) return _state; }
    }

    /// <inheritdoc />
    public TimeSpan TargetDuration
    {
        get { lock (_stateLock) return _targetDuration; }
        set
        {
            lock (_stateLock)
            {
                _targetDuration = value;
                // 自动调整 Ready 阈值为目标的 40%
                _readyThreshold = TimeSpan.FromTicks(value.Ticks * 40 / 100);
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<BufferProgressEventArgs>? BufferProgressChanged;

    /// <summary>视频数据包队列（供 VideoPipeline 消费）。</summary>
    public Channel<MediaPacket> VideoPacketQueue => _videoPacketQueue;

    /// <summary>音频数据包队列（供 AudioPipeline 消费）。</summary>
    public Channel<MediaPacket> AudioPacketQueue => _audioPacketQueue;

    /// <summary>字幕数据包队列（供 SubtitleProcessor 消费）。</summary>
    public SubtitlePacketQueue SubtitlePacketQueue => _subtitlePacketQueue;

    /// <summary>
    /// 设置轨道索引（由 MediaPlayer.OpenAsync 调用）。
    /// </summary>
    public void SetTrackIndices(int videoTrackIndex, int audioTrackIndex)
    {
        _videoTrackIndex = videoTrackIndex;
        _audioTrackIndex = audioTrackIndex;
    }

    /// <summary>
    /// 配置为网络流模式（更大缓冲目标）。
    /// </summary>
    public void ConfigureForNetworkStream()
    {
        lock (_stateLock)
        {
            _targetDuration = TimeSpan.FromSeconds(30);
            _readyThreshold = TimeSpan.FromSeconds(5);
            _starvedThreshold = TimeSpan.FromSeconds(1);
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct = default)
    {
        // 停止旧的读取线程（Seek 场景）
        _cts.Cancel();
        if (_readerTask != null)
        {
            try { await _readerTask; } catch { }
        }

        // 重建 CTS
        _cts = new CancellationTokenSource();

        // V2-06 C3: Seek after stream end 场景——包队列已 Complete，
        // 重建通道以恢复可写状态（同时重置字幕队列与缓冲状态）
        if (_completed)
        {
            ResetQueues();
            _completed = false;
        }

        lock (_stateLock)
        {
            _state = BufferState.Buffering;
            _isReady = false;
        }

        OnBufferProgressChanged();

        // 启动后台读取线程（持续填充包队列）
        var readyTcs = new TaskCompletionSource<bool>();
        _readerTask = Task.Run(() => ReaderLoopAsync(_cts.Token, ct, readyTcs));

        // 等待 Ready 或流结束或取消
        await readyTcs.Task;
    }

    private async Task ReaderLoopAsync(
        CancellationToken internalCt,
        CancellationToken externalCt,
        TaskCompletionSource<bool> readyTcs)
    {
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(internalCt, externalCt);

            while (!linkedCts.Token.IsCancellationRequested)
            {
                var packet = await _demuxer.ReadPacketAsync(linkedCts.Token);
                if (packet == null)
                {
                    // 流结束，标记队列完成
                    Complete();
                    lock (_stateLock)
                    {
                        _state = BufferState.Ready;
                        _isReady = true;
                    }
                    OnBufferProgressChanged();
                    readyTcs.TrySetResult(true);
                    return;
                }

                // 更新缓冲统计（在分发前，避免 Dispose 后访问已释放对象属性）
                UpdateBufferStats(packet);

                // 按 TrackIndex 分发
                if (packet.TrackIndex == _videoTrackIndex)
                {
                    try { await _videoPacketQueue.Writer.WriteAsync(packet, linkedCts.Token); }
                    catch (OperationCanceledException) { packet.Dispose(); throw; }
                    catch (ChannelClosedException) { packet.Dispose(); throw; }
                }
                else if (packet.TrackIndex == _audioTrackIndex)
                {
                    try { await _audioPacketQueue.Writer.WriteAsync(packet, linkedCts.Token); }
                    catch (OperationCanceledException) { packet.Dispose(); throw; }
                    catch (ChannelClosedException) { packet.Dispose(); throw; }
                }
                else
                {
                    // 字幕或其他轨道
                    if (!_subtitlePacketQueue.TryEnqueue(packet))
                        packet.Dispose(); // 队列满，丢弃包防泄漏
                }

                // 检查是否达到 Ready 阈值（仅首次通知）
                if (CheckReady())
                {
                    readyTcs.TrySetResult(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
            readyTcs.TrySetCanceled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "缓冲管理器读取异常");
            readyTcs.TrySetException(ex);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        _cts.Cancel();
    }

    /// <summary>内部读取任务（供 DisposeAsync join）。</summary>
    internal Task? ReaderTask => _readerTask;

    /// <inheritdoc />
    public void Clear()
    {
        lock (_stateLock)
        {
            _state = BufferState.Empty;
            _bufferedDuration = TimeSpan.Zero;
            _bufferedBytes = 0;
            _isReady = false;
        }

        // 清空并 Dispose 所有包
        while (_videoPacketQueue.Reader.TryRead(out var packet))
            packet.Dispose();

        while (_audioPacketQueue.Reader.TryRead(out var packet))
            packet.Dispose();

        _subtitlePacketQueue.Clear();

        OnBufferProgressChanged();
    }

    /// <summary>标记队列完成（流结束）。</summary>
    public void Complete()
    {
        _completed = true;
        _videoPacketQueue.Writer.TryComplete();
        _audioPacketQueue.Writer.TryComplete();
        _subtitlePacketQueue.Complete();
    }

    /// <summary>
    /// 重置所有包队列（V2-06 C3）。用于 Seek after stream end 场景：
    /// 流结束后包队列已 Complete，重建通道以恢复可写状态。
    /// </summary>
    /// <remarks>
    /// <para>纯内存同步操作（无 I/O）。重建视频/音频有界通道，并重置字幕队列与缓冲状态统计。</para>
    /// <para>帧队列（FrameQueue/SampleQueue）不由本管理器持有，由 MediaPlayer 在 Seek 时
    /// 经管线 Flush 清空重置，故此处不处理。</para>
    /// </remarks>
    public void ResetQueues()
    {
        // 终止旧通道写入（幂等）
        _videoPacketQueue.Writer.TryComplete();
        _audioPacketQueue.Writer.TryComplete();

        // 排空并释放旧通道中残留的包（MediaPacket 可能持有原生 _dataOwner，
        // 不释放会泄漏原生引用计数；读取线程已停止，无并发写入，TryRead 非阻塞安全）
        while (_videoPacketQueue.Reader.TryRead(out var vPacket))
            vPacket.Dispose();
        while (_audioPacketQueue.Reader.TryRead(out var aPacket))
            aPacket.Dispose();

        // 重建有界通道（恢复可写，参数与原构造一致）
        _videoPacketQueue = Channel.CreateBounded<MediaPacket>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

        _audioPacketQueue = Channel.CreateBounded<MediaPacket>(
            new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

        // 字幕队列重置（重建通道）
        _subtitlePacketQueue.Reset();

        // 重置缓冲状态
        lock (_stateLock)
        {
            _state = BufferState.Empty;
            _bufferedDuration = TimeSpan.Zero;
            _bufferedBytes = 0;
            _isReady = false;
        }

        OnBufferProgressChanged();
    }

    private void UpdateBufferStats(MediaPacket packet)
    {
        lock (_stateLock)
        {
            _bufferedBytes += packet.Data.Length;
            if (packet.Duration > TimeSpan.Zero)
                _bufferedDuration += packet.Duration;
        }
    }

    private bool CheckReady()
    {
        lock (_stateLock)
        {
            if (_isReady)
                return false; // 已 Ready，不再重复通知

            // Ready 条件：缓冲时长达到阈值，或无时长信息但有字节（某些容器无 Duration）
            if (_bufferedDuration >= _readyThreshold
                || (_bufferedDuration == TimeSpan.Zero && _bufferedBytes > 0))
            {
                _state = BufferState.Ready;
                _isReady = true;
                OnBufferProgressChanged();
                return true;
            }

            return false;
        }
    }

    private void OnBufferProgressChanged()
    {
        BufferProgressEventArgs args;
        lock (_stateLock)
        {
            var progress = _targetDuration > TimeSpan.Zero
                ? Math.Min(1.0f, (float)(_bufferedDuration / _targetDuration))
                : 1.0f;

            args = new BufferProgressEventArgs(_state, _bufferedDuration, _bufferedBytes, progress);
        }

        BufferProgressChanged?.Invoke(this, args);
    }
}

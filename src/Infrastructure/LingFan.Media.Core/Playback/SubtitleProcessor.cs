using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

/// <summary>
/// 字幕处理组件。轻量级，非完整管线。
/// </summary>
/// <remarks>
/// <para>字幕频率低、无 GPU 资源、显示时机由 Start/End 时间戳决定，</para>
/// <para>无需帧队列和同步器。</para>
/// <para>从 SubtitlePacketQueue 读取字幕包 → SubtitleDecoder 解码 → 缓存 SubtitleFrame →</para>
/// <para>按 clock.Position 检查当前应显示的字幕 → 触发 SubtitleReceived 事件。</para>
/// <para>SubtitleFrame 不实现 IDisposableFrame（仅含 string + TimeSpan，无原生资源）。</para>
/// <para>所有方法均为同步 void。</para>
/// </remarks>
public sealed class SubtitleProcessor : IAsyncDisposable, IDisposable
{
    private readonly ISubtitleDecoder _decoder;
    private readonly SubtitlePacketQueue _packetQueue;
    private readonly IMediaClock _clock;
    private readonly ILogger<SubtitleProcessor> _logger;

    private CancellationTokenSource _cts = new();
    private Task? _processTask;
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    private volatile bool _pauseAcknowledged;
    private TaskCompletionSource<bool>? _pauseAckTcs;

    /// <summary>
    /// 解码锁：确保 DecodeAsync 与 Reset 不会并发执行。
    /// SubtitleLoop 在解码+缓存期间持有锁，Clear/ClearAsync 在 Clear+Reset 前获取锁。
    /// 即使暂停确认超时（处理线程卡在长解码中），锁也能确保安全。
    /// </summary>
    private readonly SemaphoreSlim _decodeLock = new(1, 1);
    private volatile bool _pendingDecoderReset;
    private bool _disposed;

    private readonly object _subtitleLock = new();
    private readonly List<SubtitleFrame> _cachedSubtitles = new();
    private SubtitleFrame? _currentSubtitle;

    /// <summary>
    /// 初始化 <see cref="SubtitleProcessor"/> 的新实例。
    /// </summary>
    public SubtitleProcessor(
        ISubtitleDecoder decoder,
        SubtitlePacketQueue packetQueue,
        IMediaClock clock,
        ILogger<SubtitleProcessor> logger)
    {
        _decoder = decoder;
        _packetQueue = packetQueue;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>是否运行。</summary>
    public bool IsRunning => _isRunning;

    /// <summary>内部处理任务（供 DisposeAsync join）。</summary>
    internal Task? ProcessTask => _processTask;

    /// <summary>字幕帧到达事件（null = 无活动字幕，UI 清除显示）。</summary>
    public event EventHandler<SubtitleFrame?>? SubtitleReceived;

    /// <summary>
    /// 开始字幕处理。
    /// </summary>
    public void Start()
    {
        if (_isRunning)
        {
            _isPaused = false;
            return;
        }

        _isRunning = true;
        _isPaused = false;

        if (_cts.IsCancellationRequested)
        {
            _cts = new CancellationTokenSource();
        }

        _processTask = Task.Run(SubtitleLoop);
    }

    /// <summary>
    /// 暂停处理。
    /// </summary>
    public void Pause()
    {
        _isPaused = true;
    }

    /// <summary>
    /// 停止处理。
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _isPaused = false;
        _cts.Cancel();
    }

    /// <summary>
    /// 清空缓存的字幕帧（Seek 后调用）。同步版本，用于无法 await 的场景。
    /// V2 修复（L2）：先暂停处理线程，等待确认或获取解码锁后清空和重置，最后恢复运行。
    /// </summary>
    /// <remarks>
    /// <para>两阶段安全保证：</para>
    /// <list type="number">
    /// <item>暂停确认（150ms 超时）：快速路径，处理线程空闲时立即确认</item>
    /// <item>解码锁（2s 超时）：慢速路径，处理线程卡在长解码中时等待解码完成</item>
    /// </list>
    /// <para>即使暂停确认超时，解码锁也能确保 Reset 不与 DecodeAsync 并发。</para>
    /// <para>字幕循环频率低（10Hz），超时设为 150ms 以覆盖 Task.Delay(100)。</para>
    /// <para>优先使用异步版本 <see cref="ClearAsync"/>（无 Thread.Sleep 阻塞）。</para>
    /// </remarks>
    public void Clear()
    {
        var shouldResume = _isRunning && !_isPaused;
        if (_isRunning)
        {
            _pauseAcknowledged = false;
            _isPaused = true;

            // 阶段1: 等待暂停确认（快速路径，150ms 超时）
            // 字幕循环频率低（Task.Delay(100)），需更长超时
            for (var i = 0; i < 150 && !_pauseAcknowledged; i++)
            {
                Thread.Sleep(1);
            }

            if (!_pauseAcknowledged)
            {
                _logger.LogWarning("字幕处理暂停确认超时（150ms），等待解码锁确保安全");
            }

            // 阶段2: 获取解码锁（慢速路径，确保无 DecodeAsync 在执行）
            if (_decodeLock.Wait(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    lock (_subtitleLock)
                    {
                        _cachedSubtitles.Clear();
                        _currentSubtitle = null;
                    }
                    _decoder.Reset();
                }
                finally
                {
                    _decodeLock.Release();
                }
            }
            else
            {
                _logger.LogError("字幕处理解码锁获取超时（2s），标记延迟 Reset 防止竞态崩溃");
                lock (_subtitleLock)
                {
                    _cachedSubtitles.Clear();
                    _currentSubtitle = null;
                }
                _pendingDecoderReset = true;   // 锁超时未做 Reset，待处理线程下次进入锁时补做，确保解码器状态必然复位
            }
        }
        else
        {
            // 处理线程未运行，无需锁
            lock (_subtitleLock)
            {
                _cachedSubtitles.Clear();
                _currentSubtitle = null;
            }
            _decoder.Reset();
        }

        if (shouldResume)
        {
            _isPaused = false;
        }
    }

    /// <summary>
    /// 清空缓存的字幕帧（Seek 后调用）。异步版本，优先使用。
    /// V2 修复（L2）：先暂停处理线程，等待确认或获取解码锁后清空和重置，最后恢复运行。
    /// </summary>
    /// <remarks>
    /// <para>两阶段安全保证：</para>
    /// <list type="number">
    /// <item>暂停确认（150ms 超时）：快速路径，使用 TaskCompletionSource 信号通知</item>
    /// <item>解码锁（2s 超时）：慢速路径，处理线程卡在长解码中时等待解码完成</item>
    /// </list>
    /// <para>即使暂停确认超时，解码锁也能确保 Reset 不与 DecodeAsync 并发。</para>
    /// <para>字幕循环频率低（10Hz），超时设为 150ms 以覆盖 Task.Delay(100)。</para>
    /// <para>RunContinuationsAsynchronously 避免续体在处理线程执行。</para>
    /// </remarks>
    public async Task ClearAsync()
    {
        var shouldResume = _isRunning && !_isPaused;
        if (_isRunning)
        {
            _pauseAckTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pauseAcknowledged = false;
            _isPaused = true;

            // 阶段1: 等待暂停确认（快速路径，150ms 超时）
            try
            {
                await _pauseAckTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(150));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("字幕处理暂停确认超时（150ms），等待解码锁确保安全");
            }

            // 阶段2: 获取解码锁（慢速路径，确保无 DecodeAsync 在执行）
            if (await _decodeLock.WaitAsync(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    lock (_subtitleLock)
                    {
                        _cachedSubtitles.Clear();
                        _currentSubtitle = null;
                    }
                    _decoder.Reset();
                }
                finally
                {
                    _decodeLock.Release();
                }
            }
            else
            {
                _logger.LogError("字幕处理解码锁获取超时（2s），标记延迟 Reset 防止竞态崩溃");
                lock (_subtitleLock)
                {
                    _cachedSubtitles.Clear();
                    _currentSubtitle = null;
                }
                _pendingDecoderReset = true;   // 锁超时未做 Reset，待处理线程下次进入锁时补做，确保解码器状态必然复位
            }
        }
        else
        {
            // 处理线程未运行，无需锁
            lock (_subtitleLock)
            {
                _cachedSubtitles.Clear();
                _currentSubtitle = null;
            }
            _decoder.Reset();
        }

        if (shouldResume)
        {
            _isPaused = false;
        }
    }

    private async Task SubtitleLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    _pauseAcknowledged = true;
                    _pauseAckTcs?.TrySetResult(true);
                    await Task.Delay(50, _cts.Token);
                    continue;
                }

                // 1. 解码新的字幕包（加锁防止与 Clear/Reset 竞态）
                if (_packetQueue.TryDequeue(out var packet) && packet != null)
                {
                    await _decodeLock.WaitAsync(_cts.Token);
                    try
                    {
                        // 隐患B修复：解码锁获取超时期间 Clear 可能跳过 Reset，此处补做，确保解码器内部状态必然复位
                        if (_pendingDecoderReset)
                        {
                            _decoder.Reset();
                            _pendingDecoderReset = false;
                        }

                        // 双重检查：获取锁后确认未暂停（防止在等待锁期间被 Clear 暂停）
                        if (_isPaused)
                        {
                            packet.Dispose();
                            continue; // finally 会释放锁，跳回循环顶部进入暂停分支
                        }

                        SubtitleFrame? subtitleFrame;
                        try
                        {
                            subtitleFrame = await _decoder.DecodeAsync(packet);
                        }
                        finally
                        {
                            packet.Dispose();
                        }

                        if (subtitleFrame != null)
                        {
                            lock (_subtitleLock)
                            {
                                _cachedSubtitles.Add(subtitleFrame);
                            }
                        }
                    }
                    finally
                    {
                        _decodeLock.Release();
                    }
                }

                // 2. 检查当前应显示的字幕
                CheckCurrentSubtitle();

                // 3. 低频检查（10Hz）
                await Task.Delay(100, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "字幕处理异常");
        }
        finally
        {
            _isRunning = false;
        }
    }

    private void CheckCurrentSubtitle()
    {
        var position = _clock.Position;

        SubtitleFrame? newSubtitle = null;

        lock (_subtitleLock)
        {
            // 查找当前时间应显示的字幕
            foreach (var subtitle in _cachedSubtitles)
            {
                if (position >= subtitle.Start && position < subtitle.End)
                {
                    newSubtitle = subtitle;
                    break;
                }
            }

            // 如果字幕变化了，触发事件
            if (!ReferenceEquals(newSubtitle, _currentSubtitle))
            {
                _currentSubtitle = newSubtitle;
            }
            else
            {
                return; // 无变化
            }
        }

        // 在锁外触发事件
        SubtitleReceived?.Invoke(this, newSubtitle);
    }

    /// <summary>
    /// 释放处理组件资源（解码锁和 CTS）。
    /// </summary>
    /// <remarks>
    /// <para>必须在处理线程退出后调用。DisposeAsync 路径在 Step_StopPipelinesAsync join 后调用。</para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 隐患A修复：释放信号量前确保处理线程已退出，避免 SemaphoreSlim.Dispose 与并发 WaitAsync/Release 的未定义行为
        EnsureThreadStopped();

        _decodeLock.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// 隐患A修复：释放解码锁前停止并 join 处理线程。
    /// 仅当当前不在处理线程自身上调用时才等待，避免自死锁。
    /// 正常流程（MediaPlayer 已先 join）下任务已完成，Wait 立即返回，无阻塞。
    /// </summary>
    private void EnsureThreadStopped()
    {
        if (_processTask is null)
            return;
        if (Task.CurrentId == _processTask.Id)
            return; // 防御：若在处理线程自身上调用则不等待（理论上不会发生）

        _isRunning = false;
        _isPaused = false;
        _cts.Cancel();
        try
        {
            _processTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "字幕处理线程 join 失败，仍继续释放资源");
        }
    }

    /// <summary>
    /// 异步释放处理组件资源。优先使用（MediaPlayer.DisposeAsync 在线程 join 后调用）。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

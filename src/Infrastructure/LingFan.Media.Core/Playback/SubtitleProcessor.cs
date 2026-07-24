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
public sealed class SubtitleProcessor
{
    private readonly ISubtitleDecoder _decoder;
    private readonly SubtitlePacketQueue _packetQueue;
    private readonly IMediaClock _clock;
    private readonly ILogger<SubtitleProcessor> _logger;

    private CancellationTokenSource _cts = new();
    private Task? _processTask;
    private volatile bool _isRunning;
    private volatile bool _isPaused;

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
    /// 清空缓存的字幕帧（Seek 后调用）。
    /// </summary>
    public void Clear()
    {
        lock (_subtitleLock)
        {
            _cachedSubtitles.Clear();
            _currentSubtitle = null;
        }
        _decoder.Reset();
    }

    private async Task SubtitleLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    await Task.Delay(50, _cts.Token);
                    continue;
                }

                // 1. 解码新的字幕包
                if (_packetQueue.TryDequeue(out var packet) && packet != null)
                {
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
}

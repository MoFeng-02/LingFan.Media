using System;
using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Playback;

/// <summary>
/// 回退包装播放器：转发 <see cref="IMediaPlayer"/> 全部成员到运行时选定的后端 player。
/// 自身不持有任何后端逻辑，仅在 <see cref="OpenAsync"/> 时按 <see cref="BackendFallbackMediaPlayerFactory.Backends"/> 注册顺序尝试后端组并回退。
/// </summary>
/// <remarks>
/// <para>每个播放独立：本实例只持有一个 <c>_active</c> 后端 player，不共享状态。</para>
/// <para>无头优先 / 出餐：video 经 <see cref="VideoFrameAvailable"/>、audio 经 <see cref="AudioDataAvailable"/> 出餐，与有头无头无关——有头即 UI 控件级 Present Sink 订阅消费。</para>
/// </remarks>
public sealed class FallbackMediaPlayer : IMediaPlayer
{
    private readonly BackendFallbackMediaPlayerFactory _owner;
    private readonly ILogger? _logger;
    private IMediaPlayer? _active;

    // Open 前可设置的本地偏好，Open 后透传到 _active。
    private float _volume = 1f;
    private bool _isMuted;
    private float _playbackRate = 1f;
    private ProcessingMode _mode = ProcessingMode.RealTime;

    public FallbackMediaPlayer(BackendFallbackMediaPlayerFactory owner, ILogger? logger)
    {
        _owner = owner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OpenAsync(IMediaSource source, CancellationToken ct = default)
    {
        var key = source.Identifier;

        // 换源重新打开：先彻底释放上一轮选定的后端，避免原生资源泄漏（违反 NativeCallGate 纪律）与事件双发。
        if (_active is not null)
        {
            DetachEvents();
            var prev = _active;
            _active = null;
            try { await prev.DisposeAsync().ConfigureAwait(false); }   // 委托 Core 走 NativeCallGate 兜底
            catch { /* 吞掉清理异常，避免掩盖本次 Open 意图 */ }
        }

        var backends = _owner.Backends;
        if (backends.Count == 0)
            throw new MediaBackendUnsupportedException(key);

        // 缓存命中：从该后端开始试（直接命中）；否则从 0 顺序试。两种都会环绕尝试其余后端，确保缓存失效时仍能回退。
        int start = _owner.Cache.TryGetValue(key, out var cached) && cached >= 0 && cached < backends.Count
            ? cached
            : 0;

        for (int k = 0; k < backends.Count; k++)
        {
            int i = (start + k) % backends.Count;
            var g = backends[i];
            IMediaPlayer? inner = null;
            try
            {
                // 用命中的 factory 接口组建 Session（lookup→instance，经核心 composer）。
                inner = _owner.Create(g.Demuxer, g.VideoDecoder, g.AudioDecoder, g.SubtitleDecoder);
                await inner.OpenAsync(source, ct).ConfigureAwait(false);
                _active = inner;
                _owner.Cache[key] = i;          // 单次标记 → 后续同样源直接命中
                _logger?.LogInformation("[Playback] 已用后端 {Backend} 打开 {Source}", g.Name, key);
                ApplyLocalSettings(inner);
                AttachEvents(inner);
                return;
            }
            catch (OperationCanceledException)
            {
                // 尊重取消：先释放本次已创建的 inner（避免原生资源泄漏），再原样上抛，不回退。
                if (inner is not null)
                {
                    try { await inner.DisposeAsync().ConfigureAwait(false); }
                    catch { /* 吞掉清理异常，避免掩盖取消根因 */ }
                }
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[Playback] 后端 {Backend} 无法打开 {Source}，回退下一顺位", g.Name, key);
                if (inner is not null)
                {
                    try { await inner.DisposeAsync().ConfigureAwait(false); }   // NativeCallGate 兜底清理原生资源
                    catch { /* 吞掉清理异常，避免掩盖回退根因 */ }
                }
            }
        }

        throw new MediaBackendUnsupportedException(key);
    }

    /// <inheritdoc />
    public MediaState State => _active?.State ?? MediaState.Stopped;

    /// <inheritdoc />
    public TimeSpan Position => _active?.Position ?? TimeSpan.Zero;

    /// <inheritdoc />
    public TimeSpan Duration => _active?.Duration ?? TimeSpan.Zero;

    /// <inheritdoc />
    public IMediaSession? Session => _active?.Session;

    /// <inheritdoc />
    public long VideoDroppedFrames => _active?.VideoDroppedFrames ?? 0;

    /// <inheritdoc />
    public float Volume
    {
        get => _active?.Volume ?? _volume;
        set { _volume = value; if (_active is not null) _active.Volume = value; }
    }

    /// <inheritdoc />
    public bool IsMuted
    {
        get => _active?.IsMuted ?? _isMuted;
        set { _isMuted = value; if (_active is not null) _active.IsMuted = value; }
    }

    /// <inheritdoc />
    public float PlaybackRate
    {
        get => _active?.PlaybackRate ?? _playbackRate;
        set { _playbackRate = value; if (_active is not null) _active.PlaybackRate = value; }
    }

    /// <inheritdoc />
    public ProcessingMode Mode
    {
        get => _active?.Mode ?? _mode;
        set { _mode = value; if (_active is not null) _active.Mode = value; }
    }

    /// <inheritdoc />
    public Task PlayAsync() => _active?.PlayAsync() ?? Task.CompletedTask;

    /// <inheritdoc />
    public Task PauseAsync() => _active?.PauseAsync() ?? Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default) => _active?.StopAsync(ct) ?? Task.CompletedTask;

    /// <inheritdoc />
    public Task SeekAsync(TimeSpan position, CancellationToken ct = default)
        => _active?.SeekAsync(position, ct) ?? Task.CompletedTask;

    /// <inheritdoc />
    public event EventHandler<MediaStateChangedEventArgs> StateChanged = delegate { };

    /// <inheritdoc />
    public event EventHandler<MediaErrorEventArgs> ErrorOccurred = delegate { };

    /// <inheritdoc />
    public event EventHandler<TimeSpan> PositionChanged = delegate { };

    /// <inheritdoc />
    public event EventHandler<SubtitleFrame?> SubtitleReceived = delegate { };

    /// <inheritdoc />
    public event Action<AudioFrame>? AudioDataAvailable;

    /// <inheritdoc />
    public event Action<VideoFrame>? VideoFrameAvailable;

    private void ApplyLocalSettings(IMediaPlayer inner)
    {
        inner.Volume = _volume;
        inner.IsMuted = _isMuted;
        inner.PlaybackRate = _playbackRate;
        inner.Mode = _mode;
    }

    private void AttachEvents(IMediaPlayer inner)
    {
        inner.StateChanged += OnStateChanged;
        inner.ErrorOccurred += OnErrorOccurred;
        inner.PositionChanged += OnPositionChanged;
        inner.SubtitleReceived += OnSubtitleReceived;
        inner.AudioDataAvailable += OnAudioDataAvailable;
        inner.VideoFrameAvailable += OnVideoFrameAvailable;
    }

    private void DetachEvents()
    {
        if (_active is null) return;
        _active.StateChanged -= OnStateChanged;
        _active.ErrorOccurred -= OnErrorOccurred;
        _active.PositionChanged -= OnPositionChanged;
        _active.SubtitleReceived -= OnSubtitleReceived;
        _active.AudioDataAvailable -= OnAudioDataAvailable;
        _active.VideoFrameAvailable -= OnVideoFrameAvailable;
    }

    // 事件 sender 统一为包装层自身（this）：消费方持有的 IMediaPlayer 即本实例，
    // 不应暴露底层易变的 inner；避免底层误传 null 时 sender! 强转非空的风险。
    private void OnStateChanged(object? sender, MediaStateChangedEventArgs e) => StateChanged(this, e);
    private void OnErrorOccurred(object? sender, MediaErrorEventArgs e) => ErrorOccurred(this, e);
    private void OnPositionChanged(object? sender, TimeSpan e) => PositionChanged(this, e);
    private void OnSubtitleReceived(object? sender, SubtitleFrame? e) => SubtitleReceived(this, e);
    private void OnAudioDataAvailable(AudioFrame f) => AudioDataAvailable?.Invoke(f);
    private void OnVideoFrameAvailable(VideoFrame f) => VideoFrameAvailable?.Invoke(f);

    /// <inheritdoc />
    public void Dispose()
    {
        DetachEvents();
        if (_active is not null)
        {
            try { _active.Dispose(); }
            catch { /* NativeCallGate 兜底 */ }
            _active = null;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DetachEvents();
        if (_active is not null)
        {
            var active = _active;
            _active = null;
            try { return active.DisposeAsync(); }
            catch { return ValueTask.CompletedTask; }
        }
        return ValueTask.CompletedTask;
    }
}

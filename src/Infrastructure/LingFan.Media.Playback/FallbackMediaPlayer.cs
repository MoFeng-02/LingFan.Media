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

        // 换源重新打开：先彻底释放上一次选定的后端，避免原生资源泄漏（违反 NativeCallGate 协议）与事件双发。
        if (_active is not null)
        {
            DetachEvents();
            var prev = _active;
            _active = null;
            try { await prev.DisposeAsync().ConfigureAwait(false); }   // 委托 Core 走 NativeCallGate 兜底
            catch { /* 吞掉清理异常，避免掩盖本次 Open 意图 */ }
        }

        // ── 格式记忆：Open 前轻量探测 (容器, 视频编码)，用于提前命中格式级缓存，跳过已知坏后端 ──
        // 仅本地文件做探测（网络流不预先建连、且探测会浪费一次 HTTP；不可 Seek 流探测器自行返回 Unknown）。
        // 探测失败或编码未知均无害：退回全试回退，且成功后仍会写入真实格式 key。
        ContainerFormat detectedContainer = ContainerFormat.Unknown;
        VideoCodec detectedVideo = VideoCodec.Unknown;
        if (source.Type == MediaSourceType.File && _owner._streamFactory is not null && _owner._formatDetector is not null)
        {
            IMediaStream? probe = null;
            try
            {
                probe = await _owner._streamFactory.CreateAsync(source, ct).ConfigureAwait(false);
                var profile = _owner._formatDetector.DetectProfile(probe);   // 内部已 Seek 回退
                detectedContainer = profile.Container;
                detectedVideo = profile.Video;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 探测失败 → 退回全试，不阻断播放 */ }
            finally { if (probe is not null) { try { probe.Close(); } catch { } } }
        }

        var backends = _owner.Backends;
        if (backends.Count == 0)
            throw new MediaBackendUnsupportedException(key);

        // ── 决定回退起点（公平、无硬编码）──
        // 优先：格式级记忆（同 (容器, 视频编码) 上次回退成功命中的后端）—避免每次同格式重走回退；
        // 其次：文件级记忆（同一文件上次命中的后端）；
        // 兜底：从 0 顺序试。三种都会环绕尝试其余后端，确保记忆失效时仍能回退。
        int start = 0;
        if (detectedContainer != ContainerFormat.Unknown && detectedVideo != VideoCodec.Unknown
            && _owner.FormatCache.TryGetValue(new FormatKey(detectedContainer, detectedVideo), out var fmtHit)
            && fmtHit >= 0 && fmtHit < backends.Count)
        {
            start = fmtHit;
        }
        else if (_owner.Cache.TryGetValue(key, out var cached) && cached >= 0 && cached < backends.Count)
        {
            start = cached;
        }

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
                // 格式记忆：用会话真实视频编码（探测可能不准）写精确 key，
                // 确保 mp4/H264 与 mp4/H265 各自独立记忆、互不污染；webm/VP9 等回退结果同样被记住。
                var realVideo = detectedVideo;
                try
                {
                    var videoTrack = _active.Session?.VideoTracks.FirstOrDefault(t => t.Type == TrackType.Video);
                    if (videoTrack?.VideoCodec is VideoCodec rv && rv != VideoCodec.Unknown)
                        realVideo = rv;
                }
                catch { /* 读轨道失败不阻断记忆写入 */ }
                if (detectedContainer != ContainerFormat.Unknown && realVideo != VideoCodec.Unknown)
                    _owner.FormatCache[new FormatKey(detectedContainer, realVideo)] = i;
                _owner.Cache[key] = i;          // 单次标记 → 后续同样源直接命中
                _logger?.LogInformation("[Playback] 已用后端 {Backend} 打开 {Source}（格式 {Container}/{Video}）", g.Name, key, detectedContainer, realVideo);
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
                    catch { /* 吞掉清理异常，避免掩盖取消成因 */ }
                }
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[Playback] 后端 {Backend} 无法打开 {Source}，回退下一顺位", g.Name, key);
                if (inner is not null)
                {
                    try { await inner.DisposeAsync().ConfigureAwait(false); }   // NativeCallGate 兜底清理原生资源
                    catch { /* 吞掉清理异常，避免掩盖回退成因 */ }
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

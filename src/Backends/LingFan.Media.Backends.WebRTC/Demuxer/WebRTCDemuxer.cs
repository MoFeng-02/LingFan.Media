namespace LingFan.Media.Backends.WebRTC.Demuxer;

/// <summary>
/// WebRTC <see cref="IMediaDemuxer"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>当前状态</b>：需要原生 WebRTC 库（PeerConnection API），尚未集成。</para>
/// <para>所有运行时操作抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <see cref="Task.CompletedTask"/>。</item>
/// <item><see cref="OpenAsync"/>：抛 <see cref="PlatformNotSupportedException"/>（真异步签名保留，未来集成原生库后实现）。</item>
/// <item><see cref="ReadPacketAsync"/>：抛 <see cref="PlatformNotSupportedException"/>。</item>
/// <item><see cref="SeekAsync"/>：抛 <see cref="PlatformNotSupportedException"/>。</item>
/// <item><see cref="Close"/> / <see cref="Dispose"/> / <see cref="DisposeAsync"/>：同步，无操作。</item>
/// </list>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
internal sealed class WebRTCDemuxer : IMediaDemuxer
{
    private readonly ILogger<WebRTCDemuxer> _logger;
    private bool _disposed;

    public WebRTCDemuxer(ILogger<WebRTCDemuxer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public IReadOnlyList<MediaTrack> Tracks => Array.Empty<MediaTrack>();

    /// <inheritdoc/>
    public MediaMetadata Metadata => new();

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task OpenAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new PlatformNotSupportedException(
            "WebRTC 后端需要原生 WebRTC 库（如 Google libwebrtc C API 绑定），尚未集成。" +
            "请使用 FFmpeg 或 VLC 后端进行媒体播放。");
    }

    /// <inheritdoc/>
    public ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default)
    {
        throw new PlatformNotSupportedException(
            "WebRTC 后端需要原生 WebRTC 库，尚未集成。");
    }

    /// <inheritdoc/>
    public Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new PlatformNotSupportedException(
            "WebRTC 后端需要原生 WebRTC 库，尚未集成。");
    }

    /// <inheritdoc/>
    public void Close() { }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

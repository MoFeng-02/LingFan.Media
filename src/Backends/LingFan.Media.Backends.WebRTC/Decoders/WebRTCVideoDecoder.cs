namespace LingFan.Media.Backends.WebRTC.Decoders;

/// <summary>
/// WebRTC <see cref="IVideoDecoder"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>当前状态</b>：需要原生 WebRTC 库（PeerConnection VideoTrack API），尚未集成。</para>
/// <para>DecodeAsync 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
internal sealed class WebRTCVideoDecoder : IVideoDecoder
{
    private readonly ILogger<WebRTCVideoDecoder> _logger;
    private bool _disposed;

    public WebRTCVideoDecoder(ILogger<WebRTCVideoDecoder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public VideoCodec Codec => VideoCodec.Unknown;

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <inheritdoc/>
    public void Initialize(VideoCodec codec, VideoSettings settings)
    {
        // 不抛异常：允许工厂创建实例，运行时操作再抛
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
    {
        throw new PlatformNotSupportedException(
            "WebRTC 后端需要原生 WebRTC 库，尚未集成。");
    }

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> FlushAsync()
    {
        return new ValueTask<VideoFrame?>((VideoFrame?)null);
    }

    /// <inheritdoc/>
    public void Reset() { }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

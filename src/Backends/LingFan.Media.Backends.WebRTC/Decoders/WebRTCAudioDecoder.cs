namespace LingFan.Media.Backends.WebRTC.Decoders;

/// <summary>
/// WebRTC <see cref="IAudioDecoder"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>当前状态</b>：需要原生 WebRTC 库（PeerConnection AudioTrack API），尚未集成。</para>
/// <para>DecodeAsync 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
internal sealed class WebRTCAudioDecoder : IAudioDecoder
{
    private readonly ILogger<WebRTCAudioDecoder> _logger;
    private bool _disposed;

    public WebRTCAudioDecoder(ILogger<WebRTCAudioDecoder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public AudioCodec Codec => AudioCodec.Unknown;

    /// <inheritdoc/>
    public int OutputSampleRate => 48000;

    /// <inheritdoc/>
    public int OutputChannels => 1;

    /// <inheritdoc/>
    public void Initialize(AudioCodec codec, AudioSettings settings)
    {
        // 不抛异常：允许工厂创建实例
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet)
    {
        throw new PlatformNotSupportedException(
            "WebRTC 后端需要原生 WebRTC 库，尚未集成。");
    }

    /// <inheritdoc/>
    public ValueTask<AudioFrame?> FlushAsync()
    {
        return new ValueTask<AudioFrame?>((AudioFrame?)null);
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

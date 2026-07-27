namespace LingFan.Media.Backends.WebRTC.Decoders;

/// <summary>
/// <see cref="IVideoDecoderFactory"/> 的 WebRTC 实现。
/// </summary>
public sealed class WebRTCVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public WebRTCVideoDecoderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IVideoDecoder Create(VideoCodec codec, VideoSettings settings)
    {
        var decoder = new WebRTCVideoDecoder(_loggerFactory.CreateLogger<WebRTCVideoDecoder>());
        decoder.Initialize(codec, settings);
        return decoder;
    }

    /// <inheritdoc/>
    public Task<IVideoDecoder> CreateAsync(VideoCodec codec, VideoSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IVideoDecoder>(Create(codec, settings));
    }
}

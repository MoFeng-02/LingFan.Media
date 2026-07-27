namespace LingFan.Media.Backends.WebRTC.Decoders;

/// <summary>
/// <see cref="IAudioDecoderFactory"/> 的 WebRTC 实现。
/// </summary>
public sealed class WebRTCAudioDecoderFactory : IAudioDecoderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public WebRTCAudioDecoderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IAudioDecoder Create(AudioCodec codec, AudioSettings settings)
    {
        var decoder = new WebRTCAudioDecoder(_loggerFactory.CreateLogger<WebRTCAudioDecoder>());
        decoder.Initialize(codec, settings);
        return decoder;
    }

    /// <inheritdoc/>
    public Task<IAudioDecoder> CreateAsync(AudioCodec codec, AudioSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IAudioDecoder>(Create(codec, settings));
    }
}

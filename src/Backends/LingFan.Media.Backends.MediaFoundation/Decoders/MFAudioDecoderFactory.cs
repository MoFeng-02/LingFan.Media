namespace LingFan.Media.Backends.MediaFoundation.Decoders;

/// <summary>
/// <see cref="IAudioDecoderFactory"/> 的 MediaFoundation 实现。
/// </summary>
public sealed class MFAudioDecoderFactory : IAudioDecoderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public MFAudioDecoderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IAudioDecoder Create(AudioCodec codec, AudioSettings settings)
    {
        var decoder = new MFAudioDecoder(_loggerFactory.CreateLogger<MFAudioDecoder>());
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

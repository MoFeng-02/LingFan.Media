using LingFan.Media.Backends.MediaCodec.Decoders;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// <see cref="AndroidAudioDecoder"/> 的工厂（<see cref="IAudioDecoderFactory"/>）。
/// </summary>
/// <remarks>
/// <para>Singleton 工厂，无状态。每次 Create/CreateAsync 返回新实例。</para>
/// <para>依赖倒置：仅依赖 <see cref="AndroidBackend"/> 与 <see cref="ILoggerFactory"/>，绝不引用 Renderers。</para>
/// </remarks>
internal sealed class AndroidAudioDecoderFactory : IAudioDecoderFactory
{
    private readonly AndroidBackend _backend;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>初始化工厂的新实例。</summary>
    public AndroidAudioDecoderFactory(AndroidBackend backend, ILoggerFactory loggerFactory)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IAudioDecoder Create(AudioCodec codec, AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AndroidAudioDecoder(_backend, _loggerFactory.CreateLogger<AndroidAudioDecoder>());
    }

    /// <inheritdoc/>
    public Task<IAudioDecoder> CreateAsync(AudioCodec codec, AudioSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IAudioDecoder>(
            new AndroidAudioDecoder(_backend, _loggerFactory.CreateLogger<AndroidAudioDecoder>()));
    }
}

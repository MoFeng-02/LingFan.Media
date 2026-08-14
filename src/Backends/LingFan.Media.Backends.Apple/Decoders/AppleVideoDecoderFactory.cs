using LingFan.Media.Backends.Apple.Decoders;

namespace LingFan.Media.Backends.Apple.Decoders;

/// <summary>
/// <see cref="AppleVideoDecoder"/> 的工厂（<see cref="IVideoDecoderFactory"/>）。
/// </summary>
/// <remarks>
/// <para>Singleton 工厂，无状态。每次 Create/CreateAsync 返回新实例。</para>
/// <para>依赖倒置：仅依赖 <see cref="AppleBackend"/> 与 <see cref="ILoggerFactory"/>，绝不引用 Renderers。</para>
/// </remarks>
internal sealed class AppleVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly AppleBackend _backend;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>初始化工厂的新实例。</summary>
    public AppleVideoDecoderFactory(AppleBackend backend, ILoggerFactory loggerFactory)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IVideoDecoder Create(VideoCodec codec, VideoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AppleVideoDecoder(_backend, _loggerFactory.CreateLogger<AppleVideoDecoder>());
    }

    /// <inheritdoc/>
    public Task<IVideoDecoder> CreateAsync(VideoCodec codec, VideoSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IVideoDecoder>(
            new AppleVideoDecoder(_backend, _loggerFactory.CreateLogger<AppleVideoDecoder>()));
    }
}

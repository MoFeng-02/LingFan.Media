using LingFan.Media.Backends.MediaCodec.Decoders;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// <see cref="AndroidVideoDecoder"/> 的工厂（<see cref="IVideoDecoderFactory"/>）。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 Create/CreateAsync 返回新实例。</para>
/// <para>依赖倒置：仅依赖 <see cref="AndroidBackend"/> 与 <see cref="ILoggerFactory"/>（Abstractions 传递），绝不引用 Renderers。
/// GPU 零拷贝已暂缓（详见设计文档 §5.2），视频解码器当前不依赖 <see cref="IGpuDeviceContext"/> / <see cref="IGpuFrameProducer"/>。</para>
/// </remarks>
internal sealed class AndroidVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly AndroidBackend _backend;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化工厂的新实例。
    /// </summary>
    /// <param name="backend">后端入口（持选项与平台能力）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public AndroidVideoDecoderFactory(AndroidBackend backend, ILoggerFactory loggerFactory)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IVideoDecoder Create(VideoCodec codec, VideoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var decoder = new AndroidVideoDecoder(_backend, _loggerFactory.CreateLogger<AndroidVideoDecoder>());
        decoder.Initialize(codec, settings); // 契约：工厂负责初始化解码器（同步初始化）
        return decoder;
    }

    /// <inheritdoc/>
    public Task<IVideoDecoder> CreateAsync(VideoCodec codec, VideoSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();
        var decoder = new AndroidVideoDecoder(_backend, _loggerFactory.CreateLogger<AndroidVideoDecoder>());
        decoder.Initialize(codec, settings); // 契约：工厂负责初始化解码器（同步初始化）
        return Task.FromResult<IVideoDecoder>(decoder);
    }
}

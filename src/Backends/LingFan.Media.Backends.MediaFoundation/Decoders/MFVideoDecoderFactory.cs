namespace LingFan.Media.Backends.MediaFoundation.Decoders;

/// <summary>
/// <see cref="IVideoDecoderFactory"/> 的 MediaFoundation 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新实例。</para>
/// <para><b>异步策略</b>（与 FFmpegVideoDecoderFactory 对称）：</para>
/// <list type="bullet">
/// <item><see cref="Create"/>：同步，手动 new + Initialize。</item>
/// <item><see cref="CreateAsync"/>：接口契约，返回 <see cref="Task.FromResult"/>。</item>
/// </list>
/// </remarks>
public sealed class MFVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IGpuDeviceContext? _gpuContext;
    private readonly IEnumerable<IGpuFrameProducer>? _gpuFrameProducers;

    public MFVideoDecoderFactory(
        ILoggerFactory loggerFactory,
        IGpuDeviceContext? gpuContext = null,
        IEnumerable<IGpuFrameProducer>? gpuFrameProducers = null)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _gpuContext = gpuContext;
        _gpuFrameProducers = gpuFrameProducers;
    }

    /// <inheritdoc/>
    public IVideoDecoder Create(VideoCodec codec, VideoSettings settings)
    {
        var decoder = new MFVideoDecoder(_loggerFactory.CreateLogger<MFVideoDecoder>(), _gpuContext, _gpuFrameProducers);
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

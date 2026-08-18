namespace LingFan.Media.Backends.FFmpeg.Decoders;

/// <summary>
/// <see cref="IVideoDecoderFactory"/> 的 FFmpeg 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新实例。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="Create"/>：同步，手动 new + <see cref="IVideoDecoder.Initialize"/>。</item>
/// <item><see cref="CreateAsync"/>：接口契约，无 I/O，
/// 返回 <see cref="Task.FromResult"/>。优先使用（支持 CT，未来硬解 GPU 设备初始化可能 I/O）。</item>
/// </list>
/// </remarks>
public sealed class FFmpegVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IGpuDeviceContext? _gpuContext;
    private readonly FFmpegOptions? _options;
    private readonly IEnumerable<IGpuFrameProducer>? _frameProducers;
    private readonly IVaApiExport? _vaApiExport;

    /// <summary>
    /// 初始化 <see cref="FFmpegVideoDecoderFactory"/> 的新实例。
    /// </summary>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <param name="gpuContext">可选 GPU 设备上下文（注册了 D3D11 渲染器时由 DI 注入，启用 D3D11VA 硬解）。</param>
    /// <param name="options">可选 FFmpeg 配置（AddFFmpeg 注册的 Singleton；含 MediaCodec Surface 注入点）。</param>
    /// <param name="frameProducers">可选零拷贝帧生产者（GL/Vulkan 渲染器注册，供 ffmpeg 自有 D3D11 设备场景下的 GPU 零拷贝导入）。</param>
    /// <param name="vaApiExport">可选 VAAPI 导出器（Linux 真实零拷贝：VA Surface → dma_buf；由 Platforms.Linux 注册）。</param>
    public FFmpegVideoDecoderFactory(ILoggerFactory loggerFactory, IGpuDeviceContext? gpuContext = null, FFmpegOptions? options = null, IEnumerable<IGpuFrameProducer>? frameProducers = null, IVaApiExport? vaApiExport = null)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _gpuContext = gpuContext;
        _options = options;
        _frameProducers = frameProducers;
        _vaApiExport = vaApiExport;
    }

    /// <inheritdoc/>
    public IVideoDecoder Create(VideoCodec codec, VideoSettings settings)
    {
        var decoder = new FFmpegVideoDecoder(_loggerFactory.CreateLogger<FFmpegVideoDecoder>(), _gpuContext, _options, _frameProducers, _vaApiExport);
        decoder.Initialize(codec, settings);
        return decoder;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：无 I/O（手动 new + 同步 Initialize），返回 <see cref="Task.FromResult"/>。
    /// 优先使用此方法（支持 CT，未来硬解 GPU 设备初始化可能 I/O）。
    /// </remarks>
    public Task<IVideoDecoder> CreateAsync(VideoCodec codec, VideoSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IVideoDecoder>(Create(codec, settings));
    }
}

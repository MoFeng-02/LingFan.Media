namespace LingFan.Media.Backends.FFmpeg.Decoders;

/// <summary>
/// <see cref="IVideoDecoderFactory"/> 的 FFmpeg 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新实例。</para>
/// <para>工厂构造期零原生触碰；<see cref="Create"/> 前调用 <see cref="FFmpegBackend.EnsureNativeInitialized"/>
/// 确保自绑定加载器就绪——只有真正使用 FFmpeg 后端时才要求原生库在场。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="Create"/>：同步，手动 new + <see cref="IVideoDecoder.Initialize"/>。
/// 不代为初始化 GPU 设备上下文——需要硬件解码且上下文尚未就绪时，调用方须先
/// <see cref="IGpuDeviceContext.InitializeAsync"/>，否则句柄为零、解码器回退软件解码。</item>
/// <item><see cref="CreateAsync"/>：先让 GPU 设备上下文就绪（<see cref="IGpuDeviceContext.InitializeAsync"/>）
/// 再创建解码器，因此可能触发一次同步 native 设备创建。管线侧优先使用。</item>
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
    /// <remarks>不代为初始化 GPU 设备上下文：<see cref="IGpuDeviceContext"/> 的句柄 getter 在设备未创建时返回零值，
    /// 同步路径无法 await 初始化。需要 D3D11VA / MediaCodec 等硬解时，调用方应先
    /// <see cref="IGpuDeviceContext.InitializeAsync"/>，或用 <see cref="CreateAsync"/>。</remarks>
    public IVideoDecoder Create(VideoCodec codec, VideoSettings settings)
    {
        FFmpegBackend.EnsureNativeInitialized(_options);
        var decoder = new FFmpegVideoDecoder(_loggerFactory.CreateLogger<FFmpegVideoDecoder>(), _gpuContext, _options, _frameProducers, _vaApiExport);
        decoder.Initialize(codec, settings);
        return decoder;
    }

    /// <inheritdoc/>
    /// <remarks>先让注入的 GPU 设备上下文就绪，再创建并初始化解码器；
    /// 上下文初始化失败只告警不抛，解码器据零句柄回退软件解码。</remarks>
    public async Task<IVideoDecoder> CreateAsync(VideoCodec codec, VideoSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureGpuContextReadyAsync(ct).ConfigureAwait(false);
        return Create(codec, settings);
    }

    /// <summary>
    /// 让注入的 GPU 设备上下文就绪（未注入时直接返回）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>硬解路径要求上下文提供有效句柄，而句柄 getter 不隐式创建设备，
    /// 故就绪动作放在这个异步入口。初始化失败不致命：只告警，让解码器回退软件解码。</remarks>
    private async Task EnsureGpuContextReadyAsync(CancellationToken ct)
    {
        if (_gpuContext is null) return;
        try
        {
            await _gpuContext.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<FFmpegVideoDecoderFactory>().LogWarning(ex, "GPU 设备上下文初始化失败，硬件解码将回退软件解码。");
        }
    }
}

namespace LingFan.Media.Backends.FFmpeg.Decoders;

/// <summary>
/// <see cref="IVideoDecoderFactory"/> 的 FFmpeg 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新实例。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="Create"/>：同步，手动 new + <see cref="IVideoDecoder.Initialize"/>。</item>
/// <item><see cref="CreateAsync"/>：接口契约，V1 无 I/O，
/// 返回 <see cref="Task.FromResult"/>。优先使用（支持 CT，未来硬解 GPU 设备初始化可能 I/O）。</item>
/// </list>
/// </remarks>
public sealed class FFmpegVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="FFmpegVideoDecoderFactory"/> 的新实例。
    /// </summary>
    public FFmpegVideoDecoderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IVideoDecoder Create(VideoCodec codec, VideoSettings settings)
    {
        var decoder = new FFmpegVideoDecoder(_loggerFactory.CreateLogger<FFmpegVideoDecoder>());
        decoder.Initialize(codec, settings);
        return decoder;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：V1 无 I/O（手动 new + 同步 Initialize），返回 <see cref="Task.FromResult"/>。
    /// 优先使用此方法（支持 CT，未来硬解 GPU 设备初始化可能 I/O）。
    /// </remarks>
    public Task<IVideoDecoder> CreateAsync(VideoCodec codec, VideoSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IVideoDecoder>(Create(codec, settings));
    }
}

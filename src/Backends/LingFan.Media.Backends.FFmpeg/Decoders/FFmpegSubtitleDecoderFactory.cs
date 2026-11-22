namespace LingFan.Media.Backends.FFmpeg.Decoders;

/// <summary>
/// <see cref="ISubtitleDecoderFactory"/> 的 FFmpeg 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新实例。</para>
/// <para>工厂按 <see cref="MediaTrack.SubtitleCodec"/> 预置解码器，解码器无需参数化 Initialize。</para>
/// <para>工厂构造期零原生触碰；<see cref="Create"/> 前调用 <see cref="FFmpegBackend.EnsureNativeInitialized"/>
/// 确保自绑定加载器就绪——只有真正使用 FFmpeg 后端时才要求原生库在场。</para>
/// <para><b>异步策略</b>（与 Video/Audio 工厂对称）：</para>
/// <list type="bullet">
/// <item><see cref="Create"/>：同步，手动 new + <see cref="FFmpegSubtitleDecoder.BindStream"/>（同步边界不初始化）。</item>
/// <item><see cref="CreateAsync"/>：首选入口，BindStream 后 await InitializeAsync，返回已初始化解码器。</item>
/// </list>
/// </remarks>
public sealed class FFmpegSubtitleDecoderFactory : ISubtitleDecoderFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly FFmpegOptions _options;

    /// <summary>
    /// 初始化 <see cref="FFmpegSubtitleDecoderFactory"/> 的新实例。
    /// </summary>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <param name="options">FFmpeg 配置（AddFFmpeg 注册的 Singleton，仅持有配置数据）。</param>
    public FFmpegSubtitleDecoderFactory(ILoggerFactory loggerFactory, FFmpegOptions options)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public ISubtitleDecoder Create(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        FFmpegBackend.EnsureNativeInitialized(_options);
        var decoder = new FFmpegSubtitleDecoder(_loggerFactory.CreateLogger<FFmpegSubtitleDecoder>());
        decoder.BindStream(track);
        return decoder;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 与 Video/Audio 工厂对称：CreateAsync 返回已初始化的解码器。
    /// InitializeAsync 当前无 I/O（avcodec_alloc_context3 + open2 同步完成），await 为未来
    /// 网络字幕加载等真实 I/O 预留；同步 Create 保持只 BindStream（同步边界无法 await）。
    /// </remarks>
    public async Task<ISubtitleDecoder> CreateAsync(MediaTrack track, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ct.ThrowIfCancellationRequested();
        FFmpegBackend.EnsureNativeInitialized(_options);
        var decoder = new FFmpegSubtitleDecoder(_loggerFactory.CreateLogger<FFmpegSubtitleDecoder>());
        decoder.BindStream(track);
        await decoder.InitializeAsync(ct).ConfigureAwait(false);
        return decoder;
    }
}

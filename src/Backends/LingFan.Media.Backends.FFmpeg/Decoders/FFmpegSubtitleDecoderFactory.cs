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
/// <item><see cref="Create"/>：同步，手动 new + <see cref="FFmpegSubtitleDecoder.BindStream"/>。</item>
/// <item><see cref="CreateAsync"/>：接口契约，无 I/O，返回 <see cref="Task.FromResult"/>。</item>
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
    /// 接口契约：无 I/O（手动 new + BindStream），返回 <see cref="Task.FromResult"/>。
    /// 优先使用此方法（支持 CT，对称一致性 + 未来网络字幕加载 I/O）。
    /// </remarks>
    public Task<ISubtitleDecoder> CreateAsync(MediaTrack track, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<ISubtitleDecoder>(Create(track));
    }
}

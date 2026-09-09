namespace LingFan.Media.Backends.FFmpeg.Demuxer;

/// <summary>
/// <see cref="IMediaDemuxerFactory"/> 的 FFmpeg 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新的
/// <see cref="FFmpegDemuxer"/> 实例（每次播放新建，不共享）。</para>
/// <para>工厂构造期零原生触碰；<see cref="Create"/> 前调用 <see cref="FFmpegBackend.EnsureNativeInitialized"/>
/// 确保自绑定加载器就绪——只有真正使用 FFmpeg 后端时才要求原生库在场，其他后端回退不受影响。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="Create"/>：同步，手动 new，无 I/O。</item>
/// <item><see cref="CreateAsync"/>：接口契约，手动 new + CT 检查，返回 <see cref="Task.FromResult"/>。
/// 优先使用 <see cref="CreateAsync"/>（支持 CT，未来可异步初始化）。</item>
/// </list>
/// </remarks>
public sealed class FFmpegDemuxerFactory : IMediaDemuxerFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly FFmpegOptions _options;

    /// <summary>
    /// 初始化 <see cref="FFmpegDemuxerFactory"/> 的新实例。
    /// </summary>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <param name="options">FFmpeg 配置（AddFFmpeg 注册的 Singleton，仅持有配置数据）。</param>
    public FFmpegDemuxerFactory(ILoggerFactory loggerFactory, FFmpegOptions options)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    /// <remarks>同步边界：手动 new，无 I/O。仅用于 FFmpeg AVIO 回调等原生同步边界。</remarks>
    public IMediaDemuxer Create(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        FFmpegBackend.EnsureNativeInitialized(_options);
        return new FFmpegDemuxer(stream, _loggerFactory.CreateLogger<FFmpegDemuxer>());
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：无 I/O（手动 new），返回 <see cref="Task.FromResult"/>。
    /// 优先使用此方法（支持 CT，未来可异步初始化 FFmpeg 库）。
    /// </remarks>
    public Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ct.ThrowIfCancellationRequested();
        FFmpegBackend.EnsureNativeInitialized(_options);
        return Task.FromResult<IMediaDemuxer>(new FFmpegDemuxer(stream, _loggerFactory.CreateLogger<FFmpegDemuxer>()));
    }
}

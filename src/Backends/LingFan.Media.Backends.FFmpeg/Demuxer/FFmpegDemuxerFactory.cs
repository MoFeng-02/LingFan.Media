namespace LingFan.Media.Backends.FFmpeg.Demuxer;

/// <summary>
/// <see cref="IMediaDemuxerFactory"/> 的 FFmpeg 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新的
/// <see cref="FFmpegDemuxer"/> 实例（每次播放新建，不共享）。</para>
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

    /// <summary>
    /// 初始化 <see cref="FFmpegDemuxerFactory"/> 的新实例。
    /// </summary>
    /// <param name="loggerFactory">日志工厂。</param>
    public FFmpegDemuxerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    /// <remarks>同步边界：手动 new，无 I/O。仅用于 FFmpeg AVIO 回调等原生同步边界。</remarks>
    public IMediaDemuxer Create(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new FFmpegDemuxer(stream, _loggerFactory.CreateLogger<FFmpegDemuxer>());
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：V1 无 I/O（手动 new），返回 <see cref="Task.FromResult"/>。
    /// 优先使用此方法（支持 CT，未来可异步初始化 FFmpeg 库）。
    /// </remarks>
    public Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IMediaDemuxer>(new FFmpegDemuxer(stream, _loggerFactory.CreateLogger<FFmpegDemuxer>()));
    }
}

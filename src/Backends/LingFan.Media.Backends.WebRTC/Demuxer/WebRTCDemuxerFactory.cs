namespace LingFan.Media.Backends.WebRTC.Demuxer;

/// <summary>
/// <see cref="IMediaDemuxerFactory"/> 的 WebRTC 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。</para>
/// <para><b>异步策略</b>（与 FFmpegDemuxerFactory 对称）：</para>
/// <list type="bullet">
/// <item><see cref="Create"/>：同步，手动 new，无 I/O。</item>
/// <item><see cref="CreateAsync"/>：接口契约，返回 <see cref="Task.FromResult"/>。</item>
/// </list>
/// </remarks>
public sealed class WebRTCDemuxerFactory : IMediaDemuxerFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public WebRTCDemuxerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IMediaDemuxer Create(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new WebRTCDemuxer(_loggerFactory.CreateLogger<WebRTCDemuxer>());
    }

    /// <inheritdoc/>
    public Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IMediaDemuxer>(new WebRTCDemuxer(_loggerFactory.CreateLogger<WebRTCDemuxer>()));
    }
}

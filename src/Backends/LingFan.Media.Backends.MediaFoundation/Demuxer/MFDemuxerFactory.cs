namespace LingFan.Media.Backends.MediaFoundation.Demuxer;

/// <summary>
/// <see cref="IMediaDemuxerFactory"/> 的 MediaFoundation 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新实例。</para>
/// <para><b>仅 Windows 可用</b>：工厂方法本身不检查平台（允许 DI 注册），
/// 实际平台检查在 <see cref="MFDemuxer.OpenAsync"/> 中执行。</para>
/// <para><b>异步策略</b>（与 FFmpegDemuxerFactory 对称）：</para>
/// <list type="bullet">
/// <item><see cref="Create"/>：同步，手动 new，无 I/O。</item>
/// <item><see cref="CreateAsync"/>：接口契约，返回 <see cref="Task.FromResult"/>。</item>
/// </list>
/// </remarks>
public sealed class MFDemuxerFactory : IMediaDemuxerFactory
{
    private readonly MFBackend _backend;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="MFDemuxerFactory"/> 的新实例。
    /// </summary>
    public MFDemuxerFactory(MFBackend backend, ILoggerFactory loggerFactory)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IMediaDemuxer Create(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new MFDemuxer(_backend, _loggerFactory.CreateLogger<MFDemuxer>());
    }

    /// <inheritdoc/>
    public Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IMediaDemuxer>(new MFDemuxer(_backend, _loggerFactory.CreateLogger<MFDemuxer>()));
    }
}

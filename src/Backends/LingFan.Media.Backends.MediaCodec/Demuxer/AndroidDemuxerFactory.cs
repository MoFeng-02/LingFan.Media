using LingFan.Media.Backends.MediaCodec.Demuxer;

namespace LingFan.Media.Backends.MediaCodec.Demuxer;

/// <summary>
/// <see cref="AndroidDemuxer"/> 的工厂（<see cref="IMediaDemuxerFactory"/>）。
/// </summary>
/// <remarks>
/// <para>Singleton 工厂，无状态。每次 <c>Create/CreateAsync</c> 返回新实例（每次播放新建，不共享）。</para>
/// <para>依赖倒置：仅依赖 <see cref="AndroidBackend"/>（同后端入口）与 <see cref="ILoggerFactory"/>（Abstractions 传递），
/// 绝不引用任何 Renderers 程序集。</para>
/// </remarks>
internal sealed class AndroidDemuxerFactory : IMediaDemuxerFactory
{
    private readonly AndroidBackend _backend;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>初始化工厂的新实例。</summary>
    public AndroidDemuxerFactory(AndroidBackend backend, ILoggerFactory loggerFactory)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IMediaDemuxer Create(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new AndroidDemuxer(_backend, _loggerFactory.CreateLogger<AndroidDemuxer>());
    }

    /// <inheritdoc/>
    public Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IMediaDemuxer>(
            new AndroidDemuxer(_backend, _loggerFactory.CreateLogger<AndroidDemuxer>()));
    }
}

using LingFan.Media.Abstractions;

namespace LingFan.Media.Backends.VLCNative.Demuxer;

/// <summary>
/// <see cref="VLCNativeDemuxer"/> 工厂（零 LibVLCSharp）。
/// </summary>
/// <remarks>
/// <para>Singleton 工厂，无状态；每次 Create 返回新实例（每次播放新建，不共享）。</para>
/// <para>持 <see cref="Lazy{VLCNativeBackend}"/> 延迟原生后端初始化（仅首次播放时构造 libvlc 引擎）。</para>
/// </remarks>
public sealed class VLCNativeDemuxerFactory : IMediaDemuxerFactory
{
    private readonly Lazy<VLCNativeBackend> _backendLazy;
    private readonly ILogger<VLCNativeDemuxer> _demuxerLogger;

    /// <summary>
    /// 初始化 <see cref="VLCNativeDemuxerFactory"/> 的新实例。
    /// </summary>
    public VLCNativeDemuxerFactory(Lazy<VLCNativeBackend> backendLazy, ILoggerFactory loggerFactory)
    {
        _backendLazy = backendLazy ?? throw new ArgumentNullException(nameof(backendLazy));
        _demuxerLogger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<VLCNativeDemuxer>();
    }

    /// <inheritdoc/>
    public IMediaDemuxer Create(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _demuxerLogger.LogDebug("创建 VLC Native 解复用器（backend 延迟初始化）");
        return new VLCNativeDemuxer(_backendLazy.Value, _demuxerLogger);
    }

    /// <inheritdoc/>
    public Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Create(stream));
    }
}

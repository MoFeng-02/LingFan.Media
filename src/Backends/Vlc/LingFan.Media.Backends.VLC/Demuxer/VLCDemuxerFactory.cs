namespace LingFan.Media.Backends.VLC.Demuxer;

/// <summary>
/// <see cref="IMediaDemuxerFactory"/> 的 VLC 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新的
/// <see cref="VLCDemuxer"/> 实例（每次播放新建，不共享）。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="Create"/>：同步，手动 new，无 I/O。</item>
/// <item><see cref="CreateAsync"/>：接口契约，手动 new + CT 检查，返回 <see cref="Task.FromResult"/>。
/// 优先使用 <see cref="CreateAsync"/>（支持 CT）。</item>
/// </list>
/// </remarks>
public sealed class VLCDemuxerFactory : IMediaDemuxerFactory
{
    // 🔴 延迟原生：持有 Lazy<VLCBackend> 而非 VLCBackend 实例。VLCBackend 构造会 new LibVLC（原生加载），
    // 若在此 Singleton 工厂构造期解析，则访问 IBackendRegistry.Backends 或注册 VLC 即触发原生加载
    // （且要求 libvlc.dll 在场）——违背“注册一个后端 ≠ 马上要它的 native 库”。注意 MS DI 默认【不】自动
    // 解析 Lazy<T>（仅自动支持集合类型），须由 AddLingFanMedia→AddLazySupport 注册通用 Lazy<> 解析；
    // 注册后 Lazy 仅在 .Value 首次访问时才从容器解析 VLCBackend（=> new LibVLC 延迟到
    // 真正 Open 用到 VLC 时）；与 FFmpeg 工厂保持同一延迟语义。
    private readonly Lazy<VLCBackend> _backendFactory;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="VLCDemuxerFactory"/> 的新实例。
    /// </summary>
    /// <param name="backendFactory">VLC 后端入口的延迟工厂（Singleton，首次访问 .Value 才构造 LibVLC）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public VLCDemuxerFactory(Lazy<VLCBackend> backendFactory, ILoggerFactory loggerFactory)
    {
        _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    /// <remarks>同步边界：手动 new，无 I/O。仅用于无法 await 的原生同步边界。</remarks>
    public IMediaDemuxer Create(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new VLCDemuxer(_backendFactory.Value, _loggerFactory.CreateLogger<VLCDemuxer>());
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：无 I/O（手动 new），返回 <see cref="Task.FromResult"/>。
    /// 优先使用此方法（支持 CT）。
    /// </remarks>
    public Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IMediaDemuxer>(new VLCDemuxer(_backendFactory.Value, _loggerFactory.CreateLogger<VLCDemuxer>()));
    }
}

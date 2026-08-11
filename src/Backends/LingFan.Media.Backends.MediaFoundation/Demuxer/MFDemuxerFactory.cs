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
    // 延迟原生：持有 Lazy<MFBackend> 而非 MFBackend 实例。MFBackend 构造会 MFStartup（原生），
    // 且在非 Windows 直接抛 PlatformNotSupportedException。注意 MS DI 默认【不】自动解析 Lazy<T>
    // （仅自动支持 IEnumerable/IList/数组等集合类型），须由 AddLingFanMedia→AddLazySupport 注册通用 Lazy<> 解析；
    // 注册后 Lazy 仅在 .Value 首次访问时才从容器解析 MFBackend（=> MFStartup 原生延迟到真正 Open 用到 MF 时）。
    // 若在此 Singleton 工厂构造期解析，则访问 IBackendRegistry.Backends 或注册 MF 即触发原生加载/抛异常——
    // 违背“注册一个后端 ≠ 马上要它的 native 库”。与 FFmpeg/VLC 工厂保持同一延迟语义；平台检查顺延到实际使用时。
    private readonly Lazy<MFBackend> _backendFactory;
    private readonly ILoggerFactory _loggerFactory;

    // A 方案：SourceReader 自带硬解 + DXGI 出样所需的共享设备管理器（Singleton）。
    // 同样用 Lazy 包裹：provider 本身构造期不碰原生（开箱即用原则），但解析它会连带解析
    // IGpuDeviceContext（其实现可能在有头场景绑定渲染器设备）。用 Lazy 保持「注册 ≠ 立刻要 native」语义一致。
    private readonly Lazy<MfDxgiDeviceManagerProvider> _dxgiManagerProvider;

    // 纯 POCO 选项（AddMediaFoundation 时以 Singleton 注册），解析它不触碰任何原生 —— 不违背「开箱即用原则」。
    private readonly MediaFoundationOptions? _options;

    /// <summary>
    /// 初始化 <see cref="MFDemuxerFactory"/> 的新实例。
    /// </summary>
    public MFDemuxerFactory(
        Lazy<MFBackend> backendFactory,
        Lazy<MfDxgiDeviceManagerProvider> dxgiManagerProvider,
        ILoggerFactory loggerFactory,
        MediaFoundationOptions? options = null)
    {
        _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
        _dxgiManagerProvider = dxgiManagerProvider ?? throw new ArgumentNullException(nameof(dxgiManagerProvider));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options;
    }

    /// <inheritdoc/>
    public IMediaDemuxer Create(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new MFDemuxer(_backendFactory.Value, TryResolveDxgiManagerProvider(), _loggerFactory.CreateLogger<MFDemuxer>(), _options);
    }

    /// <inheritdoc/>
    public Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IMediaDemuxer>(
            new MFDemuxer(_backendFactory.Value, TryResolveDxgiManagerProvider(), _loggerFactory.CreateLogger<MFDemuxer>(), _options));
    }

    /// <summary>
    /// 解析 DXGI 设备管理器提供者；任何解析失败都降级为 <see langword="null"/>（demuxer 走软解兜底），绝不阻断打开。
    /// </summary>
    /// <remarks>非 Windows / 未注册 GPU 上下文等场景下解析可能抛出，此处一律吞掉——
    /// 硬解是可选增强，不该让「拿不到 GPU 设备」升级为「打不开媒体」。</remarks>
    private MfDxgiDeviceManagerProvider? TryResolveDxgiManagerProvider()
    {
        try
        {
            return _dxgiManagerProvider.Value;
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<MFDemuxerFactory>()
                .LogWarning(ex, "[MF-D3D] 解析 DXGI 设备管理器提供者失败 → SourceReader 走软解路径");
            return null;
        }
    }
}

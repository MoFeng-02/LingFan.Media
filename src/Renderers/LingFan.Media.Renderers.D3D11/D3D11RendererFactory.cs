using LingFan.Media.Renderers.Shared;

namespace LingFan.Media.Renderers.D3D11;

/// <summary>
/// <see cref="IVideoRendererFactory"/> 的 D3D11 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。持有共享的 <c>ID3D11Device</c> + <c>ID3D11DeviceContext</c>，
/// <see cref="Create"/> 返回<b>缓存单例</b> <see cref="D3D11Renderer"/>（共享 GPU Device 与唯一 SwapChain）。</para>
/// <para><b>缓存单例模式</b>：缓存单例消除"双实例/双 SwapChain"——同一工厂多次 <see cref="Create"/>
/// 返回同一渲染器实例。Core 管线（<see cref="VideoPipeline"/>）与 UI 层（D3D11GpuPresenter）
/// 通过同一工厂解析到同一渲染器，UI 层 Attach(HWND) 后管线 Present 即命中已附着实例，视频帧真正经 GPU 呈现。</para>
/// <para><b>单 HWND 限制</b>：单例渲染器一次仅能附加到<b>一个</b> HWND。同一应用内多个 VideoView 同时走 D3D11
/// 后端时，后附加者会抢占前者的 HWND（先 Detach 再 Attach），仅最后一个生效。单窗口单 VideoView 为设计目标场景。</para>
/// <para><b>单例重建</b>：若缓存实例已释放（如 MediaPlayer 释放后重开），<see cref="Create"/> 重建新实例，
/// 避免复用已释放的 SwapChain（共享设备仍复用）。</para>
/// <para>设备创建采用延迟初始化——首次 <see cref="Create"/> 时创建，
/// 避免在 DI 注册阶段（应用启动时）创建 GPU 设备。</para>
/// <para><see cref="Create"/> 为同步（config 分类）：手动 new + 共享设备引用，无 I/O。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class D3D11RendererFactory : IVideoRendererFactory, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<D3D11RendererFactory> _logger;
    private readonly object _deviceLock = new();
    private readonly object _singletonLock = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    // 缓存单例模式：缓存单例渲染器——同一工厂的多次 Create 返回同一实例。
    private D3D11Renderer? _singleton;
    private RenderContext? _renderContext;
    private bool _disposed;

    /// <summary>软帧/硬解帧缩放模式（契约层 <see cref="AspectRatioMode"/>）。默认 <see cref="AspectRatioMode.Uniform"/>（信箱）。</summary>
    public AspectRatioMode ScaleMode { get; set; } = AspectRatioMode.Uniform;

    /// <summary>
    /// 初始化 <see cref="D3D11RendererFactory"/> 的新实例。
    /// </summary>
    /// <param name="loggerFactory">日志工厂。</param>
    public D3D11RendererFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = _loggerFactory.CreateLogger<D3D11RendererFactory>();
    }

    /// <inheritdoc/>
    public IVideoRenderer Create()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDeviceCreated();
        lock (_singletonLock)
        {
            // 缓存单例模式：缓存单例。已释放则重建（共享设备仍复用），避免复用已释放的 SwapChain。
            if (_singleton is null || _singleton.IsDisposed)
            {
                _singleton = new D3D11Renderer(_device!, _context!, _loggerFactory.CreateLogger<D3D11Renderer>());
                _logger.LogDebug("D3D11 渲染器单例已创建（缓存复用）");
            }
            _singleton.ScaleMode = this.ScaleMode;
            return _singleton;
        }
    }

    /// <summary>
    /// 延迟创建 D3D11 设备和立即上下文（线程安全）。
    /// </summary>
    private void EnsureDeviceCreated()
    {
        if (_device is not null) return;

        lock (_deviceLock)
        {
            if (_device is not null) return; // double-check

            // 创建 D3D11 设备（硬件驱动 + BGRA 支持）
            // 注意：Vortice.Direct3D11.D3D11 是静态类，需完全限定名避免与本命名空间 D3D11 冲突
            // Vortice 便捷重载返回 ID3D11Device，通过 ImmediateContext 属性获取设备上下文
            // 创建 D3D11 设备（硬件驱动 + BGRA 支持；DXVA 零拷贝需 VideoSupport 标志）。
            // 优先带 VideoSupport（DXVA 硬解要求）；若创建失败（极少数无视频能力设备）则回退不含该标志，
            // 保证渲染仍可用（DXVA 会回落软解）。有头零拷贝依赖此共享设备带 VideoSupport。
            try
            {
                _device = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                    DriverType.Hardware,                        // 硬件驱动（Vortice.Direct3D 命名空间）
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "带 VideoSupport 的 D3D11 设备创建失败，回退不含 VideoSupport（渲染仍可用，DXVA 可能不可用）");
                _device = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport);
            }
            _context = _device.ImmediateContext;

            // 共享设备必须开启多线程保护。
            //    本设备同时被「硬解线程（FFmpeg D3D11VA / MF DXVA 写解码纹理）」与
            //    「呈现线程（CopySubresourceRegion + Present）」使用，而 ID3D11DeviceContext 非线程安全。
            //    FFmpeg 内部那把 d3d11va_default_lock 只保护它自己的调用，渲染器不参与 ⇒ 并发操作同一
            //    immediate context ⇒ 命令流交错（呈现错帧/半写入帧，表现为画面抽帧后跳场景）+ 驱动状态
            //    破坏 ⇒ 原生 AccessViolation。SetMultithreadProtected(TRUE) 由运行时对 context 调用加
            //    内部临界区，是共享设备做零拷贝硬解的硬性前提。
            //    QI 目标：ID3D11Multithread 实现在 immediate context 上（MSDN 明示经
            //    ID3D11DeviceContext::QueryInterface 获取）；部分驱动/运行时亦允许从 device 取到，
            //    故 context 失败时回退 device，两者皆失败仅告警（老设备无该接口，渲染仍可用）。
            bool mtProtected = D3D11MultithreadInterop.TryEnable(_context.NativePointer)
                               || D3D11MultithreadInterop.TryEnable(_device.NativePointer);
            if (mtProtected)
            {
                _logger.LogDebug("D3D11 共享设备已开启多线程保护（ID3D11Multithread.SetMultithreadProtected=TRUE）");
            }
            else
            {
                _logger.LogWarning(
                    "D3D11 设备不支持 ID3D11Multithread，无法开启多线程保护。" +
                    "硬解零拷贝（D3D11VA/DXVA）与呈现共用 immediate context 时存在竞态风险，" +
                    "可能出现错帧/花屏甚至驱动崩溃；建议改用软件解码。");
            }

            // 创建设备上下文（RenderContext 实现 IGpuDeviceContext）并注入 GPU 能力。
            // 能力查询失败不应阻断设备创建——降级为默认能力快照。
            try
            {
                _renderContext = new RenderContext(
                    GPUApiType.D3D11,
                    BuildCapabilities(),
                    _device.NativePointer,
                    _device,
                    _context!.NativePointer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "D3D11 设备能力查询失败，使用默认能力快照。");
                _renderContext = new RenderContext(
                    GPUApiType.D3D11,
                    new GpuDeviceCapabilities("Unknown", 0, 0, 16384, false, false, -1),
                    _device.NativePointer,
                    _device,
                    _context!.NativePointer);
            }

            _logger.LogDebug("D3D11 设备已创建（共享 Singleton）");
        }
    }

    /// <summary>
    /// 获取 D3D11 设备上下文（<see cref="RenderContext"/> 实现 <see cref="IGpuDeviceContext"/>）。
    /// 首次访问会确保共享设备已创建（延迟初始化）。
    /// </summary>
    /// <remarks>同步配置（config 分类）：设备创建为同步 native 调用，无 I/O await。</remarks>
    public RenderContext Context
    {
        get
        {
            EnsureDeviceCreated();
            return _renderContext!;
        }
    }

    /// <summary>
    /// 查询 D3D11 设备能力（FeatureLevel / 显存 / 名称等）。
    /// </summary>
    /// <remarks>同步 native 查询（DXGI 适配器枚举），无 I/O await → 同步（sync 分类）。</remarks>
    private GpuDeviceCapabilities BuildCapabilities()
    {
        var device = _device!;
        int featureLevel = (int)device.FeatureLevel;

        string name = "Unknown";
        ulong dedicated = 0;
        ulong shared = 0;
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
        var desc = adapter.Description;
        name = desc.Description;
        dedicated = (ulong)desc.DedicatedVideoMemory;
        shared = (ulong)desc.SharedSystemMemory;

        // D3D11 通用最大纹理尺寸（FeatureLevel 11_0+ 为 16384）。
        const int maxTexture = 16384;
        bool supportsCompute = featureLevel >= (int)FeatureLevel.Level_10_0;
        // DXVA2 在 FeatureLevel 11_0+ 普遍可用（启发式，避免引入视频接口依赖）。
        bool supportsHardwareDecode = featureLevel >= (int)FeatureLevel.Level_11_0;

        return new GpuDeviceCapabilities(name, dedicated, shared, maxTexture, supportsCompute, supportsHardwareDecode, featureLevel);
    }

    /// <summary>
    /// 释放共享 D3D11 设备、上下文与缓存的渲染器单例。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 先释放单例渲染器（Session 级资源），再释放共享设备/上下文，避免悬空引用。
        _singleton?.Dispose();
        _singleton = null;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;

        _logger.LogDebug("D3D11 设备已释放");
    }
}

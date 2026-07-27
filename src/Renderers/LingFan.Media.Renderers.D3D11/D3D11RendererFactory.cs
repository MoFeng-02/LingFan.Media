namespace LingFan.Media.Renderers.D3D11;

/// <summary>
/// <see cref="IVideoRendererFactory"/> 的 D3D11 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。持有共享的 <c>ID3D11Device</c> + <c>ID3D11DeviceContext</c>，
/// <see cref="Create"/> 返回<b>缓存单例</b> <see cref="D3D11Renderer"/>（共享 GPU Device 与唯一 SwapChain）。</para>
/// <para><b>方案 A（P0 修复）</b>：缓存单例消除"双实例/双 SwapChain"——同一工厂多次 <see cref="Create"/>
/// 返回同一渲染器实例（R1==R2）。Core 管线（<see cref="VideoPipeline"/>）与 UI 层（D3D11GpuPresenter）
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
    // 方案 A：缓存单例渲染器——同一工厂的多次 Create 返回同一实例（R1==R2）。
    private D3D11Renderer? _singleton;
    private bool _disposed;

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
            // 方案 A：缓存单例。已释放则重建（共享设备仍复用），避免复用已释放的 SwapChain。
            if (_singleton is null || _singleton.IsDisposed)
            {
                _singleton = new D3D11Renderer(_device!, _context!, _loggerFactory.CreateLogger<D3D11Renderer>());
                _logger.LogDebug("D3D11 渲染器单例已创建（缓存复用，R1==R2）");
            }
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
            _device = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                DriverType.Hardware,                        // 硬件驱动（Vortice.Direct3D 命名空间）
                DeviceCreationFlags.BgraSupport);            // BGRA 支持（SwapChain 需要）
            _context = _device.ImmediateContext;

            _logger.LogDebug("D3D11 设备已创建（共享 Singleton）");
        }
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

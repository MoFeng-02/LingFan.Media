namespace LingFan.Media.Renderers.D3D11;

/// <summary>
/// <see cref="IVideoRendererFactory"/> 的 D3D11 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。持有共享的 <c>ID3D11Device</c> + <c>ID3D11DeviceContext</c>，
/// 每次 <see cref="Create"/> 返回新 <see cref="D3D11Renderer"/>（共享 GPU Device，独立 SwapChain）。</para>
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
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
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
        return new D3D11Renderer(_device!, _context!, _loggerFactory.CreateLogger<D3D11Renderer>());
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
    /// 释放共享 D3D11 设备和上下文。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;

        _logger.LogDebug("D3D11 设备已释放");
    }
}

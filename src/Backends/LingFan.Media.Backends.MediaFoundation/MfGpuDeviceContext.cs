using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation;

/// <summary>
/// MediaFoundation 自备的窗口无关 D3D11 设备上下文（实现 Abstractions 中立契约 <see cref="IGpuDeviceContext"/>）。
/// </summary>
/// <remarks>
/// <para><b>用途</b>：无头模式下渲染器不注册 <c>IGpuDeviceContext</c>，MF 解码器需自备一个 D3D11 设备以驱动 DXVA 硬解。
/// 有头模式由 <c>AddD3D11Renderer</c> / GPU Presenter 注册的 <c>IGpuDeviceContext</c> 经 DI 胜出（同设备 → 零拷贝），
/// 本实现不被解析。</para>
/// <para><b>依赖倒置</b>：MF 与渲染器互不引用，仅经 <see cref="IGpuDeviceContext"/> 契约协作；本类是该契约在 MF 层的内部实现。</para>
/// <para><b>生命周期</b>：Singleton（由 <c>AddMediaFoundation</c> 经 <c>TryAddSingleton</c> 注册）。设备延迟创建，仅在 DXVA 实际启用时解析；
/// 共享同一设备供 N 路解码器统一使用（GPU 视频引擎解码，开销最优）。</para>
/// <para><b>AOT 兼容</b>：设备经原始 <see cref="MfDxvaInterop.D3D11CreateDevice"/> P/Invoke 创建，输出原生指针，无反射。</para>
/// <para>仅 Windows 可用。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class MfGpuDeviceContext : IGpuDeviceContext
{
    private readonly object _lock = new();
    private IntPtr _device;      // ID3D11Device*
    private IntPtr _context;     // ID3D11DeviceContext*
    private bool _created;
    private bool _disposed;

    /// <inheritdoc/>
    public GPUApiType ApiType => GPUApiType.D3D11;

    /// <inheritdoc/>
    public IntPtr DeviceHandle
    {
        get { EnsureDevice(); return _device; }
    }

    /// <inheritdoc/>
    public IntPtr ContextHandle
    {
        get { EnsureDevice(); return _context; }
    }

    /// <inheritdoc/>
    public bool IsInitialized
    {
        get { EnsureDevice(); return _created; }
    }

    private void EnsureDevice()
    {
        if (_device != IntPtr.Zero) return;
        lock (_lock)
        {
            if (_device != IntPtr.Zero) return; // double-check
            try
            {
                MfDxvaInterop.CreateD3D11Device(out _device, out _context);
                _created = _device != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                // 设备创建失败不致命：解码器将回退软件解码，IGpuDeviceContext 仅作能力查询用。
                _device = IntPtr.Zero;
                _context = IntPtr.Zero;
                _created = false;
                // 记录但不抛出——EnsureDevice 在属性 getter 中被调用，异常会冒泡到解码器初始化处被捕获。
                throw new InvalidOperationException("MF 自备 D3D11 设备创建失败（DXVA 不可用，回退软件解码）。", ex);
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：设备由 <see cref="EnsureDevice"/> 延迟创建（同步 native 调用，无 I/O await），返回 <see cref="Task.CompletedTask"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureDevice();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public GpuDeviceCapabilities GetCapabilities()
        // 最小快照：DXVA 由 MFT 直接查询设备能力，此处仅声明支持硬件解码。
        => new GpuDeviceCapabilities("MF-DXVA-Device", 0, 0, 16384, false, true, -1);

    /// <inheritdoc/>
    public object? SharedDevice => null;

    /// <inheritdoc/>
    public object? SharedPhysicalDevice => null;

    /// <inheritdoc/>
    public uint VideoQueueFamilyIndex => uint.MaxValue;

    /// <inheritdoc/>
    public uint GraphicsQueueFamilyIndex => uint.MaxValue;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context != IntPtr.Zero) { Marshal.Release(_context); _context = IntPtr.Zero; }
        if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
    }
}

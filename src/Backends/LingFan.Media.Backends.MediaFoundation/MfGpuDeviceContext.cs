using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation.Interop;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<MfGpuDeviceContext> _logger;
    private IntPtr _device;      // ID3D11Device*
    private IntPtr _context;     // ID3D11DeviceContext*
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="MfGpuDeviceContext"/> 的新实例。
    /// </summary>
    /// <param name="logger">日志（设备创建失败在此告警，不抛异常）。</param>
    public MfGpuDeviceContext(ILogger<MfGpuDeviceContext> logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc/>
    public GPUApiType ApiType => GPUApiType.D3D11;

    /// <inheritdoc/>
    /// <remarks>契约（<see cref="IGpuDeviceContext"/>）：无设备时返回 <see cref="IntPtr.Zero"/>。
    /// getter 不隐式创建设备——就绪由 <see cref="InitializeAsync"/> 或显式 <see cref="EnsureCreated"/> 完成。</remarks>
    public IntPtr DeviceHandle => _device;

    /// <inheritdoc/>
    /// <remarks>无上下文时返回 <see cref="IntPtr.Zero"/>；与 <see cref="DeviceHandle"/> 同步就绪。</remarks>
    public IntPtr ContextHandle => _context;

    /// <inheritdoc/>
    /// <remarks>设备已创建即为 <see langword="true"/>；getter 本身不触发创建。</remarks>
    public bool IsInitialized => _device != IntPtr.Zero;

    /// <summary>
    /// 创建自备 D3D11 设备（幂等，唯一创建点）。
    /// </summary>
    /// <returns>设备可用返回 <see langword="true"/>；创建失败返回 <see langword="false"/>。</returns>
    /// <remarks>设备创建失败不致命——解码器将回退软件解码，故此处只告警不抛异常：
    /// 本类仅作能力查询用，抛异常会冒泡打断解码器初始化。
    /// 已释放的实例（<see cref="Dispose"/> 之后）直接返回失败，不重建——重建会得到无人持有的新设备，
    /// 且解码器随后的调用只会落到已释放的上下文字面上。</remarks>
    internal bool EnsureCreated()
    {
        if (_disposed) return false;
        if (_device != IntPtr.Zero) return true;
        lock (_lock)
        {
            if (_disposed) return false;
            if (_device != IntPtr.Zero) return true; // double-check
            try
            {
                MfDxvaInterop.CreateD3D11Device(out _device, out _context);
            }
            catch (Exception ex)
            {
                _device = IntPtr.Zero;
                _context = IntPtr.Zero;
                _logger.LogWarning(ex, "MF 自备 D3D11 设备创建失败，DXVA 不可用，将回退软件解码。");
                return false;
            }

            if (_device == IntPtr.Zero)
            {
                _context = IntPtr.Zero;
                _logger.LogWarning("MF 自备 D3D11 设备创建返回空设备，DXVA 不可用，将回退软件解码。");
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 取 <see cref="IGpuDeviceContext"/> 的 D3D11 设备句柄，必要时先让 MF 自备上下文完成延迟创建。
    /// </summary>
    /// <param name="context">GPU 设备上下文（渲染器注册的共享设备或本类实例）。</param>
    /// <returns>ID3D11Device 指针；不可用时为 <see cref="IntPtr.Zero"/>。</returns>
    /// <remarks>getter 不再隐式创建（契约要求无设备返回零句柄），故取句柄的调用方经此显式就绪。</remarks>
    internal static IntPtr ResolveDeviceHandle(IGpuDeviceContext context)
    {
        if (context is MfGpuDeviceContext mf) mf.EnsureCreated();
        return context.DeviceHandle;
    }

    /// <inheritdoc/>
    /// <remarks>异步外观、同步实现：设备创建是 native 同步调用（无 I/O 可等待），故在调用线程直接完成并返回
    /// 已完成的 <see cref="Task.CompletedTask"/>，不用 <c>Task.Run</c> 伪装异步。
    /// 创建失败不抛异常——解码器据零句柄回退软件解码；唯一会抛的是已取消的 <paramref name="ct"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureCreated();
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
    /// <remarks>整个释放序列（置已释放标志 + 释放两个句柄）在同一临界区内完成，与 <see cref="EnsureCreated"/>
    /// 的锁内「判标志 → 建设备 → 赋句柄」互斥。若把置位留在锁外，创建线程可能已通过标志检查、随后把新建设备
    /// 写入 <c>_device</c>，而此时 <c>_disposed</c> 已为真、再无调用方释放它——设备句柄永久泄漏。</remarks>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_context != IntPtr.Zero) { Marshal.Release(_context); _context = IntPtr.Zero; }
            if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
        }
    }
}

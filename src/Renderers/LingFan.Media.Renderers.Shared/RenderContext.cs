namespace LingFan.Media.Renderers.Shared;

/// <summary>
/// 渲染上下文。持有 GPU 设备/上下文共享信息，并实现 <see cref="IGpuDeviceContext"/> 中立契约。
/// </summary>
/// <remarks>
/// <para>由 <see cref="IVideoRendererFactory"/>（如 <c>D3D11RendererFactory</c>）持有（Singleton 共享 GPU Device），
/// SwapChain / CommandQueue 是 Session 级（每个 Renderer 实例独立）。</para>
/// <para>V2 扩展：实现 <see cref="IGpuDeviceContext"/>（Abstractions 中立桥），暴露完整设备能力查询，
/// 供 Avalonia / Outputs 等层查询 GPU 能力而无需引用具体渲染器模块（依赖倒置严守）。</para>
/// <para><b>能力注入</b>：设备能力（<see cref="GpuDeviceCapabilities"/>）由具体渲染器工厂在设备创建时查询并注入，
/// 本类（Renderers.Shared）不依赖任何具体 GPU API 库（零 Vortice 依赖、AOT 友好）。</para>
/// <para><b>异步策略</b>：<see cref="IGpuDeviceContext.InitializeAsync"/> 为接口契约——共享设备由工厂创建并注入，
/// 本方法无真实 I/O await，返回 <see cref="Task.CompletedTask"/>（非伪异步，无隐藏阻塞）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class RenderContext : IGpuDeviceContext
{
    /// <summary>GPU API 类型。</summary>
    public GPUApiType GpuApiType { get; }

    /// <summary>共享 GPU 设备对象（运行时显式 cast，如 ID3D11Device / VkDevice）。</summary>
    public object? SharedDevice { get; }

    /// <summary>GPU 设备能力（由工厂注入，纯内存快照）。</summary>
    public GpuDeviceCapabilities Capabilities { get; }

    /// <summary>GPU 设备原生句柄（如 ID3D11Device* 指针）。无设备时为 <see cref="IntPtr.Zero"/>。</summary>
    public IntPtr DeviceHandle { get; }

    /// <summary>GPU 设备上下文原生句柄（如 ID3D11DeviceContext* 指针）。无上下文时为 <see cref="IntPtr.Zero"/>。</summary>
    public IntPtr ContextHandle { get; }

    /// <summary>
    /// 初始化 <see cref="RenderContext"/> 的新实例。
    /// </summary>
    /// <param name="gpuApiType">GPU API 类型。</param>
    /// <param name="capabilities">GPU 设备能力（必填，不可为 null）。</param>
    /// <param name="deviceHandle">GPU 设备原生句柄。</param>
    /// <param name="sharedDevice">共享 GPU 设备对象（可为 null）。</param>
    /// <param name="contextHandle">GPU 设备上下文原生句柄（D3D11VA 硬解需要）。</param>
    public RenderContext(
        GPUApiType gpuApiType,
        GpuDeviceCapabilities capabilities,
        IntPtr deviceHandle,
        object? sharedDevice = null,
        IntPtr contextHandle = default)
    {
        GpuApiType = gpuApiType;
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        DeviceHandle = deviceHandle;
        ContextHandle = contextHandle;
        SharedDevice = sharedDevice;
    }

    // ── IGpuDeviceContext 实现（接口契约，无真实 I/O）──

    /// <inheritdoc/>
    GPUApiType IGpuDeviceContext.ApiType => GpuApiType;

    /// <inheritdoc/>
    IntPtr IGpuDeviceContext.DeviceHandle => DeviceHandle;

    /// <inheritdoc/>
    IntPtr IGpuDeviceContext.ContextHandle => ContextHandle;

    /// <inheritdoc/>
    bool IGpuDeviceContext.IsInitialized => SharedDevice is not null;

    /// <inheritdoc/>
    Task IGpuDeviceContext.InitializeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask; // 设备由工厂创建并注入，无 I/O
    }

    /// <inheritdoc/>
    GpuDeviceCapabilities IGpuDeviceContext.GetCapabilities() => Capabilities;
}

namespace LingFan.Media.Abstractions;

/// <summary>
/// 中立 GPU 设备上下文契约（跨层共享，零外部引用）。
/// </summary>
/// <remarks>
/// <para>作为 Abstractions 中立桥，供 Avalonia / Outputs 等层查询 GPU 能力或获取纹理句柄，
/// 而无需引用具体渲染器模块（如 Renderers.D3D11），严守依赖倒置。</para>
/// <para><b>异步策略</b>：<see cref="InitializeAsync"/> 为接口契约——当底层共享 GPU 设备已由
/// 工厂（如 <c>D3D11RendererFactory</c>）创建、本方法仅做能力查询（同步 COM 调用）<b>无真实 I/O await</b> 时，
/// 实现返回 <see cref="Task.CompletedTask"/>（非伪异步，因无隐藏阻塞）。若某后端实现中确需异步初始化，
/// 则一路 <c>await</c> 实现真异步。判断口诀：方法体有无真实 await → 有则 async，无则 Task.CompletedTask。</para>
/// </remarks>
public interface IGpuDeviceContext
{
    /// <summary>GPU API 类型。</summary>
    GPUApiType ApiType { get; }

    /// <summary>共享 GPU 设备原生句柄（如 ID3D11Device* 的指针）。无设备时为 <see cref="IntPtr.Zero"/>。</summary>
    IntPtr DeviceHandle { get; }

    /// <summary>
    /// 共享 GPU 设备上下文原生句柄（如 ID3D11DeviceContext* 指针）。
    /// 用于硬件解码器初始化（D3D11VA 需要 device + device_context）。无上下文时为 <see cref="IntPtr.Zero"/>。
    /// </summary>
    IntPtr ContextHandle { get; }

    /// <summary>
    /// 共享 GPU 设备对象（运行时显式 cast，如 ID3D11Device / Device(Vulkan)）。
    /// 供解码器复用渲染器共享设备进行零拷贝；无设备时为 <see langword="null"/>。
    /// </summary>
    object? SharedDevice { get; }

    /// <summary>
    /// 共享 GPU 物理设备对象（Vulkan 等需「同物理设备对齐」零拷贝时提供，盒装 PhysicalDevice）。
    /// 非 Vulkan 后端为 <see langword="null"/>。
    /// </summary>
    object? SharedPhysicalDevice { get; }

    /// <summary>
    /// 视频解码队列族索引（对应 <c>VK_QUEUE_VIDEO_DECODE_BIT_KHR</c>）。
    /// 设备创建时启用了 video-decode 扩展且存在该队列族才非 <see cref="uint.MaxValue"/>；否则为 <see cref="uint.MaxValue"/>（解码器回落软件解码）。
    /// </summary>
    uint VideoQueueFamilyIndex { get; }

    /// <summary>
    /// 图形渲染队列族索引（对应 <c>VK_QUEUE_GRAPHICS_BIT</c>）。
    /// Vulkan 硬解 DPB 图像需在「video-decode 队列族」与「图形队列族」之间共享（跨队列族读写零拷贝上屏），
    /// 此值供解码器在创建 DPB 图像时正确设置 <c>VK_SHARING_MODE_CONCURRENT</c> 的队列族列表。
    /// 仅 Vulkan 后端有意义；非 Vulkan 后端或单一队列族时为 <see cref="uint.MaxValue"/>（解码器退化为同族独占）。
    /// </summary>
    uint GraphicsQueueFamilyIndex { get; }

    /// <summary>设备是否已初始化（共享设备已创建）。</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// 确保 GPU 设备上下文就绪（绑定设备 / 查询能力）。
    /// 接口契约：仅做同步 COM 能力查询时返回 <see cref="Task.CompletedTask"/>（非伪异步）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>设备就绪的任务（实现无 I/O 时即 <see cref="Task.CompletedTask"/>）。</returns>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>获取 GPU 设备能力（纯内存查询，同步消费）。</summary>
    /// <returns>设备能力快照。</returns>
    GpuDeviceCapabilities GetCapabilities();
}

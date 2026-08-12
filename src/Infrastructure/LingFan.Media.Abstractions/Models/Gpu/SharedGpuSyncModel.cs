namespace LingFan.Media.Abstractions;

/// <summary>
/// 共享 GPU 表面的同步模型（中立枚举，跨 GPU API）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：不同 GPU API 的跨设备共享纹理有各自原生的同步原语——
/// D3D11/DXGI 用 keyed mutex，Vulkan 用信号量（<c>VkSemaphore</c>，经外部句柄导出）。
/// 若契约只表达其中一种，另一种后端就被迫跨 API 互操作或伪造句柄类型。
/// 本枚举让<b>每个后端自报</b>它使用的同步模型，消费方（UI 合成器）据此选择对应的提交方式，
/// 从而各后端只用自己 API 的原生机制，互不跨界。</para>
/// <para><b>扩展方式</b>：新增同步模型时在此追加成员，并在消费侧的一次 switch 中补齐分派分支。</para>
/// </remarks>
public enum SharedGpuSyncMode
{
    /// <summary>
    /// keyed mutex：以整数键轮流持有表面（DXGI 共享纹理原生机制）。
    /// 键值由 <see cref="ISharedGpuSurfaceSource.ConsumerAcquireKey"/> /
    /// <see cref="ISharedGpuSurfaceSource.ConsumerReleaseKey"/> 给出。
    /// </summary>
    KeyedMutex = 0,

    /// <summary>
    /// 信号量：生产者写完后 signal，消费者 wait 后采样、采样完 signal 交还（Vulkan 原生机制）。
    /// 句柄由 <see cref="ISharedGpuSurfaceSource.Semaphores"/> 给出。
    /// </summary>
    Semaphores = 1,

    /// <summary>
    /// 无显式同步：由底层驱动/合成器隐式保证顺序。
    /// 仅在平台明确保证时使用（存在撕裂风险，非首选）。
    /// </summary>
    None = 2,
}

/// <summary>
/// 共享 GPU 信号量的句柄类型（中立枚举）。
/// </summary>
/// <remarks>与宿主 UI 框架的「外部信号量句柄类型」一一对应，但本枚举不引用任何 UI 框架或 GPU 库。</remarks>
public enum SharedGpuSemaphoreKind
{
    /// <summary>Vulkan 外部信号量 NT 句柄（Windows）。</summary>
    VulkanOpaqueNtHandle = 0,

    /// <summary>Vulkan 外部信号量 POSIX 文件描述符（Linux/Android）。</summary>
    VulkanOpaquePosixFileDescriptor = 1,

    /// <summary>Apple MTLSharedEvent（经 <c>VK_EXT_metal_objects</c> 从 Vulkan 信号量导出，仅 macOS/iOS / MoltenVK）。</summary>
    MetalSharedEvent = 2,
}

/// <summary>
/// 共享 GPU 表面的信号量对（中立值对象）。用于 <see cref="SharedGpuSyncMode.Semaphores"/> 模型。
/// </summary>
/// <param name="ConsumerWaitHandle">
/// 消费方<b>等待</b>的信号量句柄——由生产者在写完一帧后 signal，表示「表面内容已就绪，可以采样」。
/// </param>
/// <param name="ConsumerSignalHandle">
/// 消费方<b>发信</b>的信号量句柄——由消费方在采样完成后 signal，表示「表面已归还，生产者可覆写」。
/// </param>
/// <param name="Kind">句柄类型。</param>
/// <remarks>
/// <para><b>生命周期</b>：信号量是<b>长期对象</b>，跨帧复用，随
/// <see cref="ISharedGpuSurfaceSource"/> 一同创建与释放；消费方只需导入一次。</para>
/// <para><b>纯数据</b>：不持有所有权，不可释放。</para>
/// </remarks>
public readonly record struct SharedGpuSemaphorePair(
    IntPtr ConsumerWaitHandle,
    IntPtr ConsumerSignalHandle,
    SharedGpuSemaphoreKind Kind)
{
    /// <summary>两个句柄是否均有效。</summary>
    public bool IsValid => ConsumerWaitHandle != IntPtr.Zero && ConsumerSignalHandle != IntPtr.Zero;
}

/// <summary>
/// 目标 GPU 适配器身份（中立值对象）。由消费方（宿主合成器）提供，供生产者选择<b>同一物理设备</b>。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：跨设备共享纹理的导入要求生产者与合成器位于同一物理 GPU
/// （多 GPU 机器上尤为关键）。若生产者自行按偏好评分选设备，可能选中与合成器不同的适配器，
/// 导致导入失败或静默黑屏。消费方把自身适配器身份透传给工厂，生产者据此优选匹配设备，
/// 无法匹配时应抛 <see cref="NotSupportedException"/> 让调用方干净回退。</para>
/// <para><b>字段来源</b>：<see cref="DeviceUuid"/> 对应 Vulkan
/// <c>VkPhysicalDeviceIDProperties.deviceUUID</c>（16 字节）；
/// <see cref="DeviceLuid"/> 对应 DXGI 适配器 LUID（8 字节）。
/// 宿主可能只提供其中之一，甚至都不提供（此时生产者按自身偏好选择）。</para>
/// </remarks>
public sealed class SharedGpuAdapterIdentity
{
    /// <summary>身份未知（宿主未提供任何标识）。生产者应按自身偏好选择设备。</summary>
    public static SharedGpuAdapterIdentity Unknown { get; } = new(default, default);

    /// <summary>初始化 <see cref="SharedGpuAdapterIdentity"/> 的新实例。</summary>
    /// <param name="deviceUuid">Vulkan 物理设备 UUID（16 字节）；未知时传 <see langword="default"/>。</param>
    /// <param name="deviceLuid">DXGI 适配器 LUID（8 字节）；未知时传 <see langword="default"/>。</param>
    public SharedGpuAdapterIdentity(ReadOnlyMemory<byte> deviceUuid, ReadOnlyMemory<byte> deviceLuid)
    {
        DeviceUuid = deviceUuid;
        DeviceLuid = deviceLuid;
    }

    /// <summary>Vulkan 物理设备 UUID（16 字节）。长度不为 16 视为未提供。</summary>
    public ReadOnlyMemory<byte> DeviceUuid { get; }

    /// <summary>DXGI 适配器 LUID（8 字节）。长度不为 8 视为未提供。</summary>
    public ReadOnlyMemory<byte> DeviceLuid { get; }

    /// <summary>是否提供了可用的 Vulkan 设备 UUID。</summary>
    public bool HasDeviceUuid => DeviceUuid.Length == 16;

    /// <summary>是否提供了可用的 DXGI 适配器 LUID。</summary>
    public bool HasDeviceLuid => DeviceLuid.Length == 8;

    /// <summary>是否未提供任何标识。</summary>
    public bool IsUnknown => !HasDeviceUuid && !HasDeviceLuid;

    /// <summary>判断给定的 16 字节 UUID 是否与本身份一致。</summary>
    /// <param name="candidate">候选设备 UUID。</param>
    /// <returns>本身份未提供 UUID 时返回 <see langword="false"/>（调用方应视为「无法确认」而非「不匹配」）。</returns>
    public bool MatchesDeviceUuid(ReadOnlySpan<byte> candidate)
        => HasDeviceUuid && candidate.Length == 16 && DeviceUuid.Span.SequenceEqual(candidate);

    /// <summary>判断给定的 8 字节 LUID 是否与本身份一致。</summary>
    /// <param name="candidate">候选适配器 LUID。</param>
    /// <returns>本身份未提供 LUID 时返回 <see langword="false"/>。</returns>
    public bool MatchesDeviceLuid(ReadOnlySpan<byte> candidate)
        => HasDeviceLuid && candidate.Length == 8 && DeviceLuid.Span.SequenceEqual(candidate);
}

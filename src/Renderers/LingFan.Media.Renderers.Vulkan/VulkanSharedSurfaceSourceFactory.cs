using LingFan.Media.Renderers.Shared;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// <see cref="ISharedGpuSurfaceSource"/> 的 Vulkan 实现工厂（中立契约的 Vulkan 适配器入口）。
/// </summary>
/// <remarks>
/// <para>作为「GPU 适配层」的一部分注册到 DI：UI 层（CompositionVideoRenderer）遍历
/// <c>IEnumerable&lt;ISharedGpuSurfaceSourceFactory&gt;</c>，选中首个 <see cref="IsAvailable"/>
/// 且句柄类型被宿主合成器支持的工厂，从而 UI 层不含任何「优先 D3D11 / 其次 Vulkan」硬编码分支。</para>
/// <para>产出句柄类型：Windows=<see cref="SharedGpuHandleKind.VulkanOpaqueNtHandle"/>，
/// Linux/Android=<see cref="SharedGpuHandleKind.VulkanOpaquePosixFileDescriptor"/>——
/// 与「Vulkan 渲染 Vulkan 的」宪法一致：本源产出<b>自身</b>的 Vulkan 外部句柄，不跨界伪造 D3D11 句柄。</para>
/// <para><see cref="IsAvailable"/> 为轻量平台判定（Windows / Linux / Android），不触碰原生资源；
/// 真正的设备/纹理创建延迟到 <see cref="Create"/>（若共享 Vulkan 设备尚未就绪则在此创建）。</para>
/// <para>同设备对齐：<see cref="Create"/> 按消费方透传的 <see cref="SharedGpuAdapterIdentity"/> 优选匹配设备，
/// 无法匹配时抛 <see cref="NotSupportedException"/> 干净回退到下一个工厂。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class VulkanSharedSurfaceSourceFactory : ISharedGpuSurfaceSourceFactory
{
    private readonly VulkanRendererFactory _rendererFactory;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="VulkanSharedSurfaceSourceFactory"/> 的新实例。
    /// </summary>
    /// <param name="rendererFactory">Vulkan 渲染器工厂（持有共享 Vulkan 设备与设备身份）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public VulkanSharedSurfaceSourceFactory(VulkanRendererFactory rendererFactory, ILoggerFactory loggerFactory)
    {
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public SharedGpuHandleKind HandleKind =>
        OperatingSystem.IsWindows()
            ? SharedGpuHandleKind.VulkanOpaqueNtHandle
            : SharedGpuHandleKind.VulkanOpaquePosixFileDescriptor;

    /// <inheritdoc/>
    public bool IsAvailable =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">当前环境无法创建 Vulkan 共享表面源时（调用方应回退下一个工厂）。</exception>
    public ISharedGpuSurfaceSource Create(SharedGpuAdapterIdentity? targetAdapter = null)
    {
        // 触发共享设备延迟创建（若尚未创建则返回缓存实例）；同时填充设备身份与扩展可用性。
        RenderContext ctx = _rendererFactory.Context;
        if (ctx.GpuApiType != GPUApiType.Vulkan)
            throw new NotSupportedException("Vulkan 共享表面源需要已初始化的 Vulkan 共享设备（当前不可用）。");
        if (!_rendererFactory.ExternalSharingEnabled)
            throw new NotSupportedException(
                "Vulkan 外部内存/信号量导出扩展不可用，无法创建 no-airspace 共享表面源（请确认 VK_KHR_external_memory*/external_semaphore* 已启用）。");

        // 同设备对齐：消费方透传的适配器身份必须与本工厂所选 Vulkan 物理设备一致，
        // 否则跨设备导入外部内存/信号量会失败或静默黑屏——干净回退下一个工厂。
        if (targetAdapter is { } identity)
        {
            if (identity.HasDeviceUuid && !identity.MatchesDeviceUuid(_rendererFactory.PhysicalDeviceUuid.Span))
                throw new NotSupportedException("目标适配器 UUID 与 Vulkan 物理设备不匹配，无法创建 no-airspace 共享表面源。");
            if (identity.HasDeviceLuid && !identity.MatchesDeviceLuid(_rendererFactory.PhysicalDeviceLuid.Span))
                throw new NotSupportedException("目标适配器 LUID 与 Vulkan 物理设备不匹配，无法创建 no-airspace 共享表面源。");
        }

        return new VulkanSharedSurfaceSource(
            _rendererFactory, _loggerFactory.CreateLogger<VulkanSharedSurfaceSource>());
    }
}

using LingFan.Media.GPUShare.Vulkan;
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
/// 与「Vulkan 渲染 Vulkan 的」架构原则一致：本源产出<b>自身</b>的 Vulkan 外部句柄，不跨界伪造 D3D11 句柄。</para>
/// <para><see cref="IsAvailable"/> 为轻量平台判定（Windows / Linux / Android），不触碰原生资源；
/// 真正的设备/纹理创建延迟到 <see cref="Create"/>（若共享 Vulkan 设备尚未就绪则在此创建）。</para>
/// <para>平台范围说明：Vulkan 后端经 MoltenVK 已在 macOS/iOS 启用有头路径（SwapChain 初始化与 Surface 创建）。
    /// <b>无空域零拷贝生产者（本源）已在 Apple 就绪</b>：经 <c>VK_EXT_metal_objects</c> 把 Vulkan 离屏图像导出为
    /// <see cref="SharedGpuHandleKind.IOSurfaceRef"/>、信号量导出为 <see cref="SharedGpuSemaphoreKind.MetalSharedEvent"/>
    /// （MTLSharedEvent），句柄类型在 Apple 上对应 <c>IOSurfaceRef</c>。
    /// <b>消费侧已收尾（第二类）</b>：<c>CompositionVideoRenderer</c> 的 <c>MapSemaphoreKind</c> 已分派
    /// <c>MetalSharedEvent</c>（Avalonia <c>KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent</c>），
    /// 跨线程同步沿用既有的 <c>UpdateWithSemaphoresAsync</c> 信号量握手（与 Windows / Linux 同模型）。
    /// <see cref="IsAvailable"/> 现已包含 Apple（平台级判定，不触碰原生资源），真实能力由 <see cref="Create"/>
    /// 按 <c>MetalObjectsSharingEnabled</c> 把关；若 <c>VK_EXT_metal_objects</c> 不可用则 <see cref="Create"/> 抛
    /// <see cref="NotSupportedException"/> 干净回退 Skia。<b>剩余验收</b>：须 Mac 本机运行期验证
    /// （IOSurface 导出 + MTLSharedEvent 握手 + Avalonia 合成上屏无撕裂）。</para>
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
            : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()
                ? SharedGpuHandleKind.IOSurfaceRef
                : OperatingSystem.IsAndroid()
                    ? SharedGpuHandleKind.AndroidHardwareBuffer
                    : SharedGpuHandleKind.VulkanOpaquePosixFileDescriptor;

    /// <inheritdoc/>
    /// <remarks>平台级判定（不触碰原生资源）：Windows / Linux / Android / macOS / iOS 均放行；
    /// 真实能力由 <see cref="Create"/> 把关——Apple 须 <c>MetalObjectsSharingEnabled</c>（VK_EXT_metal_objects），
    /// 其余平台须 <c>ExternalSharingEnabled</c>（VK_KHR_external_memory*）。能力不满足时 Create 抛
    /// NotSupportedException，由调用方回退下一个工厂 / Skia。</remarks>
    public bool IsAvailable =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()
        || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">当前环境无法创建 Vulkan 共享表面源时（调用方应回退下一个工厂）。</exception>
    public ISharedGpuSurfaceSource Create(SharedGpuAdapterIdentity? targetAdapter = null)
    {
        // 触发共享设备延迟创建（若尚未创建则返回缓存实例）；同时填充设备身份与扩展可用性。
        RenderContext ctx = _rendererFactory.Context;
        if (ctx.GpuApiType != GPUApiType.Vulkan)
            throw new NotSupportedException("Vulkan 共享表面源需要已初始化的 Vulkan 共享设备（当前不可用）。");
        // 导出扩展可用性：Apple / MoltenVK 走 VK_EXT_metal_objects（IOSurface / MTLSharedEvent），
        // 不依赖 external_memory / external_semaphore 扩展；其余平台仍要求 VK_KHR_external_memory*。
        bool sharingOk = (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            ? _rendererFactory.MetalObjectsSharingEnabled
            : OperatingSystem.IsAndroid()
                ? VulkanNative.HasAndroidHardwareBufferProperties
                : _rendererFactory.ExternalSharingEnabled;
        if (!sharingOk)
            throw new NotSupportedException(
                "Vulkan 共享表面源所需的导出扩展不可用，无法创建 no-airspace 共享表面源"
                + "（Apple 需 VK_EXT_metal_objects；Windows/Linux 需 VK_KHR_external_memory*/external_semaphore* 已启用）。");

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

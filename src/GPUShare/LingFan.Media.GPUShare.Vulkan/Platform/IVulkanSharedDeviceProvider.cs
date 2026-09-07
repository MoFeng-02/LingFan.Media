namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// 宿主共享 Vulkan device 提供者（治根BA，2026-09-02）。
/// 宿主（如 Avalonia Android 入口）自建 VkInstance/VkDevice 并注入 Avalonia 后，经本接口把同一组
/// 句柄提供给 <c>LingFan.Media.Renderers.Vulkan</c> 的渲染器工厂，使视频共享表面源与宿主处于<b>同一 device</b> ——
/// 共享表面源的 dma_buf fd 导入从「跨实例」变为「同 device」（驱动内部路径），
/// 根治 Adreno 跨实例 OPAQUE_FD 导入缺陷（vkAllocateMemory 报 INITIALIZATION_FAILED）。
/// </summary>
/// <remarks>
/// <para><b>放置说明</b>：本接口是 Vulkan 渲染后端的宿主协作细节，<b>不属于契约层</b>
/// （Abstractions 只放媒体抽象，平台桥接细节归宿染后端——2026-09-02 宪法审计纠正）。</para>
/// <para>全部为 <c>nint</c> 原生句柄（零托管包装、AOT 安全）；实现方须保证句柄在渲染器生命周期内有效，
/// 且 device/instance 的销毁由实现方负责（渲染器不销毁外部资源）。</para>
/// </remarks>
public interface IVulkanSharedDeviceProvider
{
    /// <summary>获取共享 device 的全部句柄。</summary>
    VulkanSharedDeviceHandles GetSharedDevice();
}

/// <summary>共享 Vulkan device 的原生句柄组（治根BA）。</summary>
/// <param name="InstanceHandle">VkInstance 句柄。</param>
/// <param name="PhysicalDeviceHandle">VkPhysicalDevice句柄。</param>
/// <param name="DeviceHandle">VkDevice 句柄。</param>
/// <param name="GraphicsQueueFamilyIndex">图形队列族索引。</param>
public readonly record struct VulkanSharedDeviceHandles(
    nint InstanceHandle,
    nint PhysicalDeviceHandle,
    nint DeviceHandle,
    uint GraphicsQueueFamilyIndex);

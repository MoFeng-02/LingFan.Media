using System.Runtime.Versioning;
using LingFan.Media.GPUShare.Vulkan;

namespace LingFan.Media.Avalonia.Android;

/// <summary>
/// 共享 Vulkan device 引导器：创建 instance/device 并以 <see cref="IVulkanSharedDeviceProvider"/>
/// 形式交付给渲染层（<c>VulkanRendererFactory.UseExternalDevice</c>）与 Avalonia
/// （<c>VulkanOptions.CustomSharedDevice</c>），使两者共用同一 VkDevice。
/// </summary>
/// <remarks>
/// 生命周期：App 级静态（与进程一致），适配器由本类保活防 GC 悬空。
/// 必须在 Avalonia 初始化之前调用 <see cref="Initialize"/>（CustomSharedDevice 仅在 AppBuilder 阶段生效）。
/// </remarks>
[SupportedOSPlatform("android23.0")]
public sealed class VulkanSharedDeviceBootstrap : IVulkanSharedDeviceProvider
{
    /// <summary>已创建的 Avalonia 实例适配器（未初始化为 null）。</summary>
    public static AvaloniaVulkanInstanceAdapter? InstanceAdapter { get; private set; }

    /// <summary>已创建的 Avalonia 设备适配器（未初始化为 null）。</summary>
    public static AvaloniaVulkanDeviceAdapter? DeviceAdapter { get; private set; }

    /// <summary>单例（注册 DI <see cref="IVulkanSharedDeviceProvider"/> 用）。</summary>
    public static VulkanSharedDeviceBootstrap Instance { get; } = new();

    /// <summary>创建 Vulkan instance/device（幂等：已初始化则无操作）。</summary>
    public static void Initialize()
    {
        if (DeviceAdapter is not null)
            return;
        (InstanceAdapter, DeviceAdapter) = VulkanDeviceFactory.Create();
    }

    /// <inheritdoc />
    public VulkanSharedDeviceHandles GetSharedDevice() =>
        new(InstanceAdapter!.Handle, DeviceAdapter!.PhysicalDeviceHandle,
            DeviceAdapter.Handle, DeviceAdapter.GraphicsQueueFamilyIndex);
}

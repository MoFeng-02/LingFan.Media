namespace LingFan.Media.Renderers.Vulkan.SafeHandles;

/// <summary>
/// Vulkan 图像的 SafeHandle 桩。
/// </summary>
/// <remarks>
/// V1 桩实现——Vulkan 渲染器尚未实现（Phase 2 目标）。
/// 未来实现时封装 VkImage + VkDeviceMemory + VkDevice。
/// </remarks>
internal sealed class SafeVulkanImageHandle : SafeHandle
{
    public SafeVulkanImageHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle() => true;
}

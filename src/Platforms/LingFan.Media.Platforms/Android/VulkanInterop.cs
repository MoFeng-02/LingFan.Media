namespace LingFan.Media.Platforms.Android;

/// <summary>
/// Android Vulkan 互操作。桩实现。
/// </summary>
/// <remarks>
/// <para>职责：将 MediaCodec 输出的 AHardwareBuffer 导入为 Vulkan VkImage，
/// 供 VulkanRenderer 零拷贝渲染。</para>
/// <para><b>GPU 零拷贝路径</b>：
/// MediaCodec → AHardwareBuffer → VkImage → VulkanRenderer → Swapchain → TextureView（SurfaceFlinger 合成）→ Display</para>
/// <para>桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// Android Vulkan 互操作属 Phase 2-3 目标。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——Vulkan API 调用是同步边界，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class VulkanInterop
{
    /// <summary>
    /// 从 AHardwareBuffer 创建 VkImage（Android 硬解零拷贝路径）。
    /// </summary>
    /// <param name="vkDevice">VkDevice 句柄。</param>
    /// <param name="hardwareBuffer">AHardwareBuffer 句柄（来自 MediaCodec 输出）。</param>
    /// <param name="width">图像宽度。</param>
    /// <param name="height">图像高度。</param>
    /// <returns>VkImage 句柄 + VkDeviceMemory 句柄。</returns>
    public (nint vkImage, nint vkMemory) CreateVkImageFromAHardwareBuffer(
        nint vkDevice, nint hardwareBuffer, int width, int height)
        => throw new NotSupportedException("Android Vulkan 互操作尚未实现。Phase 2-3 目标。");

    /// <summary>
    /// 导入 AHardwareBuffer 为 Vulkan 外部内存（Vulkan VK_ANDROID_external_memory_android_hardware_buffer 扩展）。
    /// </summary>
    /// <param name="vkDevice">VkDevice 句柄。</param>
    /// <param name="hardwareBuffer">AHardwareBuffer 句柄。</param>
    /// <returns>VkDeviceMemory 句柄。</returns>
    public nint ImportAHardwareBuffer(nint vkDevice, nint hardwareBuffer)
        => throw new NotSupportedException("AHardwareBuffer 导入尚未实现。");

    /// <summary>
    /// 获取 AHardwareBuffer 的格式和用途（用于 VkImage 创建参数匹配）。
    /// </summary>
    /// <param name="hardwareBuffer">AHardwareBuffer 句柄。</param>
    /// <returns>格式（int） + 用途（uint）。</returns>
    public (int format, uint usage) DescribeAHardwareBuffer(nint hardwareBuffer)
        => throw new NotSupportedException("AHardwareBuffer 描述尚未实现。");
}

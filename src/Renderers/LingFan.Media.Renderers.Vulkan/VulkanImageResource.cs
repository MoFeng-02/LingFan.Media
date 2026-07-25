namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan 图像帧资源。实现 <see cref="IFrameResource"/>。
/// </summary>
/// <remarks>
/// V1 桩实现——Vulkan 渲染器尚未实现（Phase 2 目标）。
/// 未来实现时封装 VkImage + VkDeviceMemory，支持 Windows/Linux/Android 跨平台 GPU 零拷贝。
/// </remarks>
public sealed class VulkanImageResource : IFrameResource
{
    /// <inheritdoc/>
    public int Width => throw new NotSupportedException("Vulkan 渲染器尚未实现。");

    /// <inheritdoc/>
    public int Height => throw new NotSupportedException("Vulkan 渲染器尚未实现。");

    /// <inheritdoc/>
    public PixelFormat Format => throw new NotSupportedException("Vulkan 渲染器尚未实现。");

    /// <summary>释放资源（桩——无资源可释放）。</summary>
    public void Dispose() { }
}

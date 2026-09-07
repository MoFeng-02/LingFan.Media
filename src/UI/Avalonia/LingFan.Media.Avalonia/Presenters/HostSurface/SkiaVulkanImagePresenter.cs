using System.Diagnostics.CodeAnalysis;
using SkiaSharp;

namespace LingFan.Media.Avalonia;

/// <summary>
/// Vulkan 原生图像的 Skia 直采适配器：把 <see cref="SharedGpuHandleKind.VulkanNativeImage"/>
/// 描述符包装为 <see cref="GRBackendTexture"/> + <c>SKImage.FromTexture</c>。
/// </summary>
/// <remarks>
/// <para>包装生命周期：每次绘制现场创建、绘制后立即释放——Ganesh 对已记录命令持有的代理引用
/// 会保活纹理直至帧冲刷；不跨帧缓存，规避渲染线程亲和与上下文重建悬挂。</para>
/// <para>采样同步契约由生产者保证：交付时图像已处于声明布局、写入已对后续采样可见、
/// 生产与消费共用同一队列按序串行（见共享表面源实现）——本适配器不做任何布局转换或同步原语。</para>
/// <para><b>AOT 兼容</b>：sealed 无状态类，public ctor（DI 直接激活，AOT 源生成器只解析 public ctor），零反射。</para>
/// </remarks>
public sealed class SkiaVulkanImagePresenter : IHostSurfacePresenter
{
    /// <inheritdoc/>
    public bool CanPresent(in SharedGpuSurfaceDescriptor descriptor)
        => descriptor.Kind == SharedGpuHandleKind.VulkanNativeImage;

    /// <inheritdoc/>
    public bool TryWrap(
        GRContext grContext,
        in SharedGpuSurfaceDescriptor descriptor,
        [NotNullWhen(true)] out SkiaWrappedSurface? surface,
        out string? failureReason)
    {
        surface = null;
        failureReason = null;

        // GRVkImageInfo 各字段 = 描述符 Native* 如实回填（生产者侧 vkCreateImage 的真实参数），
        // 创建 = 声明逐位一致；Image/Memory 句柄按位转换为 ulong（Skia 的 Vulkan 句柄类型）。
        GRVkImageInfo vkInfo = new()
        {
            Image = unchecked((ulong)descriptor.NativeImage),
            Alloc = new GRVkAlloc
            {
                Memory = unchecked((ulong)descriptor.NativeDeviceMemory),
                Offset = descriptor.MemoryOffset,
                Size = descriptor.MemorySize,
            },
            ImageTiling = descriptor.NativeImageTiling,
            ImageLayout = descriptor.NativeImageLayout,
            Format = descriptor.NativeVkFormat,
            ImageUsageFlags = descriptor.NativeImageUsage,
            SampleCount = 1,
            LevelCount = 1,
            CurrentQueueFamily = descriptor.NativeQueueFamilyIndex,
            Protected = false,
            SharingMode = 0, // VK_SHARING_MODE_EXCLUSIVE（同队列族独占）
        };

        var backendTexture = new GRBackendTexture(descriptor.Width, descriptor.Height, vkInfo);
        // TopLeft：本链路图像行 0 = 画面顶部（GPU 四边形渲染 + 行对齐拷贝），Skia 画布同 y 向下。
        // 颜色型别与 VkFormat R8G8B8A8_UNORM 对应；视频帧无透明通道 → Opaque。
        var image = SKImage.FromTexture(
            grContext, backendTexture, GRSurfaceOrigin.TopLeft,
            SKColorType.Rgba8888, SKAlphaType.Opaque);
        if (image is null)
        {
            backendTexture.Dispose();
            failureReason = "SKImage.FromTexture 返回 null（VkImage 包装失败）";
            return false;
        }

        surface = new SkiaWrappedSurface(image, backendTexture);
        return true;
    }
}

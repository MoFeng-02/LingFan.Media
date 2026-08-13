namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan 图像帧资源。实现 <see cref="IFrameResource"/>。
/// </summary>
/// <remarks>
/// <para>封装 <c>VkImage</c> + <c>VkDeviceMemory</c>，供未来 GPU 零拷贝路径使用
///（如 VAAPI 硬解 → Vulkan 外部内存导入）。</para>
/// <para><b>资源所有权</b>：构造时不 AddRef（Vulkan 句柄无引用计数），
/// <see cref="Dispose"/> 调用 <c>vk.DestroyImage</c> + <c>vk.FreeMemory</c> 显式释放。</para>
/// <para><b>异步策略</b>：<see cref="Dispose"/> 为同步（native 分类）——Vulkan 资源释放是同步原生调用，无 I/O await。</para>
/// <para>AOT 兼容：sealed 类，无反射、无 Silk.NET 运行期依赖（销毁走 <c>VulkanNative</c> 零反射绑定）。</para>
/// </remarks>
public sealed unsafe class VulkanImageResource : IFrameResource, IGpuTextureResource
{
    private readonly Device _device;
    private readonly Image _image;
    private readonly DeviceMemory _memory;
    private readonly ImageLayout _currentLayout;
    private readonly int _subresourceIndex;
    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>导入纹理的子资源 / 切片索引（D3D11 纹理数组时对应 <c>avFrame->data[1]</c>）。
    /// 出餐侧 <see cref="VulkanRenderer.BlitVulkanImageResource"/> 据此选 <c>baseArrayLayer</c>。</summary>
    public int SubresourceIndex => _subresourceIndex;

    /// <summary>Vulkan 图像句柄。</summary>
    public Image Image => _image;

    /// <summary>Vulkan 设备内存句柄。</summary>
    public DeviceMemory Memory => _memory;

    /// <summary>
    /// 当前图像布局。生产者交付图像时应设置其交付时的布局；
    /// <see cref="VulkanRenderer"/> 在零拷贝 Present 时会将其转换到
    /// <c>TransferSrcOptimal</c> 再 blit/copy 到 SwapChain。默认 <c>TransferSrcOptimal</c>。
    /// </summary>
    public ImageLayout CurrentLayout => _currentLayout;

    /// <summary>
    /// 初始化 <see cref="VulkanImageResource"/> 的新实例。
    /// </summary>
    /// <param name="device">Vulkan 逻辑设备（共享，不由本类释放；销毁时经 <c>VulkanNative</c> 调用）。</param>
    /// <param name="image">VkImage 句柄。</param>
    /// <param name="memory">VkDeviceMemory 句柄。</param>
    /// <param name="width">图像宽度。</param>
    /// <param name="height">图像高度。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="subresourceIndex">子资源 / 切片索引（D3D11 纹理数组时为数组索引）。</param>
    /// <param name="currentLayout">当前图像布局（交付时）。</param>
    public VulkanImageResource(Device device, Image image, DeviceMemory memory, int width, int height, PixelFormat format, int subresourceIndex = 0, ImageLayout currentLayout = ImageLayout.TransferSrcOptimal)
    {
        _device = device;
        _image = image;
        _memory = memory;
        Width = width;
        Height = height;
        Format = format;
        _subresourceIndex = subresourceIndex;
        _currentLayout = currentLayout;
    }

    /// <inheritdoc/>
    /// <remarks>VkImage 句柄（<c>Image.Handle</c>）作为中立原生纹理句柄，供渲染器零拷贝 Present（<see cref="BlitVulkanImageResource"/>）。</remarks>
    IntPtr IGpuTextureResource.NativeTextureHandle => (IntPtr)_image.Handle;

    /// <inheritdoc/>
    /// <remarks>返回导入时记录的切片索引（数组>1 的 D3D11VA 切片选择由此下发至 Blit 的 baseArrayLayer）。</remarks>
    int IGpuTextureResource.SubresourceIndex => _subresourceIndex;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Vulkan GPU 纹理 CPU 回读未实现（零拷贝 Present 为支持路径；
    /// 经 staging buffer + 命令缓冲拷贝回读属未来范围，不影响零拷贝上屏）。</exception>
    GpuTextureReadback IGpuTextureResource.ReadbackToCpu()
        => throw new NotSupportedException(
            "Vulkan GPU 纹理 CPU 回读未实现（零拷贝 Present 为支持路径；需经 staging buffer + 命令缓冲拷贝，属未来范围）。");

    /// <summary>
    /// 释放 Vulkan 图像和设备内存（同步原生调用）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_image.Handle != 0)
            VulkanNative.DestroyImage(_device, _image, null);

        if (_memory.Handle != 0)
            VulkanNative.FreeMemory(_device, _memory, null);
    }
}

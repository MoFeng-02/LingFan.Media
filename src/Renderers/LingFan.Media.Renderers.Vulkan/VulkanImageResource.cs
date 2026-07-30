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
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed unsafe class VulkanImageResource : IFrameResource
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Image _image;
    private readonly DeviceMemory _memory;
    private readonly ImageLayout _currentLayout;
    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

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
    /// <param name="vk">Vulkan API（共享，不由本类释放）。</param>
    /// <param name="device">Vulkan 逻辑设备（共享，不由本类释放）。</param>
    /// <param name="image">VkImage 句柄。</param>
    /// <param name="memory">VkDeviceMemory 句柄。</param>
    /// <param name="width">图像宽度。</param>
    /// <param name="height">图像高度。</param>
    /// <param name="format">像素格式。</param>
    public VulkanImageResource(Vk vk, Device device, Image image, DeviceMemory memory, int width, int height, PixelFormat format, ImageLayout currentLayout = ImageLayout.TransferSrcOptimal)
    {
        _vk = vk;
        _device = device;
        _image = image;
        _memory = memory;
        Width = width;
        Height = height;
        Format = format;
        _currentLayout = currentLayout;
    }

    /// <summary>
    /// 释放 Vulkan 图像和设备内存（同步原生调用）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_image.Handle != 0)
            _vk.DestroyImage(_device, _image, null);

        if (_memory.Handle != 0)
            _vk.FreeMemory(_device, _memory, null);
    }
}

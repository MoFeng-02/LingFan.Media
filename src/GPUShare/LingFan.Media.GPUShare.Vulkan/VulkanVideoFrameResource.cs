using System;
using Silk.NET.Vulkan;
using LingFan.Media.Abstractions;

namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// Vulkan 视频解码 DPB 帧资源（非释放，DPB 复用）。
/// </summary>
/// <remarks>
/// <para>实现 <see cref="IFrameResource"/> + <see cref="IGpuTextureResource"/>，供 VulkanVideoDecoder 产出、渲染器消费
/// （<see cref="VulkanRenderer"/> 经 pattern matching 匹配此类型，直接 blit 同设备 NV12 VkImage，零拷贝上屏）。</para>
/// <para><b>资源所有权（与 VulkanImageResource 关键区别）</b>：DPB 图像须跨多帧复用（被后续帧作为参考帧引用），
/// 因此本类 <see cref="Dispose"/> <b>不</b>调用 vkDestroyImage / vkFreeMemory——仅通知解码器该帧已被消费
/// （<paramref name="onReleased"/> 回调，传回槽位索引），由解码器 DPB 管理器决定何时复用该槽位。
/// 若 Dispose 即销毁图像，会导致「帧在途仍被渲染器读取时槽位被复用/图像被销毁」的竞态与显存泄漏。</para>
/// <para><b>异步策略</b>：<see cref="Dispose"/> 为同步（native 分类），仅触发托管回调，无 I/O await。</para>
/// <para>AOT 兼容：sealed 类，无反射、无 Silk.NET 运行期依赖（销毁走 <c>VulkanNative</c> 零反射绑定）。</para>
/// </remarks>
public sealed unsafe class VulkanVideoFrameResource : IFrameResource, IGpuTextureResource
{
    private readonly Device _device;
    private readonly Image _image;
    private readonly DeviceMemory _memory;
    private readonly int _slotIndex;
    private readonly Action<int>? _onReleased;
    private readonly ImageLayout _currentLayout;
    private bool _released;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>DPB 槽位索引（解码器侧 DPB 管理器据此复用图像）。</summary>
    public int SlotIndex => _slotIndex;

    /// <summary>Vulkan 图像句柄。</summary>
    public Image Image => _image;

    /// <summary>Vulkan 设备内存句柄。</summary>
    public DeviceMemory Memory => _memory;

    /// <summary>当前图像布局（交付时）。解码后由解码器转换到 <see cref="ImageLayout.TransferSrcOptimal"/> 供渲染器 blit。</summary>
    public ImageLayout CurrentLayout => _currentLayout;

    /// <summary>
    /// 初始化 <see cref="VulkanVideoFrameResource"/> 的新实例。
    /// </summary>
    /// <param name="device">Vulkan 逻辑设备（共享，不由本类释放）。</param>
    /// <param name="image">VkImage 句柄（DPB 复用图像，不由本类释放）。</param>
    /// <param name="memory">VkDeviceMemory 句柄（不由本类释放）。</param>
    /// <param name="width">图像宽度。</param>
    /// <param name="height">图像高度。</param>
    /// <param name="format">像素格式（NV12）。</param>
    /// <param name="slotIndex">DPB 槽位索引。</param>
    /// <param name="onReleased">帧被消费时回调（传回槽位索引），供解码器 DPB 管理器复用图像。</param>
    /// <param name="currentLayout">当前图像布局（交付时）。</param>
    public VulkanVideoFrameResource(Device device, Image image, DeviceMemory memory, int width, int height,
        PixelFormat format, int slotIndex, Action<int>? onReleased, ImageLayout currentLayout = ImageLayout.TransferSrcOptimal)
    {
        _device = device;
        _image = image;
        _memory = memory;
        Width = width;
        Height = height;
        Format = format;
        _slotIndex = slotIndex;
        _onReleased = onReleased;
        _currentLayout = currentLayout;
    }

    /// <inheritdoc/>
    IntPtr IGpuTextureResource.NativeTextureHandle => (IntPtr)_image.Handle;

    /// <inheritdoc/>
    int IGpuTextureResource.SubresourceIndex => 0;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Vulkan 视频解码 NV12 纹理 CPU 回读未实现（零拷贝 Present 为支持路径）。</exception>
    GpuTextureReadback IGpuTextureResource.ReadbackToCpu()
        => throw new NotSupportedException(
            "Vulkan 视频解码 NV12 纹理 CPU 回读未实现（零拷贝 Present 为支持路径；需经 staging buffer + 命令缓冲拷贝，属未来范围）。");

    /// <summary>
    /// 释放（非销毁）：仅通知解码器该帧已被消费，由 DPB 管理器决定槽位复用。
    /// 绝不 vkDestroyImage（否则 DPB 复用失效、帧在途即被销毁）。
    /// </summary>
    public void Dispose()
    {
        if (_released) return;
        _released = true;
        _onReleased?.Invoke(_slotIndex);
    }
}

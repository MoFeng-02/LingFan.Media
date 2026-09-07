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
    private readonly Semaphore _decodeDoneSemaphore;
    private readonly VulkanVideoGpuReadbackContext? _readbackContext;
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

    /// <summary>当前图像布局（交付时）。解码后由解码器保证为 <see cref="ImageLayout.VideoDecodeDpbKhr"/>，渲染器经中性转换器（VideoDecodeDpbKhr → ShaderReadOnlyOptimal）采样上屏。</summary>
    public ImageLayout CurrentLayout => _currentLayout;

    /// <summary>解码完成信号量（跨队列同步用）。解码器在 video 队列提交后 signal，渲染器在 graphics 队列提交前 wait，
    /// 以建立「解码写入 → 着色器采样」的跨队列执行依赖与内存可见性。CONCURRENT 共享仅解决所有权转移，不提供此依赖。</summary>
    public Semaphore DecodeDoneSemaphore => _decodeDoneSemaphore;

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
    /// <param name="decodeDoneSemaphore">解码完成信号量（跨队列同步用，可为默认空句柄）。</param>
    /// <param name="readbackContext">GPU→CPU 回读助手（诊断路径，可为 null）。</param>
    public VulkanVideoFrameResource(Device device, Image image, DeviceMemory memory, int width, int height,
        PixelFormat format, int slotIndex, Action<int>? onReleased, ImageLayout currentLayout = ImageLayout.TransferSrcOptimal,
        Semaphore decodeDoneSemaphore = default, VulkanVideoGpuReadbackContext? readbackContext = null)
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
        _decodeDoneSemaphore = decodeDoneSemaphore;
        _readbackContext = readbackContext;
    }

    /// <inheritdoc/>
    IntPtr IGpuTextureResource.NativeTextureHandle => (IntPtr)_image.Handle;

    /// <inheritdoc/>
    int IGpuTextureResource.SubresourceIndex => 0;

    /// <inheritdoc/>
    /// <remarks>调用 <see cref="VulkanVideoGpuReadbackContext"/> 同步拷两平面到 host-visible staging buffer + CPU 软转 NV12→BGRA32。
    /// 内部 barrier 保证图像布局不变（解码后 VideoDecodeDpbKhr → TransferSrcOptimal → VideoDecodeDpbKhr），
    /// 不破坏渲染器后续经 <see cref="VulkanNv12ToRgbaConverter.Convert"/> 自 VideoDecodeDpbKhr 起的 transition 链。
    /// 若解码器未注入 <c>readbackContext</c>（生产场景：仅零拷贝上屏），则按零拷贝路径约定抛 <see cref="NotSupportedException"/>。</remarks>
    GpuTextureReadback IGpuTextureResource.ReadbackToCpu()
    {
        if (_readbackContext is null)
            throw new NotSupportedException(
                "Vulkan 视频解码 NV12 纹理 CPU 回读未启用（生产路径仅零拷贝 Present，解码器未注入 readbackContext）。");
        byte[] bgra = _readbackContext.ReadbackNv12AsBgra32(_image, (uint)Width, (uint)Height, _currentLayout, (uint)_slotIndex);
        return new GpuTextureReadback(Width, Height, PixelFormat.BGRA32, bgra, Width * 4);
    }

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

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Abstractions;

/// <summary>
/// Android AHardwareBuffer 帧资源。实现 <see cref="IFrameResource"/>（Phase 2，Android 零拷贝 GPU 路径）。
/// </summary>
/// <remarks>
/// <para>放在 Abstractions 的原因：被 Backends.FFmpeg（MediaCodec 硬解）与 Renderers.Vulkan（VkImage 导入）两个层引用，
/// 属「被 2 个以上层引用」的跨层契约类型（十一章判定铁律），必须留在 Abstractions。</para>
/// <para>所有权：本资源拥有 <c>AHardwareBuffer*</c>，<see cref="Dispose"/> 时调用 NDK <c>AHardwareBuffer_release</c> 释放（原生释放，同步）。</para>
/// <para>GPU 零拷贝路径：MediaCodec → AHardwareBuffer → VkImage → VulkanRenderer → Swapchain → TextureView（SurfaceFlinger 合成）→ Display。</para>
/// <para><b>AOT 兼容</b>：sealed 类，仅一个静态 P/Invoke 声明 + 原生指针释放，无反射、无动态代码生成。</para>
/// <para>本类型仅应在 Android 平台由 MediaCodec 硬解路径创建；非 Android 平台不应构造，否则 <see cref="Dispose"/> 会因找不到
/// <c>libandroid.so</c> 抛出异常。</para>
/// </remarks>
[SupportedOSPlatform("android")]
public sealed partial class AHardwareBufferResource : IFrameResource
{
    private IntPtr _buffer;
    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>底层 <c>AHardwareBuffer*</c>（Android NDK 原生指针）。</summary>
    public IntPtr AHardwareBuffer => _buffer;

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 从 Android NDK <c>AHardwareBuffer*</c> 创建实例（零拷贝路径，资源拥有该 buffer 的所有权）。
    /// </summary>
    /// <param name="width">帧宽度（像素）。</param>
    /// <param name="height">帧高度（像素）。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="aHardwareBuffer">Android NDK <c>AHardwareBuffer*</c>，必须非空。</param>
    public AHardwareBufferResource(int width, int height, PixelFormat format, IntPtr aHardwareBuffer)
    {
        // B-CTR1: 非 Android 平台构造后 Dispose 必因找不到 libandroid.so 崩溃——
        // 构造即守卫，快速失败优于延迟到释放路径炸。
        if (!OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException("AHardwareBufferResource 仅支持 Android 平台。");
        if (aHardwareBuffer == IntPtr.Zero)
            throw new ArgumentNullException(nameof(aHardwareBuffer), "AHardwareBuffer 指针不能为空。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        Format = format;
        _buffer = aHardwareBuffer;
    }

    /// <summary>释放 AHardwareBuffer 原生资源（调用 NDK <c>AHardwareBuffer_release</c>）。同步、无 I/O。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_buffer != IntPtr.Zero)
        {
            AHardwareBufferRelease(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    [LibraryImport("libandroid.so", EntryPoint = "AHardwareBuffer_release")]
    private static partial void AHardwareBufferRelease(IntPtr buffer);
}

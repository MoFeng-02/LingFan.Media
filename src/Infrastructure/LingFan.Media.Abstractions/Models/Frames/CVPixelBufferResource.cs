using System.Runtime.Versioning;

namespace LingFan.Media.Abstractions;

/// <summary>
/// Apple CoreVideo 像素缓冲帧资源（VideoToolbox 硬解输出）。实现 <see cref="IFrameResource"/>（Phase 3，Apple 零拷贝 GPU 路径）。
/// </summary>
/// <remarks>
/// <para>放在 Abstractions 的原因：被 Backends.FFmpeg（VideoToolbox 硬解 <c>AVFrame.data[3]</c> 产出）与
/// Renderers.Metal（CVPixelBuffer → IOSurface → MTLTexture 零拷贝消费）两个层引用，
/// 属「被 2 个以上层引用」的跨层契约类型（十一章判定铁律），必须留在 Abstractions（用户 2026-07-28 拍板）。</para>
/// <para>所有权：本资源拥有已 retain 的 <c>CVPixelBufferRef</c>，<see cref="Dispose"/> 时经
/// <see cref="SafeCVPixelBufferHandle"/> 调用 CoreVideo <c>CVPixelBufferRelease</c> 释放（原生释放，同步、无 I/O）。
/// FFmpeg 侧 <c>AVFrame.data[3]</c> 为非拥有引用，创建本资源前必须先 <c>CVPixelBufferRetain</c>。</para>
/// <para>GPU 零拷贝路径：VideoToolbox → CVPixelBuffer → IOSurface → MTLTexture → MetalRenderer → CAMetalLayer（合成）→ Display。</para>
/// <para><b>AOT 兼容</b>：sealed 类 + SafeHandle 静态 P/Invoke，无反射、无动态代码生成。</para>
/// <para>本类型仅应在 macOS / iOS 平台由 VideoToolbox 硬解路径创建；非 Apple 平台不应构造，
/// 否则释放时会因找不到 CoreVideo framework 抛出异常。</para>
/// </remarks>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public sealed class CVPixelBufferResource : IFrameResource
{
    private readonly SafeCVPixelBufferHandle _handle;
    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>底层 <c>CVPixelBufferRef</c>（Apple 原生指针；已释放后为 <see cref="IntPtr.Zero"/>）。</summary>
    public IntPtr CVPixelBuffer => _disposed ? IntPtr.Zero : _handle.DangerousGetHandle();

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 从已 retain 的 <c>CVPixelBufferRef</c> 创建实例（零拷贝路径，资源接管该 buffer 的所有权）。
    /// </summary>
    /// <param name="width">帧宽度（像素）。</param>
    /// <param name="height">帧高度（像素）。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="cvPixelBuffer">已 retain 的 <c>CVPixelBufferRef</c>，必须非空。</param>
    public CVPixelBufferResource(int width, int height, PixelFormat format, IntPtr cvPixelBuffer)
    {
        // B-CTR1: 非 Apple 平台构造后释放必因找不到 CoreVideo framework 崩溃——构造即守卫。
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS())
            throw new PlatformNotSupportedException("CVPixelBufferResource 仅支持 macOS / iOS 平台。");
        if (cvPixelBuffer == IntPtr.Zero)
            throw new ArgumentNullException(nameof(cvPixelBuffer), "CVPixelBuffer 指针不能为空。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        Format = format;
        _handle = new SafeCVPixelBufferHandle(cvPixelBuffer);
    }

    /// <summary>释放 CVPixelBuffer 原生资源（经 SafeHandle 调用 <c>CVPixelBufferRelease</c>）。同步、无 I/O。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handle.Dispose();
    }
}

using System.Runtime.Versioning;

namespace LingFan.Media.Abstractions;

/// <summary>
/// Apple IOSurface 共享内存帧资源（跨进程可共享）。实现 <see cref="IFrameResource"/>（Phase 3，Apple 零拷贝 GPU 路径）。
/// </summary>
/// <remarks>
/// <para>放在 Abstractions 的原因：被 Backends.FFmpeg（VideoToolbox 硬解经 <c>CVPixelBufferGetIOSurface</c> 导出）与
/// Renderers.Metal（<c>MTLDevice.NewTexture(descriptor, iosurface, plane)</c> 零拷贝导入）两个层引用，
/// 属「被 2 个以上层引用」的跨层契约类型（十一章判定铁律），必须留在 Abstractions。</para>
/// <para>所有权：本资源拥有已 retain 的 <c>IOSurfaceRef</c>，<see cref="Dispose"/> 时经
/// <see cref="SafeIOSurfaceHandle"/> 调用 CoreFoundation <c>CFRelease</c> 释放（原生释放，同步、无 I/O）。</para>
/// <para>GPU 零拷贝路径：CVPixelBuffer → IOSurface → MTLTexture → MetalRenderer → CAMetalLayer（合成）→ Display。</para>
/// <para><b>AOT 兼容</b>：sealed 类 + SafeHandle 静态 P/Invoke，无反射、无动态代码生成。</para>
/// <para>本类型仅应在 macOS / iOS 平台创建；非 Apple 平台不应构造，
/// 否则释放时会因找不到 CoreFoundation framework 抛出异常。</para>
/// </remarks>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public sealed class IOSurfaceResource : IFrameResource
{
    private readonly SafeIOSurfaceHandle _handle;
    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>底层 <c>IOSurfaceRef</c>（Apple 原生指针；已释放后为 <see cref="IntPtr.Zero"/>）。</summary>
    public IntPtr IOSurface => _disposed ? IntPtr.Zero : _handle.DangerousGetHandle();

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 从已 retain 的 <c>IOSurfaceRef</c> 创建实例（零拷贝路径，资源接管该 surface 的所有权）。
    /// </summary>
    /// <param name="width">帧宽度（像素）。</param>
    /// <param name="height">帧高度（像素）。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="ioSurface">已 retain 的 <c>IOSurfaceRef</c>，必须非空。</param>
    public IOSurfaceResource(int width, int height, PixelFormat format, IntPtr ioSurface)
    {
        // B-CTR1: 非 Apple 平台构造后释放必因找不到 CoreFoundation framework 崩溃——构造即守卫。
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS())
            throw new PlatformNotSupportedException("IOSurfaceResource 仅支持 macOS / iOS 平台。");
        if (ioSurface == IntPtr.Zero)
            throw new ArgumentNullException(nameof(ioSurface), "IOSurface 指针不能为空。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        Format = format;
        _handle = new SafeIOSurfaceHandle(ioSurface);
    }

    /// <summary>释放 IOSurface 原生资源（经 SafeHandle 调用 <c>CFRelease</c>）。同步、无 I/O。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handle.Dispose();
    }
}

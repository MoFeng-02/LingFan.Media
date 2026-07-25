namespace LingFan.Media.Platforms.MacOS;

/// <summary>
/// IOSurface 零拷贝桥梁。V1 桩实现。
/// </summary>
/// <remarks>
/// <para>职责：管理 IOSurface 对象，作为 VideoToolbox 硬解输出（CVPixelBuffer）
/// 与 Metal 纹理之间的零拷贝桥梁。IOSurface 是 macOS 上跨进程 GPU 资源共享的标准机制。</para>
/// <para><b>GPU 零拷贝路径</b>：
/// VideoToolbox → CVPixelBuffer → IOSurface → MTLTexture → MetalRenderer → CAMetalLayer → Display</para>
/// <para>V1 桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// IOSurface 互操作属 Phase 2-3 目标（macOS）。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——IOSurface C API 调用是同步边界，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class IOSurfaceInterop
{
    /// <summary>
    /// 从 CVPixelBuffer 获取 IOSurface（零拷贝，VideoToolbox 输出路径）。
    /// </summary>
    /// <param name="pixelBuffer">CVPixelBuffer 句柄。</param>
    /// <returns>IOSurface 句柄（不转移所有权，CVPixelBuffer 释放后失效）。</returns>
    public nint GetIOSurfaceFromCVPixelBuffer(nint pixelBuffer)
        => throw new NotSupportedException("IOSurface 互操作尚未实现。Phase 2-3 目标。");

    /// <summary>
    /// 锁定 IOSurface 用于 CPU 访问。
    /// </summary>
    /// <param name="ioSurface">IOSurface 句柄。</param>
    /// <param name="readOnly">是否只读访问。</param>
    /// <returns>IOSurface 像素数据基地址。</returns>
    public nint Lock(nint ioSurface, bool readOnly)
        => throw new NotSupportedException("IOSurface 锁定尚未实现。");

    /// <summary>
    /// 解锁 IOSurface。
    /// </summary>
    /// <param name="ioSurface">IOSurface 句柄。</param>
    /// <param name="readOnly">是否只读访问。</param>
    public void Unlock(nint ioSurface, bool readOnly)
        => throw new NotSupportedException("IOSurface 解锁尚未实现。");

    /// <summary>
    /// 创建独立 IOSurface（非从 CVPixelBuffer 获取）。
    /// </summary>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    /// <param name="format">像素格式（如 'BGRA' = 0x41524742）。</param>
    /// <returns>IOSurface 句柄。</returns>
    public nint CreateIOSurface(int width, int height, uint format)
        => throw new NotSupportedException("IOSurface 创建尚未实现。");
}

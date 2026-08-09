namespace LingFan.Media.Platforms.MacOS;

/// <summary>
/// Metal GPU 互操作。桩实现。
/// </summary>
/// <remarks>
/// <para>职责：将 IOSurface / CVPixelBuffer 导入 Metal 纹理（MTLTexture），
/// 供 MetalRenderer 渲染到 CAMetalLayer。</para>
/// <para><b>GPU 零拷贝路径</b>：
/// VideoToolbox → CVPixelBuffer → IOSurface → MTLTexture → MetalRenderer → CAMetalLayer → Display</para>
/// <para>桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// Metal 互操作属 Phase 2-3 目标（macOS / iOS）。
/// 未来实现使用 SharpMetal 库绑定 Metal API。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——Metal API 调用是同步边界，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class MetalInterop
{
    /// <summary>
    /// 从 IOSurface 创建 Metal 纹理。
    /// </summary>
    /// <param name="metalDevice">id&lt;MTLDevice&gt; 句柄。</param>
    /// <param name="ioSurface">IOSurface 句柄。</param>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <param name="format">Metal 像素格式（如 MTLPixelFormatBGRA8Unorm = 80）。</param>
    /// <returns>id&lt;MTLTexture&gt; 句柄。</returns>
    public nint CreateTextureFromIOSurface(
        nint metalDevice, nint ioSurface, int width, int height, int format)
        => throw new NotSupportedException("Metal 互操作尚未实现。Phase 2-3 目标。");

    /// <summary>
    /// 从 CVPixelBuffer 创建 Metal 纹理。
    /// </summary>
    /// <param name="metalDevice">id&lt;MTLDevice&gt; 句柄。</param>
    /// <param name="pixelBuffer">CVPixelBuffer 句柄。</param>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <param name="format">Metal 像素格式。</param>
    /// <returns>id&lt;MTLTexture&gt; 句柄。</returns>
    public nint CreateTextureFromCVPixelBuffer(
        nint metalDevice, nint pixelBuffer, int width, int height, int format)
        => throw new NotSupportedException("Metal 互操作尚未实现。");

    /// <summary>
    /// 创建 CAMetalLayer——通过 CoreAnimation 子层合成实现无空域：视频帧融入宿主合成树，不独占渲染表面，其他内容可自由覆盖。
    /// </summary>
    /// <param name="width">层宽度。</param>
    /// <param name="height">层高度。</param>
    /// <returns>CAMetalLayer 句柄。</returns>
    public nint CreateMetalLayer(int width, int height)
        => throw new NotSupportedException("CAMetalLayer 创建尚未实现。");
}

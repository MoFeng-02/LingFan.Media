namespace LingFan.Media.Renderers.Metal;

/// <summary>
/// Apple IOSurface 共享内存帧资源。实现 <see cref="IFrameResource"/>。
/// </summary>
/// <remarks>
/// V1 桩实现——Metal 渲染器尚未实现（Phase 3 目标）。
/// 未来实现时封装 IOSurfaceRef（Apple 共享内存），
/// 作为 CVPixelBuffer → IOSurface → MTLTexture 零拷贝桥梁的中间资源。
/// </remarks>
public sealed class IOSurfaceResource : IFrameResource
{
    /// <inheritdoc/>
    public int Width => throw new NotSupportedException("Metal 渲染器尚未实现。");

    /// <inheritdoc/>
    public int Height => throw new NotSupportedException("Metal 渲染器尚未实现。");

    /// <inheritdoc/>
    public PixelFormat Format => throw new NotSupportedException("Metal 渲染器尚未实现。");

    /// <summary>释放资源（桩——无资源可释放）。</summary>
    public void Dispose() { }
}

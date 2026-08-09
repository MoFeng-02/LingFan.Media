namespace LingFan.Media.Renderers.Metal;

/// <summary>
/// Metal 纹理帧资源。实现 <see cref="IFrameResource"/>。
/// </summary>
/// <remarks>
/// 桩实现——Metal 渲染器尚未实现（Phase 3 目标）。
/// 未来实现时封装 MTLTexture（macOS / iOS），支持 VideoToolbox 零拷贝渲染。
/// </remarks>
public sealed class MetalTextureResource : IFrameResource
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

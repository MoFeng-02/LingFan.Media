namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL 纹理帧资源。实现 <see cref="IFrameResource"/>。
/// </summary>
/// <remarks>
/// <para>桩实现——OpenGL 渲染器尚未实现（桌面兼容备用，Phase 2-3 目标）。</para>
/// <para>与 D3D11/Vulkan/Metal 不同，OpenGL 纹理 ID 是 <c>uint</c> 整数（非指针），
/// 不使用 SafeHandle 封装，使用显式 <see cref="Dispose"/>（<c>glDeleteTextures</c>）。
/// 此设计遵循 SafeHandle 策略。</para>
/// <para>Apple 平台（macOS/iOS）已废弃 OpenGL，使用 Metal。</para>
/// </remarks>
public sealed class GLTextureResource : IFrameResource
{
    /// <inheritdoc/>
    public int Width => throw new NotSupportedException("OpenGL 渲染器尚未实现。");

    /// <inheritdoc/>
    public int Height => throw new NotSupportedException("OpenGL 渲染器尚未实现。");

    /// <inheritdoc/>
    public PixelFormat Format => throw new NotSupportedException("OpenGL 渲染器尚未实现。");

    /// <summary>OpenGL 纹理 ID（桩）。</summary>
    public uint TextureId => throw new NotSupportedException("OpenGL 渲染器尚未实现。");

    /// <summary>OpenGL 纹理目标（GL_TEXTURE_2D 等，桩）。</summary>
    public int Target => throw new NotSupportedException("OpenGL 渲染器尚未实现。");

    /// <summary>释放资源（桩——无资源可释放）。</summary>
    public void Dispose() { }
}

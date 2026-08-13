namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL 纹理帧资源。实现 <see cref="IFrameResource"/>。
/// </summary>
/// <remarks>
/// <para>预留类型——OpenGL 渲染器已实现，本类为未来 VAAPI EGL interop 零拷贝 GPU 纹理帧的占位，
/// 当前属性均未实现（抛 <see cref="NotSupportedException"/>），待该零拷贝路径接入时补全。</para>
/// <para>与 D3D11/Vulkan/Metal 不同，OpenGL 纹理 ID 是 <c>uint</c> 整数（非指针），
/// 不使用 SafeHandle 封装，使用显式 <see cref="Dispose"/>（<c>glDeleteTextures</c>）。
/// 此设计遵循 SafeHandle 策略。</para>
/// </remarks>
public sealed class GLTextureResource : IFrameResource
{
    /// <inheritdoc/>
    public int Width => throw new NotSupportedException("OpenGL GPU 纹理零拷贝路径尚未实现。");

    /// <inheritdoc/>
    public int Height => throw new NotSupportedException("OpenGL GPU 纹理零拷贝路径尚未实现。");

    /// <inheritdoc/>
    public PixelFormat Format => throw new NotSupportedException("OpenGL GPU 纹理零拷贝路径尚未实现。");

    /// <summary>OpenGL 纹理 ID（桩）。</summary>
    public uint TextureId => throw new NotSupportedException("OpenGL GPU 纹理零拷贝路径尚未实现。");

    /// <summary>OpenGL 纹理目标（GL_TEXTURE_2D 等，桩）。</summary>
    public int Target => throw new NotSupportedException("OpenGL GPU 纹理零拷贝路径尚未实现。");

    /// <summary>释放资源（桩——无资源可释放）。</summary>
    public void Dispose() { }
}

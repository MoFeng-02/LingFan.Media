using LingFan.Media.Abstractions;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL 纹理帧资源。实现 <see cref="IFrameResource"/> 与 <see cref="IGpuTextureResource"/>（零拷贝 GPU 纹理）。
/// </summary>
/// <remarks>
/// <para>由解码后端（未来 VAAPI → EGL / ffmpeg GL interop 路径）经共享组产出 GL 纹理 ID 后构造，
/// 经中立 <see cref="IGpuTextureResource"/> 交由渲染器直接采样（零拷贝）。</para>
/// <para>与 D3D11/Vulkan/Metal 不同，OpenGL 纹理 ID 是 <c>uint</c> 整数（非指针），
/// 不使用 SafeHandle 封装，使用显式 <see cref="Dispose"/>（<c>glDeleteTextures</c>，仅当 <paramref name="ownsTexture"/> 为真）。</para>
/// <para><b>回读</b>：<see cref="ReadbackToCpu"/> 经共享设备上下文（<see cref="OpenGLOffscreenDeviceContext"/>）
/// 读取 RGBA8 并转换为 BGRA32，供 Skia 软渲染兜底路径消费。未注入设备上下文时抛
/// <see cref="NotSupportedException"/>（与契约层 <see cref="IGpuTextureResource"/> 异步策略一致：同步 native 调用，非伪异步）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed unsafe class GLTextureResource : IFrameResource, IGpuTextureResource
{
    private readonly uint _textureId;
    private readonly int _target;
    private readonly int _subresourceIndex;
    private readonly OpenGLOffscreenDeviceContext? _deviceContext;
    private readonly bool _ownsTexture;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>OpenGL 纹理 ID。</summary>
    public uint TextureId => _textureId;

    /// <summary>OpenGL 纹理目标（GL_TEXTURE_2D 等，默认 0x0DE1）。</summary>
    public int Target => _target;

    /// <summary>
    /// 初始化 <see cref="GLTextureResource"/> 的新实例。
    /// </summary>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="textureId">GL 纹理 ID（来自解码侧 interop）。</param>
    /// <param name="target">GL 纹理目标（默认 GL_TEXTURE_2D）。</param>
    /// <param name="subresourceIndex">子资源索引（默认 0）。</param>
    /// <param name="deviceContext">共享设备上下文（ReadbackToCpu / 纹理释放所需）。</param>
    /// <param name="ownsTexture">是否拥有纹理所有权（true 时 Dispose 调用 glDeleteTextures）。</param>
    public GLTextureResource(
        int width,
        int height,
        PixelFormat format,
        uint textureId,
        int target = 0x0DE1,
        int subresourceIndex = 0,
        OpenGLOffscreenDeviceContext? deviceContext = null,
        bool ownsTexture = false)
    {
        Width = width;
        Height = height;
        Format = format;
        _textureId = textureId;
        _target = target;
        _subresourceIndex = subresourceIndex;
        _deviceContext = deviceContext;
        _ownsTexture = ownsTexture;
    }

    /// <inheritdoc/>
    IntPtr IGpuTextureResource.NativeTextureHandle => (IntPtr)_textureId;

    /// <inheritdoc/>
    int IGpuTextureResource.SubresourceIndex => _subresourceIndex;

    /// <inheritdoc/>
    public GpuTextureReadback ReadbackToCpu()
    {
        if (_deviceContext is null)
            throw new NotSupportedException(
                "GL 纹理 CPU 回读需要共享设备上下文（由解码侧 interop 注入 OpenGLOffscreenDeviceContext）。");

        _deviceContext.EnsureCreated();
        _deviceContext.MakeCurrent();
        try
        {
            uint tex = _textureId;
            GLNative.glBindTexture(GLNative.GlTexture2DConst, tex);
            int w = Width, h = Height;
            int size = w * h * 4;
            byte[] data = new byte[size];
            fixed (byte* p = data)
                GLNative.glGetTexImage(GLNative.GlTexture2DConst, 0, GLNative.GlRgbaConst, GLNative.GlUnsignedByteConst, p);

            // RGBA → BGRA（与 D3D11TextureResource 同源，避免色彩漂移）
            for (int i = 0; i < size; i += 4)
            {
                byte r = data[i];
                byte b = data[i + 2];
                data[i] = b;
                data[i + 2] = r;
            }

            return new GpuTextureReadback(w, h, PixelFormat.BGRA32, data, w * 4);
        }
        finally
        {
            _deviceContext.ReleaseCurrent();
        }
    }

    /// <summary>
    /// 释放 GL 纹理（仅当拥有所有权）。纹理生命周期默认由解码后端/帧路由管理（契约要求使用期间不被释放）。
    /// </summary>
    public void Dispose()
    {
        if (!_ownsTexture || _deviceContext is null) return;

        _deviceContext.EnsureCreated();
        _deviceContext.MakeCurrent();
        try
        {
            uint tex = _textureId;
            GLNative.glDeleteTextures(1, &tex);
        }
        finally
        {
            _deviceContext.ReleaseCurrent();
        }
    }
}

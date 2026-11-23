using LingFan.Media.Abstractions;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// 跨 API 零拷贝 GL 纹理帧资源（Linux：EGL_EXT_image_dma_buf_import 导入的 VAAPI dma_buf）。
/// </summary>
/// <remarks>
/// <para>由 <see cref="OpenGLGpuFrameProducer"/> 经 <c>eglCreateImageKHR</c> + <c>glEGLImageTargetTexture2DOES</c>
/// 把 VAAPI dma_buf 绑定为 GL 纹理后构造，经中立 <see cref="IGpuTextureResource"/> 交由渲染器直接采样（零拷贝）。</para>
/// <para><b>生命周期</b>：Dispose 在当前 EGL/GL 上下文下——先 <c>glDeleteTextures</c>，再 <c>eglDestroyImageKHR</c>；
/// EGLDisplay 由生产者（离屏共享组所有者）持有，此处不释放。</para>
/// <para><b>状态</b>：单平面变体（旧路径）；当前 NV12 由 <see cref="GLDmaBufNv12Texture"/> 双平面实现替代。
/// 解码侧现已产出 <see cref="GpuFrameImportKind.LinuxDmaBufFd"/>，可用性探测失败即回落软解（S_OK≠被接受）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed unsafe class GLEglDmaBufTexture : IFrameResource, IGpuTextureResource
{
    private readonly uint _textureId;
    private readonly int _subresourceIndex;
    private readonly nint _eglDisplay;
    private readonly nint _eglImage;
    private readonly OpenGLOffscreenDeviceContext _glContext;
    private readonly object _lock = new();
    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; }
    /// <inheritdoc/>
    public int Height { get; }
    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>初始化 <see cref="GLEglDmaBufTexture"/> 的新实例。</summary>
    public GLEglDmaBufTexture(
        int width, int height, PixelFormat format,
        uint textureId, nint eglDisplay, nint eglImage,
        OpenGLOffscreenDeviceContext glContext, int subresourceIndex = 0)
    {
        Width = width;
        Height = height;
        Format = format;
        _textureId = textureId;
        _eglDisplay = eglDisplay;
        _eglImage = eglImage;
        _glContext = glContext;
        _subresourceIndex = subresourceIndex;
    }

    IntPtr IGpuTextureResource.NativeTextureHandle => (IntPtr)_textureId;

    int IGpuTextureResource.SubresourceIndex => _subresourceIndex;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">CPU 回读未实现：dma_buf 零拷贝纹理由渲染器直接采样，不走 CPU 回读。
    /// 本类为单平面旧路径变体，NV12 已由 <see cref="GLDmaBufNv12Texture"/> 双平面实现替代（见类 remarks）。</exception>
    public GpuTextureReadback ReadbackToCpu()
        => throw new NotSupportedException(
            "GLEglDmaBufTexture.ReadbackToCpu 为未来端点：解码侧 VAAPI→EGL 导入尚未启用（见零拷贝架构约束）。");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _glContext.EnsureCreated();
        lock (_glContext.GlAccessLock)
        {
            _glContext.MakeCurrent();
            try
            {
                uint tex = _textureId;
                GLNative.glDeleteTextures(1, &tex);
            }
            finally
            {
                _glContext.ReleaseCurrent();
            }
        }

        if (_eglImage != nint.Zero)
            EglDmaBufImport.EglDestroyImageKHR(_eglDisplay, _eglImage);
    }
}

/// <summary>
/// 跨 API 零拷贝 GL 纹理帧资源（Linux：EGL_EXT_image_dma_buf_import 导入的 VAAPI NV12 dma_buf，双平面）。
/// </summary>
/// <remarks>
/// <para>由 <see cref="OpenGLGpuFrameProducer"/> 把 composed NV12 dma_buf 拆为 Y(R8) / UV(GR88) 两个 EGLImage
/// 并各自绑为 GL 纹理，经中立 <see cref="IGpuTextureResource"/> 交由渲染器用 NV12 shader 采样（零拷贝）。</para>
/// <para><b>生命周期</b>：Dispose 在当前 EGL/GL 上下文下——先 <c>glDeleteTextures</c>（两纹理），再
/// <c>eglDestroyImageKHR</c>（两 EGLImage）；EGLDisplay 由生产者（离屏共享组所有者）持有，此处不释放。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed unsafe class GLDmaBufNv12Texture : IFrameResource, IGpuTextureResource
{
    private readonly uint _yTexture;
    private readonly uint _uvTexture;
    private readonly nint _eglDisplay;
    private readonly nint _eglImageY;
    private readonly nint _eglImageUV;
    private readonly OpenGLOffscreenDeviceContext _glContext;
    private readonly object _lock = new();
    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; }
    /// <inheritdoc/>
    public int Height { get; }
    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>Y 平面 GL 纹理（R8）。</summary>
    public uint YTexture => _yTexture;
    /// <summary>UV 平面 GL 纹理（GR88）。</summary>
    public uint UVTexture => _uvTexture;

    /// <summary>初始化 <see cref="GLDmaBufNv12Texture"/> 的新实例。</summary>
    public GLDmaBufNv12Texture(
        int width, int height, uint yTexture, uint uvTexture,
        nint eglDisplay, nint eglImageY, nint eglImageUV,
        OpenGLOffscreenDeviceContext glContext, int subresourceIndex = 0)
    {
        Width = width;
        Height = height;
        Format = PixelFormat.NV12;
        _yTexture = yTexture;
        _uvTexture = uvTexture;
        _eglDisplay = eglDisplay;
        _eglImageY = eglImageY;
        _eglImageUV = eglImageUV;
        _glContext = glContext;
    }

    IntPtr IGpuTextureResource.NativeTextureHandle => (IntPtr)_yTexture;

    int IGpuTextureResource.SubresourceIndex => 0;

    /// <inheritdoc/>
    public GpuTextureReadback ReadbackToCpu()
        => throw new NotSupportedException(
            "GLDmaBufNv12Texture.ReadbackToCpu 为未来端点：VAAPI→EGL 双平面 NV12 导入已启用，CPU 回读暂未实现。");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _glContext.EnsureCreated();
        lock (_glContext.GlAccessLock)
        {
            _glContext.MakeCurrent();
            try
            {
                uint y = _yTexture, uv = _uvTexture;
                GLNative.glDeleteTextures(1, &y);
                GLNative.glDeleteTextures(1, &uv);
            }
            finally
            {
                _glContext.ReleaseCurrent();
            }
        }

        if (_eglImageY != nint.Zero) EglDmaBufImport.EglDestroyImageKHR(_eglDisplay, _eglImageY);
        if (_eglImageUV != nint.Zero) EglDmaBufImport.EglDestroyImageKHR(_eglDisplay, _eglImageUV);
    }
}

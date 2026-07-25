namespace LingFan.Media.Platforms.Linux;

/// <summary>
/// EGL 互操作。V1 桩实现。
/// </summary>
/// <remarks>
/// <para>职责：通过 EGL 实现 OpenGL 与 VAAPI / Vulkan 的跨 API 资源共享。
/// 使用 <c>EGL_EXT_image_dma_buf_import</c> 扩展从 dmabuf 创建 EGLImage，
/// 再绑定到 GL 纹理实现零拷贝。</para>
/// <para><b>GPU 零拷贝路径</b>：VAAPI → dmabuf → EGLImage → GLTexture → OpenGLRenderer → EGL → Display</para>
/// <para>V1 桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// EGL 互操作属 Phase 2 目标（Linux OpenGL 渲染器）。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——EGL 调用是同步边界，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class EglInterop
{
    /// <summary>
    /// 从 dmabuf 文件描述符创建 EGLImage（导入 VAAPI / Vulkan 纹理）。
    /// </summary>
    /// <param name="display">EGLDisplay 句柄。</param>
    /// <param name="dmabufFd">dmabuf 文件描述符。</param>
    /// <param name="width">图像宽度。</param>
    /// <param name="height">图像高度。</param>
    /// <returns>EGLImage 句柄。</returns>
    public nint CreateEglImageFromDmaBuf(nint display, int dmabufFd, int width, int height)
        => throw new NotSupportedException("EGL dmabuf 互操作尚未实现。Phase 2 目标。");

    /// <summary>
    /// 将 EGLImage 绑定为 OpenGL 纹理（零拷贝）。
    /// </summary>
    /// <param name="display">EGLDisplay 句柄。</param>
    /// <param name="eglImage">EGLImage 句柄。</param>
    /// <returns>OpenGL 纹理 ID。</returns>
    public int BindEglImageToTexture(nint display, nint eglImage)
        => throw new NotSupportedException("EGLImage → GLTexture 绑定尚未实现。");

    /// <summary>
    /// 销毁 EGLImage。
    /// </summary>
    /// <param name="display">EGLDisplay 句柄。</param>
    /// <param name="eglImage">EGLImage 句柄。</param>
    public void DestroyEglImage(nint display, nint eglImage)
        => throw new NotSupportedException("EGL 资源管理尚未实现。");
}

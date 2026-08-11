using System.Runtime.InteropServices;

namespace LingFan.Media.Platforms.Linux;

/// <summary>
/// EGL 互操作——DMABuf → EGLImage → GL 纹理零拷贝路径。
/// </summary>
/// <remarks>
/// <para>职责：通过 <c>EGL_EXT_image_dma_buf_import</c> 扩展从 dmabuf 创建 EGLImage，
/// 再经 <c>glEGLImageTargetTexture2DOES</c> 绑定到 GL 纹理实现零拷贝。</para>
/// <para><b>GPU 零拷贝路径</b>：VAAPI 硬解 → dmabuf → EGLImage → GLTexture → OpenGLRenderer → EGL → Display。</para>
/// <para><b>平台边界</b>：仅 Linux 有效；非 Linux 调用抛 <see cref="PlatformNotSupportedException"/>
/// （继承自 <see cref="NotSupportedException"/>，与桩契约兼容）。编译期跨平台可编译——
/// <c>libEGL.so.1</c> 仅在首次调用时加载。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——EGL/GL 调用是同步原生边界，无 I/O await；
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步，故保持同步。</para>
/// <para><b>AOT 兼容</b>：sealed 类；<see cref="LibraryImportAttribute"/> 源生成 P/Invoke +
/// <c>delegate* unmanaged</c> 非托管函数指针承载 <c>eglGetProcAddress</c> 返回的扩展入口——零反射、零动态代码。</para>
/// </remarks>
public sealed unsafe partial class EglInterop
{
    // ── EGL 常量（EGL/eglext.h：EGL_EXT_image_dma_buf_import）──
    private const int EGL_NONE = 0x3038;
    private const int EGL_WIDTH = 0x3057;
    private const int EGL_HEIGHT = 0x3056;
    private const int EGL_LINUX_DMA_BUF_EXT = 0x3270;
    private const int EGL_LINUX_DRM_FOURCC_EXT = 0x3271;
    private const int EGL_DMA_BUF_PLANE0_FD_EXT = 0x3272;
    private const int EGL_DMA_BUF_PLANE0_OFFSET_EXT = 0x3273;
    private const int EGL_DMA_BUF_PLANE0_PITCH_EXT = 0x3274;

    /// <summary>DRM_FORMAT_ARGB8888（'AR24' little-endian fourcc）——BGRA 内存序，与解码器 BGRA32 输出对应。</summary>
    private const int DrmFormatArgb8888 = 0x34325241;

    // ── GL 常量 ──
    private const int GL_TEXTURE_2D = 0x0DE1;

    // ── 扩展函数指针缓存（eglGetProcAddress 首次解析后缓存；EGL 规范保证进程内地址稳定）──
    private static delegate* unmanaged[Cdecl]<nint, nint, int, nint, int*, nint> _eglCreateImageKHR;
    private static delegate* unmanaged[Cdecl]<nint, nint, uint> _eglDestroyImageKHR;
    private static delegate* unmanaged[Cdecl]<int, nint, void> _glEGLImageTargetTexture2DOES;

    /// <summary>Linux libEGL 入口：解析 EGL/GL 扩展函数地址（同步原生调用）。</summary>
    [LibraryImport("libEGL.so.1", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint eglGetProcAddress(string procName);

    /// <summary>Linux libGLESv2：生成 GL 纹理对象。</summary>
    [LibraryImport("libGLESv2.so.2")]
    private static partial void glGenTextures(int n, uint* textures);

    /// <summary>Linux libGLESv2：绑定 GL 纹理。</summary>
    [LibraryImport("libGLESv2.so.2")]
    private static partial void glBindTexture(int target, uint texture);

    /// <summary>
    /// 从 dmabuf 文件描述符创建 EGLImage（单平面 BGRA，DRM_FORMAT_ARGB8888）。
    /// </summary>
    /// <param name="display">EGLDisplay 句柄。</param>
    /// <param name="dmabufFd">dmabuf 文件描述符。</param>
    /// <param name="width">图像宽度。</param>
    /// <param name="height">图像高度。</param>
    /// <returns>EGLImage 句柄。</returns>
    public nint CreateEglImageFromDmaBuf(nint display, int dmabufFd, int width, int height)
        => CreateEglImageFromDmaBuf(display, dmabufFd, width, height, DrmFormatArgb8888, width * 4, 0);

    /// <summary>
    /// 从 dmabuf 文件描述符创建 EGLImage（完整参数：fourcc / pitch / offset）。
    /// </summary>
    /// <param name="display">EGLDisplay 句柄。</param>
    /// <param name="dmabufFd">dmabuf 文件描述符。</param>
    /// <param name="width">图像宽度。</param>
    /// <param name="height">图像高度。</param>
    /// <param name="drmFourcc">DRM fourcc 像素格式（如 DRM_FORMAT_ARGB8888 = 0x34325241）。</param>
    /// <param name="pitch">平面 0 行距（字节）。</param>
    /// <param name="offset">平面 0 偏移（字节）。</param>
    /// <returns>EGLImage 句柄。</returns>
    public nint CreateEglImageFromDmaBuf(nint display, int dmabufFd, int width, int height, int drmFourcc, int pitch, int offset)
    {
        ThrowIfNotLinux();
        if (display == 0) throw new ArgumentException("EGLDisplay 无效。", nameof(display));
        if (dmabufFd < 0) throw new ArgumentOutOfRangeException(nameof(dmabufFd), "dmabuf fd 无效。");
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "尺寸必须为正数。");

        var create = GetEglCreateImage();

        // EGL_EXT_image_dma_buf_import 属性表（单平面）。
        int* attribs = stackalloc int[]
        {
            EGL_WIDTH, width,
            EGL_HEIGHT, height,
            EGL_LINUX_DRM_FOURCC_EXT, drmFourcc,
            EGL_DMA_BUF_PLANE0_FD_EXT, dmabufFd,
            EGL_DMA_BUF_PLANE0_OFFSET_EXT, offset,
            EGL_DMA_BUF_PLANE0_PITCH_EXT, pitch,
            EGL_NONE,
        };

        // ctx 须为 EGL_NO_CONTEXT(0)、buffer 须为 NULL（规范要求）。
        nint image = create(display, 0, EGL_LINUX_DMA_BUF_EXT, 0, attribs);
        if (image == 0) throw new InvalidOperationException("eglCreateImageKHR 从 dmabuf 创建 EGLImage 失败。");
        return image;
    }

    /// <summary>
    /// 将 EGLImage 绑定为 OpenGL 纹理（零拷贝，glEGLImageTargetTexture2DOES）。
    /// </summary>
    /// <remarks>须在持有有效 GL 上下文的线程调用。</remarks>
    /// <param name="display">EGLDisplay 句柄（保留参数，扩展入口解析无需 display）。</param>
    /// <param name="eglImage">EGLImage 句柄。</param>
    /// <returns>OpenGL 纹理 ID。</returns>
    public int BindEglImageToTexture(nint display, nint eglImage)
    {
        ThrowIfNotLinux();
        if (eglImage == 0) throw new ArgumentException("EGLImage 句柄无效。", nameof(eglImage));

        var target2D = GetGlEglImageTarget();

        uint texture;
        glGenTextures(1, &texture);
        glBindTexture(GL_TEXTURE_2D, texture);
        target2D(GL_TEXTURE_2D, eglImage);
        return (int)texture;
    }

    /// <summary>
    /// 销毁 EGLImage（eglDestroyImageKHR）。
    /// </summary>
    /// <param name="display">EGLDisplay 句柄。</param>
    /// <param name="eglImage">EGLImage 句柄。</param>
    public void DestroyEglImage(nint display, nint eglImage)
    {
        ThrowIfNotLinux();
        if (display == 0 || eglImage == 0) return; // 幂等：无效句柄直接忽略

        var destroy = GetEglDestroyImage();
        _ = destroy(display, eglImage);
    }

    // ── 扩展入口解析（首次调用缓存；同步原生调用）──

    private static delegate* unmanaged[Cdecl]<nint, nint, int, nint, int*, nint> GetEglCreateImage()
    {
        if (_eglCreateImageKHR is null)
        {
            nint p = eglGetProcAddress("eglCreateImageKHR");
            if (p == 0) throw new NotSupportedException("当前 EGL 实现不支持 eglCreateImageKHR（需 EGL_KHR_image_base + EGL_EXT_image_dma_buf_import）。");
            _eglCreateImageKHR = (delegate* unmanaged[Cdecl]<nint, nint, int, nint, int*, nint>)p;
        }
        return _eglCreateImageKHR;
    }

    private static delegate* unmanaged[Cdecl]<nint, nint, uint> GetEglDestroyImage()
    {
        if (_eglDestroyImageKHR is null)
        {
            nint p = eglGetProcAddress("eglDestroyImageKHR");
            if (p == 0) throw new NotSupportedException("当前 EGL 实现不支持 eglDestroyImageKHR。");
            _eglDestroyImageKHR = (delegate* unmanaged[Cdecl]<nint, nint, uint>)p;
        }
        return _eglDestroyImageKHR;
    }

    private static delegate* unmanaged[Cdecl]<int, nint, void> GetGlEglImageTarget()
    {
        if (_glEGLImageTargetTexture2DOES is null)
        {
            nint p = eglGetProcAddress("glEGLImageTargetTexture2DOES");
            if (p == 0) throw new NotSupportedException("当前 GL 实现不支持 glEGLImageTargetTexture2DOES（需 GL_OES_EGL_image）。");
            _glEGLImageTargetTexture2DOES = (delegate* unmanaged[Cdecl]<int, nint, void>)p;
        }
        return _glEGLImageTargetTexture2DOES;
    }

    private static void ThrowIfNotLinux()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("EGL dmabuf 互操作仅支持 Linux。");
    }
}

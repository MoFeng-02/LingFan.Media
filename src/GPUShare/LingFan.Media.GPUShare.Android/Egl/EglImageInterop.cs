using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Android.Egl;

/// <summary>
/// EGLImage 导入互操作（Android）：把 <c>AHardwareBuffer</c> 经
/// <c>EGL_NATIVE_BUFFER_ANDROID</c> 导入为 <c>EGLImageKHR</c>，再绑定到普通
/// <c>GL_TEXTURE_2D</c> 采样（非 external OES）——GL 世界内部的零拷贝共享。
/// </summary>
/// <remarks>
/// <para><b>共享模型</b>：<c>EGLImageKHR</c> 是 <b>display 级对象</b>——Android 上
/// <c>eglGetDisplay(EGL_DEFAULT_DISPLAY)</c> 为进程单例，因此生产者上下文与宿主 UI 渲染
/// 上下文天然位于同一 display，导入后即可跨上下文采样，无需共享上下文组或外部句柄导出。</para>
/// <para><b>纹理生命周期</b>：<see cref="CreateTextureFromEglImage"/> 把 EGLImage 绑定到纹理后，
/// 纹理持有图像数据的引用——即便随后 <see cref="DestroyImage"/> 销毁 EGLImage 对象，
/// 已导入的纹理内容仍有效（Khronos 导入语义），调用方按 GL 延迟删除规则释放纹理名即可。</para>
/// <para><b>线程</b>：GL 调用（<see cref="CreateTextureFromEglImage"/>）须在调用方目标
/// EGL 上下文为 current 的线程执行；EGL 调用（Create/DestroyImage）线程安全。</para>
/// <para><b>AOT 兼容</b>：全部 <c>[LibraryImport]</c> / <c>eglGetProcAddress</c> 动态解析，零反射。</para>
/// </remarks>
public static unsafe partial class EglImageInterop
{
    // EGL 常量（EGL_ANDROID_get_native_buffer / EGL_KHR_image）。
    // EglImagePreservedKhr 须为 Khronos 官方值 0x30D2（eglext.h EGL_KHR_image_base）；早期误写
    // 0x309B（实为 EGL 1.4 的 EGL_MULTISAMPLE_RESOLVE_BOX，不相干枚举）——驱动容忍未知属性未报
    // EGL_BAD_ATTRIBUTE，但 EGL_IMAGE_PRESERVED（保留 buffer 内容）声明从未真正生效。
    // 本常量为仓内 EglImagePreservedKhr 唯一定义：Bridge/AndroidAhbRgbaBridge.cs 直接引用此处，勿在本文件外复刻。
    private const uint EglNativeBufferAndroid = 0x3140;
    internal const int EglImagePreservedKhr = 0x30D2;
    private const uint EglTrue = 1;
    private const uint EglNone = 0x3038;
    private const nint EglNoImageKhr = 0;

    // GLES 常量（GLES 3.0 核心）。
    private const uint GlTexture2D = 0x0DE1;
    private const uint GlLinear = 0x2601;
    private const uint GlTextureMagFilter = 0x2801;
    private const uint GlTextureMinFilter = 0x2800;

    private static nint _glEglImageTargetTexture2DOES; // 0 = 未解析

    [LibraryImport("libEGL.so", EntryPoint = "eglGetNativeClientBufferANDROID")]
    private static partial nint eglGetNativeClientBufferANDROID(nint hardwareBuffer);

    [LibraryImport("libEGL.so", EntryPoint = "eglCreateImageKHR")]
    private static partial nint eglCreateImageKHR(nint display, nint context, uint target, nint clientBuffer, uint* attribs);

    [LibraryImport("libEGL.so", EntryPoint = "eglDestroyImageKHR")]
    private static partial uint eglDestroyImageKHR(nint display, nint image);

    [LibraryImport("libEGL.so", EntryPoint = "eglGetCurrentDisplay")]
    private static partial nint eglGetCurrentDisplay();

    [LibraryImport("libEGL.so", EntryPoint = "eglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint eglGetProcAddress(string name);

    [LibraryImport("libEGL.so", EntryPoint = "eglGetError")]
    private static partial uint eglGetError();

    /// <summary>当前线程 current 的 EGLDisplay（诊断对表用；无 current 时返回 0）。</summary>
    public static nint QueryCurrentDisplay() => eglGetCurrentDisplay();

    [LibraryImport("libGLESv2.so", EntryPoint = "glGetError")]
    private static partial uint glGetErrorGles();

    [LibraryImport("libGLESv2.so", EntryPoint = "glGenTextures")]
    private static partial void glGenTextures(int n, uint* textures);

    [LibraryImport("libGLESv2.so", EntryPoint = "glBindTexture")]
    private static partial void glBindTexture(uint target, uint texture);

    [LibraryImport("libGLESv2.so", EntryPoint = "glDeleteTextures")]
    private static partial void glDeleteTextures(int n, uint* textures);

    [LibraryImport("libGLESv2.so", EntryPoint = "glTexParameteri")]
    private static partial void glTexParameteri(uint target, uint pname, int param);

    /// <summary>
    /// 把 <c>AHardwareBuffer</c> 导入为指定 EGLDisplay 上的 <c>EGLImageKHR</c>。
    /// 失败抛 <see cref="InvalidOperationException"/>（调用方据此回退下一个共享表面源）。
    /// </summary>
    /// <param name="display">生产者 EGLDisplay（EGLImage 为 display 级对象，须与导入纹理的上下文同 display）。</param>
    /// <param name="ahbHandle">原生 <c>AHardwareBuffer</c> 句柄。</param>
    /// <returns>EGLImageKHR 句柄；销毁须经 <see cref="DestroyImage"/>。</returns>
    public static nint CreateImageFromHardwareBuffer(nint display, nint ahbHandle)
    {
        if (display == nint.Zero)
            throw new InvalidOperationException("EGL：display 无效（0），无法创建 EGLImage。");

        var clientBuffer = eglGetNativeClientBufferANDROID(ahbHandle);
        if (clientBuffer == nint.Zero)
            throw new InvalidOperationException($"EGL：eglGetNativeClientBufferANDROID 失败（0x{eglGetError():X8}）。");

        // attribs 序列：EGL_IMAGE_PRESERVED_KHR=EGL_TRUE, EGL_NONE。
        uint* attrs = stackalloc uint[] { EglImagePreservedKhr, EglTrue, EglNone, 0 };
        var image = eglCreateImageKHR(display, nint.Zero, EglNativeBufferAndroid, clientBuffer, attrs);
        if (image == EglNoImageKhr)
            throw new InvalidOperationException($"EGL：eglCreateImageKHR(EGL_NATIVE_BUFFER_ANDROID) 失败（0x{eglGetError():X8}）。");
        return image;
    }

    /// <summary>销毁 <c>EGLImageKHR</c>（已导入的纹理不受影响——纹理持有图像引用）。</summary>
    /// <param name="display">创建该 EGLImage 的 EGLDisplay。</param>
    /// <param name="eglImage">EGLImageKHR 句柄。</param>
    public static void DestroyImage(nint display, nint eglImage)
    {
        if (display != nint.Zero && eglImage != nint.Zero)
            eglDestroyImageKHR(display, eglImage);
    }

    /// <summary>
    /// 在当前 GL 上下文创建纹理并把 EGLImage 绑定为其数据源
    /// （<c>glEGLImageTargetTexture2DOES(GL_TEXTURE_2D)</c>——普通 2D 纹理，可被 Skia 直采）。
    /// </summary>
    /// <param name="eglImage">EGLImageKHR 句柄（display 级，任意同 display 上下文可导入）。</param>
    /// <returns>新创建的 GL 纹理对象名（调用方绘制后按 GL 延迟删除规则释放）。</returns>
    /// <exception cref="InvalidOperationException">纹理创建或 EGLImage 绑定失败（含 GL 错误码）。</exception>
    public static uint CreateTextureFromEglImage(nint eglImage)
    {
        EnsureGlExtension();
        uint texture = 0;
        glGenTextures(1, &texture);
        if (texture == 0)
            throw new InvalidOperationException($"GLES：glGenTextures 返回 0（GL 错误 0x{glGetErrorGles():X8}）。");
        glBindTexture(GlTexture2D, texture);
        glEglImageTargetTexture2DOES(GlTexture2D, eglImage);
        var err = glGetErrorGles();
        if (err != 0)
            throw new InvalidOperationException($"GLES：glEGLImageTargetTexture2DOES 失败（GL 错误 0x{err:X8}，EGLImage={eglImage:X}）——常见原因：EGLImage 与当前上下文不同 display / 格式不被支持。");
        glTexParameteri(GlTexture2D, GlTextureMagFilter, (int)GlLinear);
        glTexParameteri(GlTexture2D, GlTextureMinFilter, (int)GlLinear);
        glBindTexture(GlTexture2D, 0);
        return texture;
    }

    /// <summary>删除 GL 纹理对象名（已记录的采样命令由 GL 延迟删除语义保活）。</summary>
    public static void DeleteTexture(uint texture)
    {
        if (texture != 0)
            glDeleteTextures(1, &texture);
    }

    private static void glEglImageTargetTexture2DOES(uint target, nint image)
    {
        if (_glEglImageTargetTexture2DOES == 0)
        {
            _glEglImageTargetTexture2DOES = eglGetProcAddress("glEGLImageTargetTexture2DOES");
            if (_glEglImageTargetTexture2DOES == 0)
                throw new InvalidOperationException("GLES：glEGLImageTargetTexture2DOES 无法解析（缺失 OES_EGL_image 扩展）。");
        }

        var proc = (delegate* unmanaged[Stdcall]<uint, nint, void>)_glEglImageTargetTexture2DOES;
        proc(target, image);
    }

    private static void EnsureGlExtension()
    {
        if (_glEglImageTargetTexture2DOES == 0)
        {
            _glEglImageTargetTexture2DOES = eglGetProcAddress("glEGLImageTargetTexture2DOES");
            if (_glEglImageTargetTexture2DOES == 0)
                throw new InvalidOperationException("GLES：glEGLImageTargetTexture2DOES 无法解析（缺失 OES_EGL_image 扩展）。");
        }
    }
}

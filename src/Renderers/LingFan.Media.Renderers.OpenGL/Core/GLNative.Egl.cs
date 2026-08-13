using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// <see cref="GLNative"/> 的 Linux EGL 引导符号（仅 Linux 调用）。
/// </summary>
/// <remarks>
/// <para>Windows 上这些 [LibraryImport] 永不被调用（调用方均以 <see cref="OperatingSystem.IsLinux"/> 守卫），
/// 且解析器对中性名 <c>"EGL"</c> 在 Windows 上交回 <see langword="null"/>，故无运行期加载失败。</para>
/// <para><c>eglGetProcAddress</c> 为私有，供核心 <see cref="GLNative.GetProcAddress"/> 在 Linux 上解析 GL 现代函数；
/// 须在 EGL 上下文 current 后调用（否则返回 <see langword="null"/>）。</para>
/// <para>EGL 句柄类型（EGLDisplay / EGLConfig / EGLSurface / EGLContext）按 ABI 统一映射为 <c>nint</c>；
/// EGLint 为 32 位整数，用 <c>int</c>；属性表（EGLint*）以 <c>int*</c> 传递。</para>
/// </remarks>
internal static unsafe partial class GLNative
{
    [LibraryImport("EGL", EntryPoint = "eglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint eglGetProcAddress(byte* name);

    [LibraryImport("EGL", EntryPoint = "eglBindAPI")]
    public static partial int eglBindAPI(uint api);

    [LibraryImport("EGL", EntryPoint = "eglGetDisplay")]
    public static partial nint eglGetDisplay(nint displayId);

    [LibraryImport("EGL", EntryPoint = "eglInitialize")]
    public static partial int eglInitialize(nint display, int* major, int* minor);

    [LibraryImport("EGL", EntryPoint = "eglGetConfigs")]
    public static partial int eglGetConfigs(nint display, nint* configs, int configSize, int* numConfig);

    [LibraryImport("EGL", EntryPoint = "eglChooseConfig")]
    public static partial int eglChooseConfig(nint display, int* attribList, nint* configs, int configSize, int* numConfig);

    [LibraryImport("EGL", EntryPoint = "eglGetConfigAttrib")]
    public static partial int eglGetConfigAttrib(nint display, nint config, int attribute, int* value);

    [LibraryImport("EGL", EntryPoint = "eglCreateWindowSurface")]
    public static partial nint eglCreateWindowSurface(nint display, nint config, nint window, int* attribList);

    [LibraryImport("EGL", EntryPoint = "eglCreatePbufferSurface")]
    public static partial nint eglCreatePbufferSurface(nint display, nint config, int* attribList);

    [LibraryImport("EGL", EntryPoint = "eglCreateContext")]
    public static partial nint eglCreateContext(nint display, nint config, nint shareContext, int* attribList);

    [LibraryImport("EGL", EntryPoint = "eglMakeCurrent")]
    public static partial int eglMakeCurrent(nint display, nint draw, nint read, nint context);

    [LibraryImport("EGL", EntryPoint = "eglSwapBuffers")]
    public static partial int eglSwapBuffers(nint display, nint surface);

    [LibraryImport("EGL", EntryPoint = "eglSwapInterval")]
    public static partial int eglSwapInterval(nint display, int interval);

    [LibraryImport("EGL", EntryPoint = "eglGetError")]
    public static partial int eglGetError();

    [LibraryImport("EGL", EntryPoint = "eglDestroyContext")]
    public static partial int eglDestroyContext(nint display, nint context);

    [LibraryImport("EGL", EntryPoint = "eglDestroySurface")]
    public static partial int eglDestroySurface(nint display, nint surface);

    [LibraryImport("EGL", EntryPoint = "eglTerminate")]
    public static partial int eglTerminate(nint display);
}

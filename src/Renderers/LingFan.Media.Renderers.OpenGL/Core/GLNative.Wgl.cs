using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// <see cref="GLNative"/> 的 Windows WGL / Win32 引导符号（仅 Windows 调用）。
/// </summary>
/// <remarks>
/// <para>Linux 上这些 [LibraryImport] 永不被调用（调用方均以 <see cref="OperatingSystem.IsWindows"/> 守卫），
/// 故解析器无需为 Linux 提供 opengl32 / gdi32 / user32 映射。</para>
/// <para><c>wglGetProcAddress</c> 为私有，供核心 <see cref="GLNative.GetProcAddress"/> 在 Windows 上解析 GL 现代函数；
/// GL 上下文建立前调用会静默返回 <see langword="null"/>（符合 WGL 规范）。</para>
/// <para>BOOL 返回（<c>wglMakeCurrent</c> / <c>SwapBuffers</c> 等）按 Win32 实际宽度用 <c>int</c>，
/// 避免 <c>byte</c> 截断导致的栈失衡。</para>
/// </remarks>
internal static unsafe partial class GLNative
{
    [LibraryImport("opengl32", EntryPoint = "wglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint wglGetProcAddress(byte* name);

    [LibraryImport("opengl32", EntryPoint = "wglCreateContext")]
    public static partial nint wglCreateContext(nint hdc);

    [LibraryImport("opengl32", EntryPoint = "wglMakeCurrent")]
    public static partial int wglMakeCurrent(nint hdc, nint hglrc);

    [LibraryImport("opengl32", EntryPoint = "wglDeleteContext")]
    public static partial int wglDeleteContext(nint hglrc);

    [LibraryImport("opengl32", EntryPoint = "wglGetCurrentContext")]
    public static partial nint wglGetCurrentContext();

    [LibraryImport("opengl32", EntryPoint = "wglGetCurrentDC")]
    public static partial nint wglGetCurrentDC();

    // SwapBuffers 属 GDI32（opengl32 不导出），故库名用 "gdi32"。
    [LibraryImport("gdi32", EntryPoint = "SwapBuffers")]
    public static partial int SwapBuffers(nint hdc);

    // GDI32：像素格式（Win32 上下文创建前置，PIXELFORMATDESCRIPTOR 由调用方定义并 pin 后传入）
    [LibraryImport("gdi32", EntryPoint = "ChoosePixelFormat")]
    public static partial int ChoosePixelFormat(nint hdc, void* ppfd);

    [LibraryImport("gdi32", EntryPoint = "SetPixelFormat")]
    public static partial int SetPixelFormat(nint hdc, int pixelFormat, void* ppfd);

    [LibraryImport("gdi32", EntryPoint = "DescribePixelFormat")]
    public static partial int DescribePixelFormat(nint hdc, int pixelFormat, uint nBytes, void* ppfd);

    // USER32：取/释放窗口 DC（HWND → HDC）
    [LibraryImport("user32", EntryPoint = "GetDC")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32", EntryPoint = "ReleaseDC")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);
}

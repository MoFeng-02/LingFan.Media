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

    // ── 离屏设备上下文（隐藏窗口）所需 user32 P/Invoke（AOT 合规：[LibraryImport] + EntryPoint="XxxW" + Utf16）──
    // 模式移植自 OpenGLHeadfulPlaybackProbe（已验证）：WNDCLASSEXW 的 lpfnWndProc 以函数指针传入、字符串成员以 IntPtr 传入。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [LibraryImport("user32", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [LibraryImport("user32", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32", EntryPoint = "DestroyWindow")]
    public static partial int DestroyWindow(nint hwnd);

    [LibraryImport("gdi32", EntryPoint = "GetStockObject")]
    public static partial nint GetStockObject(int i);

    [LibraryImport("user32", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursorW(nint hInstance, nint lpCursorName);
}

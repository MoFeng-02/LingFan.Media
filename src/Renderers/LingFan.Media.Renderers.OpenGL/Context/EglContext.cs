using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.OpenGL.Context;

/// <summary>
/// Linux EGL 桌面 GL 上下文（libEGL，X11 Window）。
/// </summary>
/// <remarks>
/// <para>创建流程：<c>eglBindAPI(EGL_OPENGL_API)</c> → <c>eglGetDisplay</c> → <c>eglInitialize</c> →
/// <c>eglChooseConfig</c>（RGBA8 + 深度 24 + 模板 8，桌面 GL 可渲染）→
/// <c>eglCreateContext</c>（客户端版本 3）→ <c>eglCreateWindowSurface</c> → <c>eglMakeCurrent</c> →
/// <see cref="GLNative.LoadModern"/>。</para>
/// <para>Display 由调用方经 <see cref="X11WindowHandle"/> 提供（本类型不引入 X11 绑定）；
/// 仅在 <see cref="OperatingSystem.IsLinux"/> 下被构造，Windows 上永不被实例化。</para>
/// </remarks>
internal sealed unsafe class EglContext : IGlContext
{
    private const uint EglSurfaceType = 0x3033;
    private const uint EglWindowBit = 0x0004;
    private const uint EglRenderableType = 0x3040;
    private const uint EglOpenglBit = 0x0008;
    private const uint EglRedSize = 0x3024;
    private const uint EglGreenSize = 0x3023;
    private const uint EglBlueSize = 0x3022;
    private const uint EglAlphaSize = 0x3021;
    private const uint EglDepthSize = 0x3025;
    private const uint EglStencilSize = 0x3026;
    private const uint EglNone = 0x3038;
    private const uint EglContextClientVersion = 0x3098;
    private const uint EglOpenglApi = 0x30A0;

    private nint _display;
    private nint _surface;
    private nint _context;
    private readonly ILogger? _logger;

    public int GlMajor { get; private set; } = 3;
    public int GlMinor { get; private set; } = 3;

    public EglContext(nint display, nint window, ILogger? logger = null)
    {
        if (display == nint.Zero)
            throw new ArgumentNullException(nameof(display));
        if (window == nint.Zero)
            throw new ArgumentNullException(nameof(window));
        _logger = logger;
        Create(display, window);
    }

    private void Create(nint display, nint window)
    {
        _display = GLNative.eglGetDisplay(display);
        if (_display == nint.Zero)
            throw new InvalidOperationException("EGL：eglGetDisplay 失败（Display 无效）。");

        int major = 0, minor = 0;
        if (GLNative.eglInitialize(_display, &major, &minor) == 0)
            throw new InvalidOperationException($"EGL：eglInitialize 失败（0x{GLNative.eglGetError():X8}）。");

        if (GLNative.eglBindAPI(EglOpenglApi) == 0)
            throw new InvalidOperationException("EGL：eglBindAPI(EGL_OPENGL_API) 失败（无法绑定桌面 GL）。");

        int[] configAttribs =
        {
            (int)EglSurfaceType, (int)EglWindowBit,
            (int)EglRenderableType, (int)EglOpenglBit,
            (int)EglRedSize, 8,
            (int)EglGreenSize, 8,
            (int)EglBlueSize, 8,
            (int)EglAlphaSize, 8,
            (int)EglDepthSize, 24,
            (int)EglStencilSize, 8,
            (int)EglNone,
        };
        nint config = nint.Zero;
        int numConfig = 0;
        fixed (int* a = configAttribs)
        {
            if (GLNative.eglChooseConfig(_display, a, &config, 1, &numConfig) == 0 || numConfig == 0)
                throw new InvalidOperationException($"EGL：eglChooseConfig 失败（0x{GLNative.eglGetError():X8}）。");
        }

        int[] ctxAttribs =
        {
            (int)EglContextClientVersion, 3,
            (int)EglNone,
        };
        fixed (int* c = ctxAttribs)
            _context = GLNative.eglCreateContext(_display, config, nint.Zero, c);
        if (_context == nint.Zero)
            throw new InvalidOperationException($"EGL：eglCreateContext 失败（0x{GLNative.eglGetError():X8}）。");

        int[] surfAttribs = { (int)EglNone };
        fixed (int* s = surfAttribs)
            _surface = GLNative.eglCreateWindowSurface(_display, config, window, s);
        if (_surface == nint.Zero)
            throw new InvalidOperationException($"EGL：eglCreateWindowSurface 失败（0x{GLNative.eglGetError():X8}）。");

        if (GLNative.eglMakeCurrent(_display, _surface, _surface, _context) == 0)
            throw new InvalidOperationException($"EGL：eglMakeCurrent 失败（0x{GLNative.eglGetError():X8}）。");

        GLNative.LoadModern();
        GlVersionQuery.Query(out int vMajor, out int vMinor);
        if (vMajor != 0) GlMajor = vMajor;
        if (vMinor != 0) GlMinor = vMinor;
        _logger?.LogInformation("EGL：GL 上下文建立成功（EGL {EMajor}.{EMinor}，GL {GMajor}.{GMinor}）。",
            major, minor, GlMajor, GlMinor);

        // 释放：EGL 上下文具线程亲和性。创建于 Attach 线程，渲染在管线线程 Present 中发生，
        // 需在此解绑，使渲染线程可经 MakeCurrent 重新绑定（否则同 WGL 会因已有线程占用而失败）。
        GLNative.eglMakeCurrent(_display, nint.Zero, nint.Zero, nint.Zero);
    }

    public void MakeCurrent()
    {
        if (_context != nint.Zero)
            GLNative.eglMakeCurrent(_display, _surface, _surface, _context);
    }

    public void ReleaseCurrent() => GLNative.eglMakeCurrent(_display, nint.Zero, nint.Zero, nint.Zero);

    public void SwapBuffers() => GLNative.eglSwapBuffers(_display, _surface);

    public void Dispose()
    {
        if (_display != nint.Zero)
        {
            GLNative.eglMakeCurrent(_display, nint.Zero, nint.Zero, nint.Zero);
            if (_surface != nint.Zero) GLNative.eglDestroySurface(_display, _surface);
            if (_context != nint.Zero) GLNative.eglDestroyContext(_display, _context);
            GLNative.eglTerminate(_display);
        }
        _surface = nint.Zero;
        _context = nint.Zero;
        _display = nint.Zero;
    }
}

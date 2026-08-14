using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.OpenGLES.Context;

/// <summary>
/// Android EGL / OpenGL ES 上屏上下文（libEGL + libGLESv2，ANativeWindow）。
/// </summary>
/// <remarks>
/// <para>创建流程：<c>eglGetDisplay(EGL_DEFAULT_DISPLAY)</c> → <c>eglInitialize</c> →
/// <c>eglBindAPI(EGL_OPENGL_ES_API)</c> → <c>eglChooseConfig</c>（RGBA8 + 深度 24 + 模板 8，GLES 3.0 可渲染）→
/// <c>eglCreateContext</c>（客户端版本 3）→ <c>eglCreateWindowSurface</c>（ANativeWindow*）→ <c>eglMakeCurrent</c>。</para>
/// <para><b>与桌面 EGL（<see cref="LingFan.Media.Renderers.OpenGL"/> 的 EglContext）的本质差异</b>：
/// 桌面 GL 绑定 <c>EGL_OPENGL_API</c> + <c>EGL_OPENGL_BIT</c> 渲染类型（桌面 GL 3.3）；
/// Android GLES 必须绑定 <c>EGL_OPENGL_ES_API</c> + <c>EGL_OPENGL_ES3_BIT</c> 渲染类型（OpenGL ES 3.0），
/// 且窗口表面由 <c>ANativeWindow*</c>（经 <see cref="IRenderTarget.NativeHandle"/> 以 <see cref="RenderHandleType.Pointer"/> 传入的单一 IntPtr）提供，
/// 而非桌面 X11 的 <c>Display* + Window</c> 复合句柄。</para>
/// <para>仅在 <see cref="OperatingSystem.IsAndroid"/> 下被构造；非 Android 平台永不被实例化。</para>
/// </remarks>
internal sealed unsafe class AndroidEglContext : IGlContext
{
    // EGL 常量（按 ABI 映射 int；EGL_NONE / EGL_NO_* 均为 0）
    private const uint EglSurfaceType = 0x3033;
    private const uint EglWindowBit = 0x0004;
    private const uint EglRenderableType = 0x3040;
    private const uint EglOpenglEs3Bit = 0x00000040; // EGL_OPENGL_ES3_BIT
    private const uint EglRedSize = 0x3024;
    private const uint EglGreenSize = 0x3023;
    private const uint EglBlueSize = 0x3022;
    private const uint EglAlphaSize = 0x3021;
    private const uint EglDepthSize = 0x3025;
    private const uint EglStencilSize = 0x3026;
    private const uint EglNone = 0x3038;
    private const uint EglContextClientVersion = 0x3098;
    private const uint EglOpenglEsApi = 0x30A2;      // EGL_OPENGL_ES_API（区别于桌面 EGL_OPENGL_API = 0x30A0）
    private static readonly nint EglDefaultDisplay = nint.Zero; // EGL_DEFAULT_DISPLAY

    private nint _display;
    private nint _surface;
    private nint _context;
    private readonly ILogger? _logger;

    public int GlMajor { get; private set; } = 3;
    public int GlMinor { get; private set; } = 0; // GLES 次版本（3.0）

    /// <summary>GLES 上下文句柄（EGLContext）。</summary>
    public nint ContextHandle => _context;

    /// <summary>平台显示句柄（EGLDisplay）。</summary>
    public nint PlatformDisplay => _display;

    public AndroidEglContext(nint window, ILogger? logger = null)
    {
        if (window == nint.Zero)
            throw new ArgumentNullException(nameof(window));
        _logger = logger;

        // Android 上屏用默认 EGL 显示；ANativeWindow 自带 EGLNativeWindowType 关联。
        _display = GlesNative.eglGetDisplay(EglDefaultDisplay);
        if (_display == nint.Zero)
            throw new InvalidOperationException("EGL：eglGetDisplay(DEFAULT) 失败（无可用 EGL 显示）。");

        int major = 0, minor = 0;
        if (GlesNative.eglInitialize(_display, &major, &minor) == 0)
            throw new InvalidOperationException($"EGL：eglInitialize 失败（0x{GlesNative.eglGetError():X8}）。");

        CreateOnDisplay(window);

        _logger?.LogInformation(
            "EGL(Android)：GLES 上下文建立成功（EGL {EMajor}.{EMinor}，GLES {GMajor}.{GMinor}）。",
            major, minor, GlMajor, GlMinor);
    }

    private void CreateOnDisplay(nint window)
    {
        // Android 必须绑定 GLES API（非桌面 GL API）
        if (GlesNative.eglBindAPI(EglOpenglEsApi) == 0)
            throw new InvalidOperationException("EGL：eglBindAPI(EGL_OPENGL_ES_API) 失败（无法绑定 GLES）。");

        int[] configAttribs =
        {
            (int)EglSurfaceType, (int)EglWindowBit,
            (int)EglRenderableType, (int)EglOpenglEs3Bit,
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
            if (GlesNative.eglChooseConfig(_display, a, &config, 1, &numConfig) == 0 || numConfig == 0)
                throw new InvalidOperationException($"EGL：eglChooseConfig 失败（0x{GlesNative.eglGetError():X8}）。");
        }

        int[] ctxAttribs =
        {
            (int)EglContextClientVersion, 3, // GLES 3.0
            (int)EglNone,
        };
        fixed (int* c = ctxAttribs)
            _context = GlesNative.eglCreateContext(_display, config, nint.Zero, c);
        if (_context == nint.Zero)
            throw new InvalidOperationException($"EGL：eglCreateContext 失败（0x{GlesNative.eglGetError():X8}）。");

        // window 即 ANativeWindow*（opaque，EGLNativeWindowType）；与桌面 X11 的 Window 同为无绑定 IntPtr。
        int[] surfAttribs = { (int)EglNone };
        fixed (int* s = surfAttribs)
            _surface = GlesNative.eglCreateWindowSurface(_display, config, window, s);
        if (_surface == nint.Zero)
            throw new InvalidOperationException($"EGL：eglCreateWindowSurface 失败（0x{GlesNative.eglGetError():X8}）。");

        if (GlesNative.eglMakeCurrent(_display, _surface, _surface, _context) == 0)
            throw new InvalidOperationException($"EGL：eglMakeCurrent 失败（0x{GlesNative.eglGetError():X8}）。");

        // GLES 全部函数由 libGLESv2 直接导出（加载期解析），无需 LoadModern。

        // 释放：GLES 上下文具线程亲和性。创建于 Attach 线程，渲染在管线线程 Present 中发生，
        // 需在此解绑，使渲染线程可经 MakeCurrent 重新绑定。
        GlesNative.eglMakeCurrent(_display, nint.Zero, nint.Zero, nint.Zero);
    }

    public void MakeCurrent()
    {
        if (_context != nint.Zero)
            GlesNative.eglMakeCurrent(_display, _surface, _surface, _context);
    }

    public void ReleaseCurrent() => GlesNative.eglMakeCurrent(_display, nint.Zero, nint.Zero, nint.Zero);

    public void SwapBuffers() => GlesNative.eglSwapBuffers(_display, _surface);

    public void Dispose()
    {
        if (_display != nint.Zero)
        {
            GlesNative.eglMakeCurrent(_display, nint.Zero, nint.Zero, nint.Zero);
            if (_surface != nint.Zero) GlesNative.eglDestroySurface(_display, _surface);
            if (_context != nint.Zero) GlesNative.eglDestroyContext(_display, _context);
            GlesNative.eglTerminate(_display);
        }
        _surface = nint.Zero;
        _context = nint.Zero;
        _display = nint.Zero;
    }
}

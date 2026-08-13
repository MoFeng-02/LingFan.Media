using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.OpenGL.Context;

/// <summary>
/// Windows WGL（opengl32 + gdi32 + user32）GL 上下文。
/// </summary>
/// <remarks>
/// <para>创建流程：<c>GetDC</c> → <c>ChoosePixelFormat</c> / <c>SetPixelFormat</c>（含双缓冲 RGBA32）→
/// 建临时 2.1 兼容上下文以解析扩展 <c>wglCreateContextAttribsARB</c> →
/// 创建 3.3 核心 Profile 上下文并切换、释放临时上下文 → <see cref="GLNative.LoadModern"/> 解析 GL 现代函数。</para>
/// <para>所有 Win32 绑定经 <see cref="GLNative"/>（零反射 <c>[LibraryImport]</c>），无 Silk.NET 依赖。</para>
/// </remarks>
internal sealed unsafe class WglContext : IGlContext
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormatDescriptor
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits;
        public byte cGreenBits;
        public byte cBlueBits;
        public byte cAlphaBits;
        public byte cAccumBits;
        public byte cDepthBits;
        public byte cStencilBits;
        public byte cAuxBuffers;
        public byte iLayerType;
        public byte bReserved;
        public uint dwLayerMask;
        public uint dwVisibleMask;
        public uint dwDamageMask;
    }

    private const uint PfdDrawToWindow = 0x00000001;
    private const uint PfdSupportOpengl = 0x00000020;
    private const uint PfdDoublebuffer = 0x00000004;
    private const byte PfdTypeRgba = 0;
    private const byte PfdMainPlane = 0;

    private const int WglContextMajorVersionArb = 0x2091;
    private const int WglContextMinorVersionArb = 0x2092;
    private const int WglContextProfileMaskArb = 0x2094;
    private const int WglContextCoreProfileBitArb = 0x00000001;

    private nint _hwnd;
    private nint _hdc;
    private nint _hglrc;
    private readonly ILogger? _logger;

    public int GlMajor { get; private set; }
    public int GlMinor { get; private set; }

    /// <summary>GL 上下文句柄（HGLRC）。作为 <see cref="IGpuDeviceContext"/> 的 DeviceHandle / 共享组句柄。</summary>
    public nint ContextHandle => _hglrc;

    /// <summary>平台显示/设备上下文句柄（HDC）。作为 <see cref="IGpuDeviceContext"/> 的 ContextHandle（解码侧 interop 用）。</summary>
    public nint PlatformDisplay => _hdc;

    public WglContext(nint hwnd, ILogger? logger = null, nint shareContext = default)
    {
        if (hwnd == nint.Zero)
            throw new ArgumentNullException(nameof(hwnd));
        _hwnd = hwnd;
        _logger = logger;
        Create(shareContext);
    }

    private void Create(nint shareContext = default)
    {
        _hdc = GLNative.GetDC(_hwnd);
        if (_hdc == nint.Zero)
            throw new InvalidOperationException("WGL：GetDC 失败（HWND 无效）。");

        var pfd = new PixelFormatDescriptor
        {
            nSize = (ushort)sizeof(PixelFormatDescriptor),
            nVersion = 1,
            dwFlags = PfdDrawToWindow | PfdSupportOpengl | PfdDoublebuffer,
            iPixelType = PfdTypeRgba,
            cColorBits = 32,
            cDepthBits = 24,
            cStencilBits = 8,
            iLayerType = PfdMainPlane,
        };

        int pixelFormat = GLNative.ChoosePixelFormat(_hdc, &pfd);
        if (pixelFormat == 0)
            throw new InvalidOperationException("WGL：ChoosePixelFormat 失败。");
        if (GLNative.SetPixelFormat(_hdc, pixelFormat, &pfd) == 0)
            throw new InvalidOperationException("WGL：SetPixelFormat 失败。");

        // 临时 2.1 兼容上下文：扩展函数（wglCreateContextAttribsARB）需 current context 才能经 wglGetProcAddress 解析。
        nint tempRc = GLNative.wglCreateContext(_hdc);
        if (tempRc == nint.Zero)
            throw new InvalidOperationException("WGL：创建临时上下文失败。");
        if (GLNative.wglMakeCurrent(_hdc, tempRc) == 0)
        {
            GLNative.wglDeleteContext(tempRc);
            throw new InvalidOperationException("WGL：临时上下文 MakeCurrent 失败。");
        }

        nint modernRc = nint.Zero;
        nint pAttr = GLNative.GetProcAddress("wglCreateContextAttribsARB");
        if (pAttr != nint.Zero)
        {
            var createAttribs = (delegate* unmanaged<nint, nint, nint, int*, nint>)pAttr;
            int[] attribs =
            {
                WglContextMajorVersionArb, 3,
                WglContextMinorVersionArb, 3,
                WglContextProfileMaskArb, WglContextCoreProfileBitArb,
                0,
            };
            fixed (int* a = attribs)
                modernRc = createAttribs(_hdc, shareContext, nint.Zero, a);
        }

        if (modernRc == nint.Zero)
        {
            // 回退：保留临时 2.1 上下文（着色器函数仍可经 wglGetProcAddress 解析）。
            modernRc = tempRc;
            tempRc = nint.Zero;
            _logger?.LogWarning("WGL：wglCreateContextAttribsARB 不可用，回退至 2.1 兼容上下文。");
        }
        else
        {
            GLNative.wglMakeCurrent(_hdc, modernRc);
            GLNative.wglDeleteContext(tempRc);
        }

        _hglrc = modernRc;
        GLNative.LoadModern();
        GlVersionQuery.Query(out int major, out int minor);
        GlMajor = major;
        GlMinor = minor;
        _logger?.LogInformation("WGL：GL 上下文建立成功，版本 {Major}.{Minor}。", GlMajor, GlMinor);

        // 释放：GL 上下文具线程亲和性。此处创建于 Attach 线程（主线程），
        // 但真正渲染发生在管线线程的 Present 调用中，需在彼处重新 MakeCurrent。
        // 若不在此释放，管线线程的 wglMakeCurrent 会因「上下文已在其他线程 current」而失败，
        // 导致 Present 零帧。故创建后立即解绑，交由渲染线程按需绑定。
        GLNative.wglMakeCurrent(_hdc, nint.Zero);
    }

    public void MakeCurrent()
    {
        if (_hglrc != nint.Zero)
            GLNative.wglMakeCurrent(_hdc, _hglrc);
    }

    public void ReleaseCurrent() => GLNative.wglMakeCurrent(_hdc, nint.Zero);

    public void SwapBuffers() => GLNative.SwapBuffers(_hdc);

    public void Dispose()
    {
        if (_hglrc != nint.Zero)
        {
            GLNative.wglMakeCurrent(nint.Zero, nint.Zero);
            GLNative.wglDeleteContext(_hglrc);
            _hglrc = nint.Zero;
        }
        if (_hdc != nint.Zero && _hwnd != nint.Zero)
        {
            GLNative.ReleaseDC(_hwnd, _hdc);
            _hdc = nint.Zero;
        }
        _hwnd = nint.Zero;
    }

    // ── 工厂级离屏 GL 上下文（隐藏窗口）──
    // 类过程委托须保活，否则被 GC 后 lpfnWndProc 悬空 ⇒ 野调用崩溃（同 OpenGLHeadfulPlaybackProbe）。
    private static WndProcDelegate? _offscreenWndProcKeepAlive;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private const string OffscreenClassName = "LingFanGLDeviceCtx";

    /// <summary>
    /// 创建工厂级离屏 GL 上下文（隐藏 1×1 窗口 + WGL 3.3 core），作为共享组所有者。
    /// 供 <see cref="OpenGLOffscreenDeviceContext"/> 在解码器初始化前建立，使解码侧产出的 GL 纹理
    /// 经共享组对渲染器 on-screen 上下文可见（零拷贝路径的治本基础）。
    /// </summary>
    public static WglContext CreateOffscreen(ILogger? logger = null)
    {
        nint hwnd = CreateHiddenWindow();
        return new WglContext(hwnd, logger);
    }

    private static nint CreateHiddenWindow()
    {
        _offscreenWndProcKeepAlive = StaticWndProc; // 保活，防止类过程被 GC
        nint namePtr = nint.Zero;
        try
        {
            namePtr = Marshal.StringToHGlobalUni(OffscreenClassName);
            var wc = new GLNative.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<GLNative.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_offscreenWndProcKeepAlive),
                hbrBackground = GLNative.GetStockObject(4), // BLACK_BRUSH
                lpszClassName = namePtr,
                lpszMenuName = nint.Zero,
            };
            ushort atom = GLNative.RegisterClassExW(ref wc);
            if (atom == 0)
                throw new InvalidOperationException($"WGL 离屏窗口类注册失败(err={Marshal.GetLastPInvokeError()})。");
            nint hwnd = GLNative.CreateWindowExW(0, OffscreenClassName, "", 0, 0, 0, 1, 1,
                nint.Zero, nint.Zero, nint.Zero, nint.Zero);
            if (hwnd == nint.Zero)
                throw new InvalidOperationException($"WGL 离屏窗口创建失败(err={Marshal.GetLastPInvokeError()})。");
            return hwnd;
        }
        finally
        {
            if (namePtr != nint.Zero) Marshal.FreeHGlobal(namePtr);
        }
    }

    private static nint StaticWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
        => GLNative.DefWindowProcW(hWnd, msg, wParam, lParam);
}

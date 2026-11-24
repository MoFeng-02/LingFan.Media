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

    // 窗口所有权：仅 CreateOffscreen 自建的隐藏窗口归本实例释放；
    // 构造注入的宿主 HWND 只借用（GetDC/ReleaseDC 配对），Dispose 不得销毁宿主窗口。
    private readonly bool _ownsWindow;

    public int GlMajor { get; private set; }
    public int GlMinor { get; private set; }

    /// <summary>GL 上下文句柄（HGLRC）。作为 <see cref="IGpuDeviceContext"/> 的 DeviceHandle / 共享组句柄。</summary>
    public nint ContextHandle => _hglrc;

    /// <summary>平台显示/设备上下文句柄（HDC）。作为 <see cref="IGpuDeviceContext"/> 的 ContextHandle（解码侧 interop 用）。</summary>
    public nint PlatformDisplay => _hdc;

    /// <summary>
    /// 在给定窗口上创建 WGL GL 上下文。窗口为借用：本实例只获取/归还其 DC，不负责销毁窗口。
    /// </summary>
    /// <param name="hwnd">宿主窗口句柄（HWND），须有效且生命周期覆盖本实例。</param>
    /// <param name="logger">可选日志记录器。</param>
    /// <param name="shareContext">可选共享组上下文句柄（HGLRC）；非零时新上下文与其共享纹理等资源。</param>
    public WglContext(nint hwnd, ILogger? logger = null, nint shareContext = default)
        : this(hwnd, logger, shareContext, ownsWindow: false)
    {
    }

    private WglContext(nint hwnd, ILogger? logger, nint shareContext, bool ownsWindow)
    {
        if (hwnd == nint.Zero)
            throw new ArgumentNullException(nameof(hwnd));
        _hwnd = hwnd;
        _logger = logger;
        _ownsWindow = ownsWindow;
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
        if (_hglrc == nint.Zero)
            throw new InvalidOperationException("WGL：上下文已释放，无法绑定。");
        // 返回值必须校验：绑定失败（上下文被其他线程占用/驱动瞬态拒绝）时若静默继续，
        // 后续 GL 调用在无效 current 上下文上执行即原生访问违规（glGenTextures 崩溃根因）。
        // 有限重试：共享组上下文在导入线程与呈现线程间交替绑定，驱动对「刚被另一线程解绑」
        // 的上下文可能瞬态拒绝（err=0）；短暂睡眠等待让出方完成解绑，而非整段会话回落 CPU。
        // 用 Sleep(0)/Sleep(1) 让出时间片（自旋不让出调度，对方线程可能恰好没机会跑完解绑）。
        for (int attempt = 0; ; attempt++)
        {
            if (GLNative.wglMakeCurrent(_hdc, _hglrc) != 0)
                return;
            if (attempt >= MakeCurrentRetryCount)
            {
                // 诊断（LINGFAN_WGL_DIAG=1）：穷尽重试后取证 DC/像素格式/上下文归属并尝试重取 DC 自愈。
                // 仅诊断路径执行，常态零开销；用于区分「DC 失效」vs「上下文被占用」vs「驱动拒绝」。
                if (Environment.GetEnvironmentVariable("LINGFAN_WGL_DIAG") == "1")
                    DiagnoseAndTryRecover();
                if (GLNative.wglMakeCurrent(_hdc, _hglrc) != 0)
                    return;
                throw new InvalidOperationException(
                    $"WGL：wglMakeCurrent 失败(err={Marshal.GetLastPInvokeError()})，上下文可能被其他线程占用。" +
                    $"目标 HGLRC=0x{_hglrc:X} HDC=0x{_hdc:X}；本线程 current HGLRC=0x{GLNative.wglGetCurrentContext():X} " +
                    $"HDC=0x{GLNative.wglGetCurrentDC():X}；线程={Environment.CurrentManagedThreadId}。");
            }
            Thread.Sleep(attempt < 8 ? 0 : 1);
        }
    }

    /// <summary>绑定失败取证：DC 有效性、像素格式、窗口归属，并尝试重取 DC 后再绑（诊断开关内调用）。</summary>
    private void DiagnoseAndTryRecover()
    {
        int pf = GLNative.GetPixelFormat(_hdc);
        nint owner = GLNative.WindowFromDC(_hdc);
        _logger?.LogWarning(
            "[WGL-DIAG] 绑定失败取证：HGLRC=0x{Rc:X} HDC=0x{Dc:X} GetPixelFormat={Pf} WindowFromDC=0x{Owner:X}（期望 HWND=0x{Hwnd:X} IsWindow={Alive}）线程={Tid}",
            _hglrc, _hdc, pf, owner, _hwnd, GLNative.IsWindow(_hwnd), Environment.CurrentManagedThreadId);
        if (owner != _hwnd || pf == 0)
        {
            // 诊断：窗口仍存活时倒出消息流水（判断窗口死前是否收到外部销毁消息）。
            if (_ownsWindow && GLNative.IsWindow(_hwnd) != 0)
            {
                _logger?.LogWarning("[WGL-DIAG] 窗口仍存活但 DC 失效——倒出消息流水：");
                DumpMsgLog();
            }
            // DC 已失效/易主：重取窗口 DC 再试（CS_OWNDC 下正常应恒等；不等即系统回收了共享 DC）。
            nint newDc = GLNative.GetDC(_hwnd);
            _logger?.LogWarning("[WGL-DIAG] DC 失效（owner=0x{Owner:X} pf={Pf}），重取 DC=0x{New:X}", owner, pf, newDc);
            if (newDc != nint.Zero && newDc != _hdc)
            {
                GLNative.ReleaseDC(_hwnd, _hdc);
                _hdc = newDc;
            }
            else if (newDc != nint.Zero)
            {
                GLNative.ReleaseDC(_hwnd, newDc);
            }
        }
    }

    // MakeCurrent 瞬态拒绝的重试次数（前 8 次让出时间片，其后 1ms 睡眠；总预算 ~100ms 覆盖调度抖动）。
    private const int MakeCurrentRetryCount = 96;

    public void ReleaseCurrent() => GLNative.wglMakeCurrent(_hdc, nint.Zero);

    public void SwapBuffers() => GLNative.SwapBuffers(_hdc);

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("LINGFAN_WGL_DIAG") == "1")
            _logger?.LogWarning("[WGL-DIAG] WglContext.Dispose：HGLRC=0x{Rc:X} HWND=0x{Hwnd:X} owns={Owns} 线程={Tid}\n{Stack}",
                _hglrc, _hwnd, _ownsWindow, Environment.CurrentManagedThreadId, Environment.StackTrace);
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
        // 自建隐藏窗口在归还 DC 后销毁；借用的宿主窗口不动。
        // 销毁必须回到创建窗口的专用线程（DestroyWindow 跨线程调用静默失败）。
        if (_ownsWindow && _hwnd != nint.Zero)
        {
            nint toDestroy = _hwnd;
            RunOnWindowThread(() => { GLNative.DestroyWindow(toDestroy); return nint.Zero; });
        }
        _hwnd = nint.Zero;
    }

    // 工厂级离屏 GL 上下文（隐藏消息窗口）
    // 类过程委托须保活，否则被 GC 后 lpfnWndProc 悬空 ⇒ 野调用崩溃（同 OpenGLHeadfulPlaybackProbe）。
    private static WndProcDelegate? _offscreenWndProcKeepAlive;

    // 专用常驻 STA 线程：离屏窗口的创建/销毁都必须在同一原生线程上执行——
    // DestroyWindow 跨线程调用会静默失败（窗口泄漏），而线程池线程原生身份会随任务漂移，
    // 导致「创建线程」早已不存在、窗口永远无法被正确销毁。常驻线程原生身份稳定，杜绝该问题。
    private static readonly object _windowThreadGate = new();
    private static Thread? _windowThread;
    private static System.Collections.Concurrent.BlockingCollection<(Func<nint> Work, TaskCompletionSource<nint> Tcs)>? _windowWorkQueue;

    /// <summary>在专用窗口线程上执行窗口创建（返回 HWND）。</summary>
    private static nint RunOnWindowThread(Func<nint> work)
    {
        lock (_windowThreadGate)
        {
            if (_windowThread is null)
            {
                _windowWorkQueue = new System.Collections.Concurrent.BlockingCollection<(Func<nint>, TaskCompletionSource<nint>)>();
                var queue = _windowWorkQueue;
                _windowThread = new Thread(() =>
                {
                    foreach (var (w, tcs) in queue.GetConsumingEnumerable())
                    {
                        try { tcs.SetResult(w()); }
                        catch (Exception ex) { tcs.SetException(ex); }
                    }
                }) { IsBackground = true, Name = "LingFanGLWindow" };
                // STA 仅 Windows 需要（WGL 消息窗口）；本方法只在 Windows 路径被调用。
                if (OperatingSystem.IsWindows())
                    _windowThread.SetApartmentState(ApartmentState.STA);
                _windowThread.Start();
            }
        }
        var tcs = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _windowWorkQueue!.Add((work, tcs));
        return tcs.Task.GetAwaiter().GetResult();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private const string OffscreenClassName = "LingFanGLDeviceCtx";

    // HWND_MESSAGE(-3)：纯消息窗口——不参与广播消息/DWM/系统窗口管理，杜绝隐藏顶层窗口
    // 被系统机制（挂起检测/幽灵化/会话广播）意外销毁导致 DC 失效（wglMakeCurrent err=0 根因）。
    private static readonly nint HwndMessage = new(-3);

    /// <summary>
    /// 创建工厂级离屏 GL 上下文（隐藏 1×1 窗口 + WGL 3.3 core），作为共享组所有者。
    /// 供 <see cref="OpenGLOffscreenDeviceContext"/> 在解码器初始化前建立，使解码侧产出的 GL 纹理
    /// 经共享组对渲染器 on-screen 上下文可见（零拷贝路径的共享组基础）。
    /// </summary>
    public static WglContext CreateOffscreen(ILogger? logger = null)
    {
        nint hwnd = CreateHiddenWindow();
        try
        {
            return new WglContext(hwnd, logger, shareContext: default, ownsWindow: true);
        }
        catch
        {
            // 构造失败时窗口尚未移交所有权，由此处销毁，避免隐藏窗口泄漏（回到创建线程执行）。
            nint orphan = hwnd;
            RunOnWindowThread(() => { GLNative.DestroyWindow(orphan); return nint.Zero; });
            throw;
        }
    }

    private static nint CreateHiddenWindow()
    {
        // 窗口创建/销毁固定在专用常驻 STA 线程（线程池原生线程身份会漂移，DestroyWindow 跨线程静默失败）。
        return RunOnWindowThread(CreateHiddenWindowCore);
    }

    private static nint CreateHiddenWindowCore()
    {
        _offscreenWndProcKeepAlive = StaticWndProc; // 保活，防止类过程被 GC
        nint namePtr = nint.Zero;
        try
        {
            namePtr = Marshal.StringToHGlobalUni(OffscreenClassName);
            // CS_OWNDC(0x0020)：WGL 上下文要求绑定窗口私有 DC——默认类样式的共享 DC 会被系统
            // 按 LRU 回收复用，导致 wglMakeCurrent 对自己刚用过的 DC 间歇性失败(err=0)。
            const uint CsOwndc = 0x0020;
            // 诊断（LINGFAN_WGL_DIAG=1）：记录窗口创建/销毁现场（线程+托管栈），定位「谁销毁了离屏窗口」。
            bool diag = Environment.GetEnvironmentVariable("LINGFAN_WGL_DIAG") == "1";
            if (diag)
                Console.Error.WriteLine($"[WGL-DIAG] CreateHiddenWindow：线程={Environment.CurrentManagedThreadId}\n{Environment.StackTrace}");
            var wc = new GLNative.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<GLNative.WNDCLASSEXW>(),
                style = CsOwndc,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_offscreenWndProcKeepAlive),
                hbrBackground = GLNative.GetStockObject(4), // BLACK_BRUSH
                lpszClassName = namePtr,
                lpszMenuName = nint.Zero,
            };
            ushort atom = GLNative.RegisterClassExW(ref wc);
            if (atom == 0)
            {
                // 窗口类 LingFanGLDeviceCtx 为进程级全局名。本进程另一离屏 GL 上下文（共享组所有者）已注册过同名类时，
                // RegisterClassExW 返回 0 且 GetLastError=ERROR_CLASS_ALREADY_EXISTS(1410)，属可复用既有类的良性情形，非失败。
                // 若不特判，多实例（如探针同时持有装饰器工厂与 DI 单例工厂）会在此误抛，阻断零拷贝导入。
                const int ERROR_CLASS_ALREADY_EXISTS = 1410;
                if (Marshal.GetLastPInvokeError() != ERROR_CLASS_ALREADY_EXISTS)
                    throw new InvalidOperationException($"WGL 离屏窗口类注册失败(err={Marshal.GetLastPInvokeError()})。");
            }
            nint hwnd = GLNative.CreateWindowExW(0, OffscreenClassName, "", 0, 0, 0, 1, 1,
                HwndMessage, nint.Zero, nint.Zero, nint.Zero);
            if (hwnd == nint.Zero)
                throw new InvalidOperationException($"WGL 离屏窗口创建失败(err={Marshal.GetLastPInvokeError()})。");
            // 诊断（LINGFAN_WGL_DIAG=1）：后台看门狗探活窗口——死亡即倒出消息流水与时刻（定位外部销毁）。
            if (Environment.GetEnvironmentVariable("LINGFAN_WGL_DIAG") == "1")
            {
                uint creatorTid = GLNative.GetCurrentThreadId();
                Console.Error.WriteLine($"[WGL-DIAG] CreateHiddenWindow：HWND=0x{hwnd:X} 托管线程={Environment.CurrentManagedThreadId} 原生线程={creatorTid}");
                nint watched = hwnd;
                var t = new Thread(() =>
                {
                    while (GLNative.IsWindow(watched) != 0) Thread.Sleep(100);
                    Console.Error.WriteLine($"[WGL-DIAG] 看门狗：离屏窗口 0x{watched:X} 已死亡 tick={Environment.TickCount64} 创建原生线程={creatorTid}");
                    DumpMsgLog();
                }) { IsBackground = true };
                t.Start();
            }
            return hwnd;
        }
        finally
        {
            if (namePtr != nint.Zero) Marshal.FreeHGlobal(namePtr);
        }
    }

    private static nint StaticWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // 诊断（LINGFAN_WGL_DIAG=1）：抓销毁类消息——定位「谁在播放中销毁了离屏窗口」。
        // WM_DESTROY=0x0002 WM_CLOSE=0x0010 WM_QUERYENDSESSION=0x0011 WM_ENDSESSION=0x0016 WM_NCDESTROY=0x0082
        if ((msg == 0x0002 || msg == 0x0010 || msg == 0x0011 || msg == 0x0016 || msg == 0x0082) &&
            Environment.GetEnvironmentVariable("LINGFAN_WGL_DIAG") == "1")
            Console.Error.WriteLine($"[WGL-DIAG] 离屏窗口收到 msg=0x{msg:X} HWND=0x{hWnd:X} 线程={Environment.CurrentManagedThreadId}\n{Environment.StackTrace}");
        // 诊断：全消息流水（环形缓冲，看门狗在窗口死亡时倒出——定位窗口死前收到的最后消息）。
        if (Environment.GetEnvironmentVariable("LINGFAN_WGL_DIAG") == "1")
        {
            int idx = System.Threading.Interlocked.Increment(ref _msgLogIdx) - 1;
            _msgLog[idx % _msgLog.Length] =
                $"{Environment.TickCount64} msg=0x{msg:X} w=0x{wParam:X} l=0x{lParam:X} tid={Environment.CurrentManagedThreadId}";
        }
        return GLNative.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // 诊断消息环形缓冲（仅 LINGFAN_WGL_DIAG=1 时写入）。
    private static readonly string?[] _msgLog = new string?[256];
    private static int _msgLogIdx;

    /// <summary>倒出离屏窗口最近收到的消息（诊断绑定失败时窗口是否已被外部销毁）。</summary>
    internal static void DumpMsgLog()
    {
        int end = System.Threading.Volatile.Read(ref _msgLogIdx);
        for (int i = Math.Max(0, end - _msgLog.Length); i < end; i++)
            Console.Error.WriteLine($"[WGL-DIAG-MSG] {_msgLog[i % _msgLog.Length]}");
    }
}

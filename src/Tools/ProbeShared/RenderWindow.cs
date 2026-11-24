using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ProbeShared;

/// <summary>
/// 有头探针真实窗口（专用 STA 线程 + 消息泵 + 黑底自注册窗口类）。
/// </summary>
/// <remarks>
/// <para>D3D11 SwapChain 依赖 STA 线程 + 消息泵；窗口创建/销毁钉在同一线程
/// （DestroyWindow 跨线程静默失败）。窗口类黑底消除 MF 预热/解码期间的启动白屏。</para>
/// <para>窗口类名由调用方传入（各探针独立类名，避免同进程多探针类注册冲突）；
/// classStyle 传 CS_OWNDC(0x0020) 供 WGL 探针使用（私有 DC，共享 DC 被系统回收会让 wglMakeCurrent 间歇失败）。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RenderWindow : IDisposable
{
    /// <summary>窗口句柄（创建失败为 IntPtr.Zero）。</summary>
    public IntPtr Hwnd { get; private set; } = IntPtr.Zero;

    /// <summary>窗口真实客户区宽（GetClientRect 实测，不是构造入参）。SwapChain 必须按它建，否则 DXGI 会再拉伸一层。</summary>
    public int ClientW { get; private set; }

    /// <summary>窗口真实客户区高（GetClientRect 实测）。</summary>
    public int ClientH { get; private set; }

    /// <summary>窗口所在监视器有效 DPI（96=100%）。≠96 且进程非 DPI-aware ⇒ DWM 会做位图拉伸。</summary>
    public uint Dpi { get; private set; }

    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private volatile bool _alive = true;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private readonly bool _visible;
    private readonly int _w, _h;
    private readonly string _className;
    private readonly uint _classStyle;

    private static readonly object _classLock = new();
    private static readonly HashSet<string> _registeredClasses = new();
    // 必须保持根引用：类过程委托一旦被 GC，RegisterClassExW 注册的 lpfnWndProc 即悬空 ⇒ 野调用崩溃。
    private static WndProcDelegate? _wndProcKeepAlive;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>创建窗口并启动消息泵线程（阻塞至 HWND 就绪，5s 超时抛异常）。</summary>
    /// <param name="w">请求宽（实际以客户区实测为准）。</param>
    /// <param name="h">请求高。</param>
    /// <param name="visible">是否可见。</param>
    /// <param name="className">窗口类名（各探针独立，进程内幂等注册）。</param>
    /// <param name="classStyle">窗口类样式（WGL 探针传 CS_OWNDC=0x0020，其余 0）。</param>
    public RenderWindow(int w, int h, bool visible, string className = "LingFanProbeWnd", uint classStyle = 0)
    {
        _w = w; _h = h; _visible = visible; _className = className; _classStyle = classStyle;
        _thread = new Thread(Run) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("窗口线程未就绪（HWND 创建超时）。");
    }

    private void Run()
    {
        RegisterWindowClass(_className, _classStyle);
        Hwnd = NativeMethods.CreateWindowExW(0, _className, "", _visible ? (WS_POPUP | WS_VISIBLE) : WS_POPUP,
            0, 0, _w, _h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
        {
            _ready.Set();
            return;
        }
        if (_visible) NativeMethods.ShowWindow(Hwnd, 1);
        // 实测客户区与 DPI：Attach 必须用实测客户区，用构造入参会让 SwapChain 与窗口错位，DXGI 会再拉伸一层。
        if (NativeMethods.GetClientRect(Hwnd, out var rc))
        {
            ClientW = rc.Right - rc.Left;
            ClientH = rc.Bottom - rc.Top;
        }
        try { Dpi = NativeMethods.GetDpiForWindow(Hwnd); } catch { Dpi = 0; }
        _ready.Set();
        while (_alive)
        {
            while (NativeMethods.PeekMessageW(out var msg, Hwnd, 0, 0, 1))
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
            Thread.Sleep(1);
        }
        NativeMethods.DestroyWindow(Hwnd);
        Hwnd = IntPtr.Zero;
    }

    /// <summary>停止消息泵并销毁窗口（同线程 Join 500ms）。</summary>
    public void Dispose()
    {
        _alive = false;
        try { _thread.Join(500); } catch { }
        _ready.Dispose();
    }

    /// <summary>进程级按类名注册一次黑底窗口类（替代 "Static" 系统类的白色背景，消除启动白屏）。幂等。</summary>
    private static void RegisterWindowClass(string className, uint classStyle)
    {
        lock (_classLock)
        {
            if (!_registeredClasses.Add(className)) return;
            IntPtr namePtr = IntPtr.Zero;
            try
            {
                namePtr = Marshal.StringToHGlobalUni(className);
                _wndProcKeepAlive ??= StaticWndProc; // 保活，防止类过程被 GC
                var wc = new NativeMethods.WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                    style = classStyle,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                    hInstance = IntPtr.Zero,
                    hbrBackground = NativeMethods.GetStockObject(4), // BLACK_BRUSH=4
                    lpszMenuName = IntPtr.Zero,
                    lpszClassName = namePtr,
                    hCursor = NativeMethods.LoadCursorW(IntPtr.Zero, (IntPtr)32512), // IDC_ARROW
                };
                ushort atom = NativeMethods.RegisterClassExW(ref wc);
                if (atom == 0)
                    Console.WriteLine($"[HEADFUL-WND] 窗口类注册失败(err={Marshal.GetLastPInvokeError()})，回退默认类");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HEADFUL-WND] 窗口类注册异常（忽略）: {ex.Message}");
            }
            finally
            {
                if (namePtr != IntPtr.Zero) Marshal.FreeHGlobal(namePtr);
            }
        }
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
}
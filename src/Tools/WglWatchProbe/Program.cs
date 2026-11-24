using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Runtime.Versioning;
using LingFan.Media.Renderers.OpenGL;

[assembly: SupportedOSPlatform("windows")]

namespace WglWatchProbe;

/// <summary>
/// WGL 离屏窗口存活性最小复现探针：只建隐藏窗口（复用渲染器同款类样式/消息窗父级），
/// 不建 GL 上下文、不播放媒体，观察窗口是否被系统机制自发销毁。
/// 用法：dotnet run --project src/Tools/WglWatchProbe -c Release -- [秒数=40] [消息泵=0|1]
/// </summary>
internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint h, uint m, nint w, nint l);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(int ex, string cls, string name, int style,
        int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow")] private static partial int IsWindow(nint h);
    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")] private static partial uint GetCurrentThreadId();
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] private static partial nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")] private static partial int DestroyWindow(nint h);
    [LibraryImport("gdi32.dll", EntryPoint = "GetStockObject")] private static partial nint GetStockObject(int i);
    [LibraryImport("user32.dll", EntryPoint = "WindowFromDC")] private static partial nint WindowFromDC(nint dc);
    [LibraryImport("user32.dll", EntryPoint = "GetDC")] private static partial nint GetDC(nint h);
    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")] private static partial int ReleaseDC(nint h, nint dc);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")] private static partial int PeekMessageW(out MSG m, nint h, uint a, uint b, uint r);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] private static partial int DispatchMessageW(ref MSG m);

    private static WndProc? _keepAlive;
    private static nint _hwnd;

    private static nint Proc(nint h, uint m, nint w, nint l)
    {
        if (m is 0x0002 or 0x0010 or 0x0011 or 0x0016 or 0x0082)
            Console.WriteLine($"[WATCH] msg=0x{m:X} tick={Environment.TickCount64} tid={Environment.CurrentManagedThreadId}\n{Environment.StackTrace}");
        // 消息窗不自动泵消息：诊断模式下全量记录（定位死前最后消息）。
        if (Environment.GetEnvironmentVariable("LINGFAN_WGL_DIAG") == "1")
            Console.WriteLine($"[WATCH-MSG] {Environment.TickCount64} msg=0x{m:X} w=0x{w:X} l=0x{l:X} tid={Environment.CurrentManagedThreadId}");
        return DefWindowProcW(h, m, w, l);
    }

    private static int Main(string[] args)
    {
        // 模式 real：直接经渲染器 CreateOffscreen 建真实离屏 GL 上下文，观察其窗口存活性
        //（区分「窗口机制自发销毁」vs「播放链内某处销毁」）。
        if (args.Length > 0 && args[0] == "real")
            return RunReal(args);
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 40;
        bool pump = args.Length > 1 && args[1] == "1";
        bool messageParent = args.Length > 2 && args[2] == "msg";

        _keepAlive = Proc;
        nint namePtr = Marshal.StringToHGlobalUni("LingFanGLDeviceCtxWatch");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0x0020, // CS_OWNDC
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_keepAlive),
            hbrBackground = GetStockObject(4),
            lpszClassName = namePtr,
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            Console.WriteLine($"注册失败 err={Marshal.GetLastWin32Error()}");
            return 1;
        }
        _hwnd = CreateWindowExW(0, "LingFanGLDeviceCtxWatch", "", 0, 0, 0, 1, 1,
            messageParent ? new nint(-3) : nint.Zero, nint.Zero, nint.Zero, nint.Zero);
        Marshal.FreeHGlobal(namePtr);
        Console.WriteLine($"[WATCH] HWND=0x{_hwnd:X} pump={pump} parent={(messageParent ? "HWND_MESSAGE" : "top")} tid={Environment.CurrentManagedThreadId}");
        if (_hwnd == nint.Zero) return 1;

        nint dc = GetDC(_hwnd);
        Console.WriteLine($"[WATCH] DC=0x{dc:X} owner=0x{WindowFromDC(dc):X}");

        var watcher = new Thread(() =>
        {
            while (IsWindow(_hwnd) != 0) Thread.Sleep(100);
            Console.WriteLine($"[WATCH] 窗口死亡 tick={Environment.TickCount64}");
        }) { IsBackground = true };
        watcher.Start();

        long end = Environment.TickCount64 + seconds * 1000L;
        while (Environment.TickCount64 < end && IsWindow(_hwnd) != 0)
        {
            if (pump)
            {
                while (PeekMessageW(out var msg, _hwnd, 0, 0, 1) != 0)
                    DispatchMessageW(ref msg);
            }
            Thread.Sleep(10);
        }
        bool alive = IsWindow(_hwnd) != 0;
        Console.WriteLine($"[WATCH] 结束 alive={alive} ownerNow=0x{WindowFromDC(dc):X}");
        ReleaseDC(_hwnd, dc);
        if (alive) DestroyWindow(_hwnd);
        return alive ? 0 : 2;
    }

    /// <summary>真实离屏 GL 上下文（渲染器 CreateOffscreen）+ 周期性 MakeCurrent/ReleaseCurrent 循环。</summary>
    private static int RunReal(string[] args)
    {
        int seconds = args.Length > 1 ? int.Parse(args[1]) : 40;
        if (args.Length > 2 && args[2] == "dual")
            return RunDual(seconds, args.Length > 3 && args[3] == "pump");
        var ctx = new OpenGLOffscreenDeviceContext();
        ctx.EnsureCreated();
        Console.WriteLine($"[WATCH-REAL] 离屏上下文已建立 share=0x{ctx.ShareHandle:X}");
        // 可选：再建一个 on-screen 上下文（借用离屏窗口 HWND 之外的新顶层窗口），复现双上下文共享组场景。
        // 参数 real <秒> dual：dual 时另建宿主窗口 + WglContext(share)，并交替 MakeCurrent。
        bool dual = args.Length > 2 && args[2] == "dual";
        nint hostHwnd = nint.Zero;
        object? onscreen = null;
        if (dual)
        {
            _keepAlive = Proc;
            nint np = Marshal.StringToHGlobalUni("LingFanGLDeviceCtxWatch");
            var wc2 = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = 0x0020,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_keepAlive),
                hbrBackground = GetStockObject(4),
                lpszClassName = np,
            };
            RegisterClassExW(ref wc2);
            hostHwnd = CreateWindowExW(0, "LingFanGLDeviceCtxWatch", "", 0, 0, 0, 640, 480,
                nint.Zero, nint.Zero, nint.Zero, nint.Zero);
            Marshal.FreeHGlobal(np);
            Console.WriteLine($"[WATCH-REAL] 宿主窗口 HWND=0x{hostHwnd:X}");
            // 经反射-free 路径：OpenGLOffscreenDeviceContext 不暴露 on-screen 创建；改用渲染器 Attach。
            var renderer = new OpenGLRenderer(null, ctx);
            renderer.Attach(new HwndTarget(hostHwnd, 640, 480));
            onscreen = renderer;
            Console.WriteLine("[WATCH-REAL] on-screen 渲染器已 Attach（共享组接入）");
        }
        long end = Environment.TickCount64 + seconds * 1000L;
        int binds = 0, fails = 0;
        var rnd = new Random(1234);
        while (Environment.TickCount64 < end)
        {
            try
            {
                lock (ctx.GlAccessLock)
                {
                    ctx.MakeCurrent();
                    ctx.ReleaseCurrent();
                }
                binds++;
                if (onscreen is OpenGLRenderer r && rnd.Next(2) == 0)
                {
                    // 经渲染器 Clear 走 on-screen 绑定+SwapBuffers（与播放链同路径）。
                    r.Clear();
                }
            }
            catch (Exception ex)
            {
                fails++;
                if (fails <= 3) Console.WriteLine($"[WATCH-REAL] 绑定失败 #{fails}: {ex.Message}");
            }
            Thread.Sleep(16);
        }
        Console.WriteLine($"[WATCH-REAL] 结束 binds={binds} fails={fails}");
        (onscreen as IDisposable)?.Dispose();
        if (hostHwnd != nint.Zero) DestroyWindow(hostHwnd);
        ctx.Dispose();
        return fails == 0 ? 0 : 2;
    }

    /// <summary>双上下文 + 可选消息泵线程（复现探针 STA 泵线程环境）。</summary>
    private static int RunDual(int seconds, bool pump)
    {
        // 先建宿主窗口（可选专用 STA 泵线程，与 OpenGLHeadfulPlaybackProbe 同构）。
        _keepAlive = Proc;
        nint np = Marshal.StringToHGlobalUni("LingFanGLDeviceCtxWatch");
        var wc2 = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0x0020,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_keepAlive),
            hbrBackground = GetStockObject(4),
            lpszClassName = np,
        };
        RegisterClassExW(ref wc2);
        Marshal.FreeHGlobal(np);
        nint hostHwnd = nint.Zero;
        var ready = new ManualResetEventSlim();
        bool alive = true;
        Thread? pumpThread = null;
        if (pump)
        {
            pumpThread = new Thread(() =>
            {
                hostHwnd = CreateWindowExW(0, "LingFanGLDeviceCtxWatch", "", 0, 0, 0, 640, 480,
                    nint.Zero, nint.Zero, nint.Zero, nint.Zero);
                ready.Set();
                while (alive)
                {
                    while (PeekMessageW(out var msg, hostHwnd, 0, 0, 1) != 0)
                        DispatchMessageW(ref msg);
                    Thread.Sleep(1);
                }
                // 窗口归创建线程销毁（DestroyWindow 跨线程调用静默失败）。
                DestroyWindow(hostHwnd);
            }) { IsBackground = true };
            pumpThread.SetApartmentState(ApartmentState.STA);
            pumpThread.Start();
            ready.Wait(5000);
        }
        else
        {
            hostHwnd = CreateWindowExW(0, "LingFanGLDeviceCtxWatch", "", 0, 0, 0, 640, 480,
                nint.Zero, nint.Zero, nint.Zero, nint.Zero);
        }
        Console.WriteLine($"[WATCH-DUAL] 宿主 HWND=0x{hostHwnd:X} pump={pump}");

        var ctx = new OpenGLOffscreenDeviceContext();
        ctx.EnsureCreated();
        var renderer = new OpenGLRenderer(null, ctx);
        renderer.Attach(new HwndTarget(hostHwnd, 640, 480));
        Console.WriteLine("[WATCH-DUAL] 双上下文就绪");

        long end = Environment.TickCount64 + seconds * 1000L;
        int binds = 0, fails = 0;
        var rnd = new Random(1234);
        // 双线程并发：主线程 Clear（on-screen），后台线程 MakeCurrent（离屏）——模拟呈现/解码线程。
        var importThread = new Thread(() =>
        {
            while (Environment.TickCount64 < end)
            {
                try
                {
                    lock (ctx.GlAccessLock) { ctx.MakeCurrent(); ctx.ReleaseCurrent(); }
                    Interlocked.Increment(ref binds);
                }
                catch (Exception ex)
                {
                    int f = Interlocked.Increment(ref fails);
                    if (f <= 3) Console.WriteLine($"[WATCH-DUAL] 导入绑定失败 #{f}: {ex.Message}");
                }
                Thread.Sleep(8);
            }
        }) { IsBackground = true };
        importThread.Start();
        while (Environment.TickCount64 < end)
        {
            try { renderer.Clear(); }
            catch (Exception ex)
            {
                int f = Interlocked.Increment(ref fails);
                if (f <= 3) Console.WriteLine($"[WATCH-DUAL] Clear 失败 #{f}: {ex.Message}");
            }
            Thread.Sleep(16);
        }
        importThread.Join(2000);
        Console.WriteLine($"[WATCH-DUAL] 结束 binds={binds} fails={fails}");
        renderer.Dispose();
        // 先停泵再销窗：DestroyWindow 必须由创建窗口的线程调用（跨线程调用静默失败，窗口泄漏）。
        alive = false;
        pumpThread?.Join(1000);
        if (!pump && hostHwnd != nint.Zero) DestroyWindow(hostHwnd);
        ctx.Dispose();
        return fails == 0 ? 0 : 2;
    }

    /// <summary>最小 HWND 渲染目标（仅供本探针 Attach 用）。</summary>
    private sealed class HwndTarget : LingFan.Media.Abstractions.IRenderTarget
    {
        private readonly nint _h;
        public HwndTarget(nint h, int w, int hgt) { _h = h; Width = w; Height = hgt; }
        public LingFan.Media.Abstractions.RenderTargetType Type => LingFan.Media.Abstractions.RenderTargetType.Window;
        public LingFan.Media.Abstractions.RenderHandleType HandleType => LingFan.Media.Abstractions.RenderHandleType.Pointer;
        public object NativeHandle => _h;
        public int Width { get; }
        public int Height { get; }
        public float Scale => 1f;
    }
}
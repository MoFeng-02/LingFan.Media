using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Backends.VLCNative;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.OpenGL;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenGLHeadfulPlaybackProbe;

// Program 的窗口/渲染装饰段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    // 计数装饰器：包裹真实 IVideoRenderer，统计 Present 调用

    private sealed class CountingVideoRendererFactory : IVideoRendererFactory
    {
        private readonly IVideoRendererFactory _inner;
        private readonly int _saveFrames;
        private readonly string _saveDir;
        public CountingVideoRenderer? Last { get; private set; }
        public CountingVideoRendererFactory(IVideoRendererFactory inner, int saveFrames, string saveDir)
        {
            _inner = inner;
            _saveFrames = saveFrames;
            _saveDir = saveDir;
        }
        public IVideoRenderer Create()
        {
            var wrapped = new CountingVideoRenderer(_inner.Create(), _saveFrames, _saveDir);
            Last = wrapped;
            return wrapped;
        }
    }

    private sealed class CountingVideoRenderer : IVideoRenderer
    {
        private readonly IVideoRenderer _inner;
        private readonly int _saveFrames;
        private readonly string _saveDir;
        public int PresentCount;
        public CountingVideoRenderer(IVideoRenderer inner, int saveFrames, string saveDir)
        {
            _inner = inner;
            _saveFrames = saveFrames;
            _saveDir = saveDir;
        }
        public void Attach(IRenderTarget target) => _inner.Attach(target);
        public void Detach() => _inner.Detach();
        public void Present(VideoFrame frame)
        {
            int n = Interlocked.Increment(ref PresentCount);
            // 诊断：把渲染器收到的真实帧落 PNG（在 _inner.Present 之前同步拷贝，不持有帧引用）。
            // CPU 软解帧走 NV12/BGRA/RGBA 转换；OpenGL 不消费 GPU 纹理帧，无需 Readback 分支。
            if (_saveFrames > 0 && n % _saveFrames == 0)
                FrameDumper.DumpFrame(frame, n, _saveDir);
            _inner.Present(frame);
        }
        public TimeSpan PresentationLatency => _inner.PresentationLatency;

        public void Clear() => _inner.Clear();
        public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);
        public void Dispose() => _inner.Dispose();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    // 渲染目标：把 HWND 包装成 IRenderTarget（Window 类型）

    private sealed class HwndRenderTarget : IRenderTarget
    {
        private readonly IntPtr _hwnd;
        private readonly int _w, _h;
        public HwndRenderTarget(IntPtr hwnd, int w, int h) { _hwnd = hwnd; _w = w; _h = h; }
        public RenderTargetType Type => RenderTargetType.Window;
        public RenderHandleType HandleType => RenderHandleType.Pointer;
        public object NativeHandle => _hwnd;
        public int Width => _w;
        public int Height => _h;
        public float Scale => 1f;
    }

    // 真实窗口（专用 STA 线程 + 消息泵）；[LibraryImport] 重写（AOT 合规）

    private sealed class RenderWindow : IDisposable
    {
        public IntPtr Hwnd { get; private set; } = IntPtr.Zero;
        /// <summary>窗口真实客户区宽（GetClientRect 实测，不是构造入参）。GL 上下文必须按它建，否则 DWM 会再拉伸一层。</summary>
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

        // —— 自注册窗口类（黑底，消除 MF 预热/解码期间的启动白屏）——
        private const string WindowClassName = "LingFanGlProbeWnd";
        private static readonly object _classLock = new();
        private static bool _classRegistered;
        // 必须保持根引用：类过程委托一旦被 GC，RegisterClassExW 注册的 lpfnWndProc 即悬空 ⇒ 野调用崩溃。
        private static WndProcDelegate? _wndProcKeepAlive;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public RenderWindow(int w, int h, bool visible)
        {
            _w = w; _h = h; _visible = visible;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA); // WGL 依赖 STA 线程 + 消息泵
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("窗口线程未就绪（HWND 创建超时）。");
        }

        private void Run()
        {
            RegisterWindowClass();
            Hwnd = NativeMethods.CreateWindowExW(0, WindowClassName, "", _visible ? (WS_POPUP | WS_VISIBLE) : WS_POPUP,
                0, 0, _w, _h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Hwnd == IntPtr.Zero)
            {
                _ready.Set();
                return;
            }
            if (_visible) NativeMethods.ShowWindow(Hwnd, 1);
            // 实测客户区与 DPI：Attach 必须用实测客户区，用构造入参会让 GL 上下文与窗口错位，DWM 会再拉伸一层。
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

        public void Dispose()
        {
            _alive = false;
            try { _thread.Join(500); } catch { }
            _ready.Dispose();
        }

        /// <summary>进程级注册一次黑底窗口类（替代 "Static" 系统类的白色背景，消除启动白屏）。幂等。</summary>
        private static void RegisterWindowClass()
        {
            lock (_classLock)
            {
                if (_classRegistered) return;
                IntPtr namePtr = IntPtr.Zero;
                try
                {
                    namePtr = Marshal.StringToHGlobalUni(WindowClassName);
                    _wndProcKeepAlive = StaticWndProc; // 保活，防止类过程被 GC
                    var wc = new NativeMethods.WNDCLASSEXW
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                        style = 0,
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
                    _classRegistered = true;
                }
            }
        }

        private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            => NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}

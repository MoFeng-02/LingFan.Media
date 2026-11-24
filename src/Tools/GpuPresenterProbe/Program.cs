using System;
using System.Runtime.InteropServices;
using System.Threading;
using LingFan.Media.Abstractions;
using LingFan.Media.Extensions;
using LingFan.Media.Presenters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace GpuPresenterProbe;

/// <summary>
/// GPU Presenter 生命周期探针：验证 Vulkan/OpenGL 原生 Presenter 的
/// Initialize → Present（合成彩条帧）→ Resize → Clear → Dispose 全链路。
/// 不走解码管线——直接构造软件帧喂 Presenter，隔离验证「Presenter + 渲染器 + 窗口」段。
/// </summary>
/// <remarks>
/// 用法：GpuPresenterProbe [--backend vulkan|opengl] [--visible] [--seconds N]
/// 判据：各阶段无异常 + Present 计数达标 + Resize 后画面尺寸生效 + 干净退出（exit 0）。
/// </remarks>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        string backend = "vulkan";
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--backend") backend = args[i + 1].ToLowerInvariant();
        bool visible = Array.Exists(args, a => a == "--visible");
        int seconds = 3;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--seconds" && int.TryParse(args[i + 1], out var s)) seconds = Math.Max(1, s);

        Console.WriteLine($"[PRESENTER-PROBE] backend={backend} visible={visible} seconds={seconds}");

        var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(LogLevel.Information));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        var builder = services.AddLingFanMedia();
        switch (backend)
        {
            case "opengl": builder.AddOpenGLPresenter(); break;
            case "vulkan": builder.AddVulkanPresenter(); break;
            default:
                Console.WriteLine($"[PRESENTER-PROBE] 未知 backend: {backend}（支持 vulkan/opengl）");
                return 2;
        }
        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IGpuPresenterFactory>();
        Console.WriteLine($"[PRESENTER-PROBE] 工厂解析: {factory.PresenterType.Name}");

        using var win = new RenderWindow(640, 480, visible);
        if (win.Hwnd == IntPtr.Zero)
        {
            Console.WriteLine("[PRESENTER-PROBE] FAIL: 窗口创建失败");
            return 1;
        }

        var presenter = factory.Create();
        int presented = 0;
        try
        {
            // 1) Initialize
            presenter.Initialize(new GpuRenderTarget(win.Hwnd, win.ClientW, win.ClientH, 1.0f));
            Console.WriteLine("[PRESENTER-PROBE] Initialize OK");

            // 2) Present 合成彩条帧（每帧换色相，肉眼可辨活动画面）
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            int hue = 0;
            while (DateTime.UtcNow < deadline)
            {
                using var frame = MakeColorBarFrame(win.ClientW, win.ClientH, hue);
                presenter.Present(frame);
                presented++;
                hue = (hue + 3) % 360;
                Thread.Sleep(16);
            }
            Console.WriteLine($"[PRESENTER-PROBE] Present OK: {presented} 帧");

            // 3) Resize（窗口尺寸不变，仅验证 Presenter 重建 SwapChain 路径不崩溃且后续可继续 Present）
            presenter.Resize(win.ClientW, win.ClientH, 1.0f);
            for (int i = 0; i < 10; i++)
            {
                using var frame = MakeColorBarFrame(win.ClientW, win.ClientH, 120);
                presenter.Present(frame);
                presented++;
                Thread.Sleep(16);
            }
            Console.WriteLine("[PRESENTER-PROBE] Resize + 续播 OK");

            // 4) Clear
            presenter.Clear();
            Console.WriteLine("[PRESENTER-PROBE] Clear OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PRESENTER-PROBE] FAIL: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            presenter.Dispose();
            return 1;
        }
        finally
        {
            // 5) Dispose（幂等性顺带验证：二次 Dispose 不得抛）
            presenter.Dispose();
            presenter.Dispose();
        }
        Console.WriteLine("[PRESENTER-PROBE] Dispose OK（含幂等复调用）");

        bool pass = presented >= seconds * 30; // 16ms 节拍下的宽松下限
        Console.WriteLine($"[PRESENTER-PROBE] => {(pass ? "PASS" : "FAIL")} (presented={presented})");
        return pass ? 0 : 1;
    }

    /// <summary>构造 BGRA32 彩条帧（8 竖条，色相随 hue 旋转）。</summary>
    private static VideoFrame MakeColorBarFrame(int w, int h, int hue)
    {
        var res = new SoftwareFrameResource(w, h, PixelFormat.BGRA32, w * h * 4);
        var span = res.Data.Span;
        for (int x = 0; x < w; x++)
        {
            int bar = x * 8 / w;
            HsvToBgra((hue + bar * 45) % 360, 0.9, 0.95, out byte b, out byte g, out byte r);
            for (int y = 0; y < h; y++)
            {
                int o = (y * w + x) * 4;
                span[o] = b; span[o + 1] = g; span[o + 2] = r; span[o + 3] = 255;
            }
        }
        return new VideoFrame(w, h, PixelFormat.BGRA32, res,
            TimeSpan.FromMilliseconds(hue * 16), TimeSpan.FromMilliseconds(16), keyFrame: true);
    }

    private static void HsvToBgra(int h, double s, double v, out byte b, out byte g, out byte r)
    {
        double c = v * s;
        double hp = h / 60.0;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        double m = v - c;
        double rr = 0, gg = 0, bb = 0;
        switch ((int)hp)
        {
            case 0: rr = c; gg = x; break;
            case 1: rr = x; gg = c; break;
            case 2: gg = c; bb = x; break;
            case 3: gg = x; bb = c; break;
            case 4: rr = x; bb = c; break;
            default: rr = c; bb = x; break;
        }
        r = (byte)((rr + m) * 255);
        g = (byte)((gg + m) * 255);
        b = (byte)((bb + m) * 255);
    }

    // —— 真实窗口（专用 STA 线程 + 消息泵，与有头探针同构）——

    private sealed class RenderWindow : IDisposable
    {
        public IntPtr Hwnd { get; private set; }
        public int ClientW { get; private set; }
        public int ClientH { get; private set; }
        private readonly ManualResetEventSlim _ready = new();
        private readonly Thread _thread;
        private volatile bool _alive = true;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_VISIBLE = 0x10000000;
        private readonly bool _visible;
        private readonly int _w, _h;
        private const string WindowClassName = "LingFanPresenterProbeWnd";
        private static readonly object _classLock = new();
        private static bool _classRegistered;
        private static WndProcDelegate? _wndProcKeepAlive;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public RenderWindow(int w, int h, bool visible)
        {
            _w = w; _h = h; _visible = visible;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("窗口线程未就绪。");
        }

        private void Run()
        {
            lock (_classLock)
            {
                if (!_classRegistered)
                {
                    _wndProcKeepAlive = DefWindowProcW;
                    IntPtr namePtr = Marshal.StringToHGlobalUni(WindowClassName);
                    try
                    {
                        var wc = new WNDCLASSEXW
                        {
                            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                            lpszClassName = namePtr,
                            hbrBackground = IntPtr.Zero,
                        };
                        ushort atom = RegisterClassExW(ref wc);
                        if (atom == 0 && Marshal.GetLastPInvokeError() != 1410)
                        { _ready.Set(); return; }
                        _classRegistered = true;
                    }
                    finally { Marshal.FreeHGlobal(namePtr); }
                }
            }
            Hwnd = CreateWindowExW(0, WindowClassName, "GpuPresenterProbe",
                _visible ? (WS_POPUP | WS_VISIBLE) : WS_POPUP,
                0, 0, _w, _h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Hwnd == IntPtr.Zero) { _ready.Set(); return; }
            if (_visible) ShowWindow(Hwnd, 1);
            if (GetClientRect(Hwnd, out var rc)) { ClientW = rc.Right - rc.Left; ClientH = rc.Bottom - rc.Top; }
            _ready.Set();
            while (_alive)
            {
                while (PeekMessageW(out var msg, Hwnd, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                Thread.Sleep(1);
            }
            DestroyWindow(Hwnd);
            Hwnd = IntPtr.Zero;
        }

        public void Dispose()
        {
            _alive = false;
            _thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public IntPtr lpszMenuName; public IntPtr lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public int ptX; public int ptY;
    }

    [LibraryImport("user32", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(int exStyle, string cls, string name, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [LibraryImport("user32")] private static partial int ShowWindow(IntPtr hwnd, int cmd);
    [LibraryImport("user32")] private static partial int DestroyWindow(IntPtr hwnd);
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out RECT rc);
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref MSG msg);
    [LibraryImport("user32")] private static partial IntPtr DispatchMessageW(ref MSG msg);
    [LibraryImport("user32")] private static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
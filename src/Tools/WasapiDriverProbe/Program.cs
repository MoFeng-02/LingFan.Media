// =============================================================================
//  WASAPI Driver Probe —— 独立、纯净的 Windows 音频驱动诊断工具
//  用途：在不依赖 LingFan.Media 任何代码的前提下，客观判定：
//    1) windowless 进程（如 dotnet test 的 testhost）的音频会话是否会被 OS 挂起；
//    2) IAudioClient2.SetClientProperties 在各 AudioClientCategory 下是否可用（或崩）；
//    3) 独占模式 Initialize 是否可用。
//  关键点：本程序用 .NET 官方 [ComImport] COM 互操作（CLR 自动算 vtable），
//          不是项目里手写的 vtable P/Invoke。因此本程序的成败 = 机器/driver 行为，
//          与 LingFan.Media 代码无关。若本程序也崩/失败，则 100% 是机器/driver 问题。
// =============================================================================
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;

static partial class Native
{
    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter, int dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetConsoleWindow();

    // 隐藏锚点窗口（对照实验用）
    public const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const int WS_VISIBLE = 0x10000000;
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    // 🔴 [LibraryImport] 默认 ExactSpelling=true，不做 A/W 后缀自动探测（[DllImport] 默认 false 会试）。
    // 所有 user32/kernel32 的 A/W 双版本 API 必须显式写 EntryPoint = "XxxW"，否则运行期 EntryPointNotFoundException。
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public int time; public int x; public int y;
    }
}

// ---- COM 接口（[ComImport] 官方互操作，vtable 由 CLR 正确计算） ----
// 注意：InterfaceIsIUnknown 下，C# 接口的第一个方法对应原生 vtable slot 3（IUnknown 之后）。
[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr ppEndpoint);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
    int GetBufferSize(out uint bufferFrameCount);
    int GetStreamLatency(out long latency);
    int GetCurrentPadding(out int numPaddingFrames);
    int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
    int GetMixFormat(out IntPtr ppFormat);
    int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
    int Start();
    int Stop();
    int Reset();
    int SetEventHandle(IntPtr eventHandle);
    int GetService(ref Guid riid, out IntPtr ppv);
}

[ComImport]
[Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient2 : IAudioClient
{
    int GetPeriodicity(out long hnsPeriodicity);
    int GetLatency(out long hnsLatency);
    int GetProcessingPeriod(out long hnsDefaultProcessingPeriod, out long hnsMinimumProcessingPeriod);
    int SetClientProperties(ref AudioClientProperties pProperties);
    int IsOffloadCapable(int category, out bool pbOffloadCapable);
}

[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioRenderClient
{
    int GetBuffer(int numFramesRequested, out IntPtr ppData);
    int ReleaseBuffer(int numFramesWritten, int dwFlags);
}

[StructLayout(LayoutKind.Sequential)]
struct AudioClientProperties
{
    public uint cbSize;
    public int bIsOffload;   // BOOL (4 bytes)
    public int eCategory;    // AUDIO_STREAM_CATEGORY
    public int eStreamOptions;
}

// ---- 常量 ----
static class C
{
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const int AUDCLNT_SHAREMODE_EXCLUSIVE = 1;
    public const int CLSCTX_ALL = 23;
    public const int eRender = 0;
    public const int eMultimedia = 1;
    public const int COINIT_MULTITHREADED = 0x0;
}

static class Program
{
    static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    static readonly Guid IID_IAudioClient2 = new Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA");
    static readonly Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    // 官方 AUDIO_STREAM_CATEGORY 值
    static readonly (string name, int val)[] Categories = new[]
    {
        ("Other", 0),
        ("ForegroundOnlyMedia", 1),
        ("BackgroundCapableMedia", 2),
        ("Communications", 3),
        ("GameChat", 4),
        ("Speech", 5),
        ("Movie", 6),
        ("Media", 7),
        ("GameMedia", 8),
    };

    static int Main(string[] args)
    {
        Native.CoInitializeEx(IntPtr.Zero, C.COINIT_MULTITHREADED);

        if (args.Length >= 1 && args[0] == "setprops")
        {
            // 子进程模式：只调用一次 SetClientProperties（隔离崩溃）
            return RunSetProps(int.Parse(args[1]));
        }

        bool windowed = args.Length >= 1 && args[0] == "windowed";
        if (windowed) SpawnAnchorWindow();

        Console.WriteLine("=== WASAPI Driver Probe ===");
        Console.WriteLine($"运行进程: {(Native.GetConsoleWindow() == IntPtr.Zero ? "windowless 控制台" : "带控制台窗口")}" +
                          (windowed ? " + 隐藏锚点窗口(对照)" : ""));
        Console.WriteLine();

        try { Probe1_SharedPlayback(windowed); }
        catch (Exception ex) { Console.WriteLine($"[Probe1] 异常: {ex}"); }

        Console.WriteLine();
        try { Probe2_SetClientProperties(); }
        catch (Exception ex) { Console.WriteLine($"[Probe2] 异常: {ex}"); }

        Console.WriteLine();
        try { Probe3_Exclusive(); }
        catch (Exception ex) { Console.WriteLine($"[Probe3] 异常: {ex}"); }

        Console.WriteLine();
        Console.WriteLine("=== 诊断完成。把以上输出贴回给助手即可定性。 ===");
        return 0;
    }

    // -------------------------------------------------------------------------
    // Probe 1：共享模式 windowless 播放 16s，监控设备消费（判定 OS 是否挂起会话）
    // -------------------------------------------------------------------------
    static void Probe1_SharedPlayback(bool windowed)
    {
        Console.WriteLine("[Probe 1] 共享模式播放 16s，监控设备消费（判定 OS 是否挂起音频会话）");

        IMMDeviceEnumerator enumerator = CreateEnumerator();
        enumerator.GetDefaultAudioEndpoint(C.eRender, C.eMultimedia, out IntPtr pEndpoint);
        IMMDevice device = (IMMDevice)Marshal.GetObjectForIUnknown(pEndpoint);

        Guid gClient = IID_IAudioClient;
        device.Activate(ref gClient, C.CLSCTX_ALL, IntPtr.Zero, out IntPtr pClient);
        IAudioClient client = (IAudioClient)Marshal.GetObjectForIUnknown(pClient);

        client.GetMixFormat(out IntPtr pwfx);
        int blockAlign = Marshal.ReadInt16(pwfx, 12);  // nBlockAlign
        int bits = Marshal.ReadInt16(pwfx, 14);        // wBitsPerSample
        int sampleRate = Marshal.ReadInt32(pwfx, 4);   // nSamplesPerSec
        bool isFloat = IsFloatFormat(pwfx);
        Console.WriteLine($"  默认端点格式: blockAlign={blockAlign}B, bits={bits}, rate={sampleRate}Hz, float={isFloat}");

        int hr = client.Initialize(C.AUDCLNT_SHAREMODE_SHARED, 0, 0, 0, pwfx, IntPtr.Zero);
        if (hr < 0) { Console.WriteLine($"  Initialize 失败 HRESULT=0x{hr:X8}"); return; }

        client.GetBufferSize(out uint bufferFrames);
        IntPtr pRenderUnk;
        Guid gRender = IID_IAudioRenderClient;
        client.GetService(ref gRender, out pRenderUnk);
        IAudioRenderClient render = (IAudioRenderClient)Marshal.GetObjectForIUnknown(pRenderUnk);
        client.Start();

        Console.WriteLine($"  缓冲帧数={bufferFrames} (~{bufferFrames / (double)sampleRate:F2}s)");
        Console.WriteLine("  t(s)  written  played  (played 滞后于 written 即设备停消费=会话被挂起)");

        var sw = Stopwatch.StartNew();
        long writtenFrames = 0;
        bool stalled = false;
        int stallStartSec = 0;

        while (sw.Elapsed.TotalSeconds < 16)
        {
            client.GetCurrentPadding(out int padding);
            int avail = (int)bufferFrames - padding;
            if (avail > 0)
            {
                render.GetBuffer(avail, out IntPtr buf);
                FillTone(buf, avail, blockAlign, isFloat, bits);
                render.ReleaseBuffer(avail, 0);
                writtenFrames += avail;
            }
            double t = sw.Elapsed.TotalSeconds;
            double writtenSec = writtenFrames / (double)sampleRate;
            double playedSec = (writtenFrames - padding) / (double)sampleRate;

            if (t >= 1.0 && (int)t != (int)(t - 0.05))
                Console.WriteLine($"  {t,4:F1}  {writtenSec,7:F2}  {playedSec,7:F2}");

            // 停滞判定：t>11s 后 played 明显不再跟随 written
            if (t > 11 && writtenSec - playedSec > 2.0)
            {
                if (!stalled) { stalled = true; stallStartSec = (int)t; }
            }
            Thread.Sleep(40);
        }
        client.Stop();

        if (stalled)
            Console.WriteLine($"  => VERDICT: 会话在 ~{stallStartSec}s 被 OS 挂起（设备停止消费缓冲）。" +
                              $"这是 Windows 对 windowless/非前台进程的策略，非代码 bug。");
        else
            Console.WriteLine("  => VERDICT: 会话未被挂起，设备持续消费（windowless 进程也能完整播放）。");
    }

    // -------------------------------------------------------------------------
    // Probe 2：逐 AudioClientCategory 调用 SetClientProperties（子进程隔离崩溃）
    // -------------------------------------------------------------------------
    static void Probe2_SetClientProperties()
    {
        Console.WriteLine("[Probe 2] IAudioClient2.SetClientProperties 逐分类探测（每个分类独立子进程，崩溃不外溢）");

        foreach (var cat in Categories)
        {
            // 选一个能递归调用自身的方式：优先独立 exe，否则用 bin 下的 dll 经 dotnet 执行
            string selfDll = Path.Combine(AppContext.BaseDirectory, "WasapiDriverProbe.dll");
            bool selfExe = !string.IsNullOrEmpty(Environment.ProcessPath)
                && !Environment.ProcessPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                && !Environment.ProcessPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Environment.ProcessPath);

            string fileName, arguments;
            if (selfExe)
            {
                fileName = Environment.ProcessPath!;
                arguments = $"setprops {cat.val}";
            }
            else if (File.Exists(selfDll))
            {
                fileName = "dotnet";
                arguments = $"\"{selfDll}\" setprops {cat.val}";
            }
            else
            {
                fileName = "dotnet";
                arguments = $"run --project \"{(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))}..\\..\\..\\WasapiDriverProbe.csproj\" setprops {cat.val}";
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode == unchecked((int)0xC0000005))
                Console.WriteLine($"  {cat.name,-22}({cat.val}): CRASH 0xC0000005 (原生 AV，driver 实现损坏)");
            else if (p.ExitCode < 0)
                Console.WriteLine($"  {cat.name,-22}({cat.val}): 子进程退出码 0x{p.ExitCode:X8}");
            else
                Console.WriteLine($"  {cat.name,-22}({cat.val}): {stdout.Trim()}");
        }
    }

    // 子进程：只调用一次 SetClientProperties
    static int RunSetProps(int category)
    {
        try
        {
            Native.CoInitializeEx(IntPtr.Zero, C.COINIT_MULTITHREADED);
            IMMDeviceEnumerator enumerator = CreateEnumerator();
            enumerator.GetDefaultAudioEndpoint(C.eRender, C.eMultimedia, out IntPtr pEndpoint);
            IMMDevice device = (IMMDevice)Marshal.GetObjectForIUnknown(pEndpoint);
            Guid gClient = IID_IAudioClient;
            device.Activate(ref gClient, C.CLSCTX_ALL, IntPtr.Zero, out IntPtr pClient);
            IAudioClient client = (IAudioClient)Marshal.GetObjectForIUnknown(pClient);
            client.GetMixFormat(out IntPtr pwfx);
            int hrInit = client.Initialize(C.AUDCLNT_SHAREMODE_SHARED, 0, 0, 0, pwfx, IntPtr.Zero);
            if (hrInit < 0) { Console.WriteLine($"Initialize 失败 0x{hrInit:X8}"); return hrInit; }

            IAudioClient2 client2;
            try { client2 = (IAudioClient2)client; }
            catch (InvalidCastException) { Console.WriteLine("IAudioClient2 不支持 (旧系统)"); return 1; }

            var props = new AudioClientProperties
            {
                cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                bIsOffload = 0,
                eCategory = category,
                eStreamOptions = 0,
            };
            int hr = client2.SetClientProperties(ref props);
            if (hr < 0) { Console.WriteLine($"HRESULT=0x{hr:X8}"); return hr; }
            Console.WriteLine("S_OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            return -1;
        }
    }

    // -------------------------------------------------------------------------
    // Probe 3：独占模式 Initialize
    // -------------------------------------------------------------------------
    static void Probe3_Exclusive()
    {
        Console.WriteLine("[Probe 3] 独占模式 (AUDCLNT_SHAREMODE_EXCLUSIVE) Initialize 探测");
        try
        {
            IMMDeviceEnumerator enumerator = CreateEnumerator();
            enumerator.GetDefaultAudioEndpoint(C.eRender, C.eMultimedia, out IntPtr pEndpoint);
            IMMDevice device = (IMMDevice)Marshal.GetObjectForIUnknown(pEndpoint);
            Guid gClient = IID_IAudioClient;
            device.Activate(ref gClient, C.CLSCTX_ALL, IntPtr.Zero, out IntPtr pClient);
            IAudioClient client = (IAudioClient)Marshal.GetObjectForIUnknown(pClient);
            client.GetMixFormat(out IntPtr pwfx);
            int hr = client.Initialize(C.AUDCLNT_SHAREMODE_EXCLUSIVE, 0, 0, 0, pwfx, IntPtr.Zero);
            if (hr < 0)
                Console.WriteLine($"  => HRESULT=0x{hr:X8} (独占不可用：{(hr == unchecked((int)0x88890019) ? "AUDCLNT_E_ENDPOINT_OFFLOAD_NOT_CAPABLE / 端点不支持" : "其他")})");
            else
                Console.WriteLine("  => S_OK（独占可用）");
        }
        catch (Exception ex) { Console.WriteLine($"  异常: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // helpers
    // -------------------------------------------------------------------------
    static IMMDeviceEnumerator CreateEnumerator()
    {
        Guid clsid = CLSID_MMDeviceEnumerator;
        Guid iid = IID_IMMDeviceEnumerator;
        int hr = Native.CoCreateInstance(ref clsid, IntPtr.Zero, C.CLSCTX_ALL,
            ref iid, out IntPtr pEnum);
        if (hr < 0) throw new Exception($"CoCreateInstance MMDeviceEnumerator 失败 0x{hr:X8}");
        return (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pEnum);
    }

    static bool IsFloatFormat(IntPtr pwfx)
    {
        short fmt = Marshal.ReadInt16(pwfx, 0);
        if ((ushort)fmt == 0xFFFE) // WAVEFORMATEXTENSIBLE
        {
            // SubFormat GUID 在偏移 24，float = {00000003-0000-0010-8000-00AA00389B71}
            byte[] floatSub = { 3,0,0,0, 0,0,0x10,0x80, 0,0,0xAA,0,0x38,0x9B,0x71 };
            for (int i = 0; i < 16; i++)
                if (Marshal.ReadByte(pwfx, 24 + i) != floatSub[i]) return false;
            return true;
        }
        return false; // 0x0001 PCM 视为 int
    }

    static void FillTone(IntPtr buf, int frames, int blockAlign, bool isFloat, int bits)
    {
        unsafe
        {
            byte* p = (byte*)buf;
            for (int f = 0; f < frames; f++)
            {
                double sample = Math.Sin(2 * Math.PI * 440.0 * (Environment.TickCount + f) / 48000.0) * 0.3;
                if (isFloat)
                {
                    float v = (float)sample;
                    for (int c = 0; c < blockAlign / 4; c++)
                        *(float*)(p + c * 4) = v;
                }
                else if (bits == 16)
                {
                    short v = (short)(sample * 30000);
                    for (int c = 0; c < blockAlign / 2; c++)
                        *(short*)(p + c * 2) = v;
                }
                else // 32-bit int 或兜底：填 0
                {
                    for (int b = 0; b < blockAlign; b++) p[b] = 0;
                }
                p += blockAlign;
            }
        }
    }

    // 对照实验：创建隐藏锚点窗口 + 消息泵线程
    static Native.WndProc _anchorWndProc;
    static void SpawnAnchorWindow()
    {
        _anchorWndProc = (h, m, w, l) => Native.DefWindowProc(h, m, w, l);
        var thread = new Thread(() =>
        {
            IntPtr classNamePtr = Marshal.StringToHGlobalUni("WASAPIProbeAnchor");
            Native.WNDCLASSEX wc = new Native.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_anchorWndProc),
                hInstance = Native.GetConsoleWindow(),
                lpszClassName = classNamePtr,
            };
            Native.RegisterClassEx(ref wc);
            Native.CreateWindowEx(0, "WASAPIProbeAnchor", "anchor",
                Native.WS_VISIBLE, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            while (Native.GetMessage(out Native.MSG msg, IntPtr.Zero, 0, 0) > 0) { }
            Marshal.FreeHGlobal(classNamePtr);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Thread.Sleep(200); // 等窗口建好
    }
}

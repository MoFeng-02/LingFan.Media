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

    // [LibraryImport] 默认 ExactSpelling=true，不做 A/W 后缀自动探测（[DllImport] 默认 false 会试）。
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

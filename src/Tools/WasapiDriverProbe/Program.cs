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

static partial class Program
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

}

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;

// Program 的探针实验段（部分类，由 Program.cs 按职责拆分，编译期等价）。
static partial class Program
{
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
        // Windows 进程访问违规退出码（STATUS_ACCESS_VIOLATION）；裸 hex 仅在具名常量出现一次。
        const int StatusAccessViolation = unchecked((int)0xC0000005);

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

            if (p.ExitCode == StatusAccessViolation)
                Console.WriteLine($"  {cat.name,-22}({cat.val}): CRASH STATUS_ACCESS_VIOLATION (原生访问违规，driver 实现损坏)");
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

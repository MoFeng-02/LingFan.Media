using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Backends.VLCNative;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Renderers.OpenGL;
using LingFan.Media.Renderers.Vulkan;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HeadfulPlaybackProbe;

// Program 的环境定位/GPU 探测/参数解析段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    // VLC 原生库定位（libvlc.dll 所在目录）：仅 --backend vlc 时用于前置 PATH

    /// <summary>定位原生 libvlc 目录（libvlc.dll 所在目录）：优先探针自带分发的 NuGet 原生包，其次系统已安装的 VLC。</summary>
    private static string? LocateLibVlc()
    {
        string baseDir = AppContext.BaseDirectory;
        string? bundled = FindBundledLibVlc(baseDir);
        if (bundled is not null) return bundled;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "libvlc.dll")))
                    return dir;
            }
            catch { /* 忽略无权限目录 */ }
        }

        var candidates = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string? pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string? pfX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (pf is not null) candidates.Add(Path.Combine(pf, "VideoLAN", "VLC"));
            if (pfX86 is not null) candidates.Add(Path.Combine(pfX86, "VideoLAN", "VLC"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            candidates.Add("/usr/lib/x86_64-linux-gnu");
            candidates.Add("/usr/lib");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add("/Applications/VLC.app/Contents/MacOS/lib");
            candidates.Add("/usr/local/lib");
        }

        foreach (var cand in candidates)
        {
            try
            {
                if (File.Exists(Path.Combine(cand, "libvlc.dll")) ||
                    File.Exists(Path.Combine(cand, "libvlc.so")) ||
                    File.Exists(Path.Combine(cand, "libvlc.dylib")))
                    return cand;
            }
            catch { /* 忽略 */ }
        }

        return null;
    }

    /// <summary>在输出目录树中找含 libvlc.dll 的目录（NuGet 原生包随构建复制的位置不固定）。优先与当前进程架构匹配的那一份。</summary>
    private static string? FindBundledLibVlc(string startDir)
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => "x64"
        };
        string archDir = Path.Combine(startDir, "libvlc", "win-" + arch);
        if (File.Exists(Path.Combine(archDir, "libvlc.dll")))
            return archDir;

        if (File.Exists(Path.Combine(startDir, "libvlc.dll")))
            return startDir;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(startDir))
            {
                if (File.Exists(Path.Combine(sub, "libvlc.dll")))
                    return sub;
                foreach (var sub2 in Directory.EnumerateDirectories(sub))
                {
                    if (File.Exists(Path.Combine(sub2, "libvlc.dll")))
                        return sub2;
                }
            }
        }
        catch { /* 忽略无权限/并发删除 */ }
        return null;
    }

    // 参数 / 资源解析辅助

    private static bool HasFlag(string[] args, params string[] flags)
    {
        foreach (var a in args)
            foreach (var f in flags)
                if (string.Equals(a, f, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static double ParseDouble(string[] args, string name, double fallback)
    {
        var v = ParseOption(args, name);
        return double.TryParse(v, out var d) ? d : fallback;
    }

    /// <summary>
    /// 查默认适配器与首选适配器（独显优先）的 VideoProcessor 对 NV12/BGRA 的输入/输出支持
    /// （诊断零拷贝链路的驱动级格式支持矩阵）。--probe-gpus 触发。
    /// </summary>
    private static void ProbeGpuVideoProcessorSupport()
    {
        ProbeAdapter("默认适配器(通常 AMD 核显)", useAdapter: false, IntPtr.Zero);
        IntPtr pref = LingFan.Media.GPUShare.D3D11.D3D11Interop.FindPreferredAdapter();
        if (pref != IntPtr.Zero)
        {
            try
            {
                ProbeAdapter("首选适配器(独显优先)", useAdapter: true, pref);
            }
            finally
            {
                LingFan.Media.GPUShare.D3D11.D3D11Interop.Release(pref);
            }
        }
        else
        {
            Console.WriteLine("[GPU-PROBE] 无首选适配器（回退默认路径）");
        }
    }

    private static void ProbeAdapter(string name, bool useAdapter, IntPtr selectedAdapter)
    {
        try
        {
            IntPtr dev, ctx;
            if (useAdapter)
                LingFan.Media.GPUShare.D3D11.D3D11Interop.D3D11CreateDeviceOnAdapter(selectedAdapter, out dev, out ctx);
            else
                LingFan.Media.GPUShare.D3D11.D3D11Interop.D3D11CreateDevice(out dev, out ctx);
            // 设备→GPU 归属验证（QI IDXGIDevice → GetAdapter → GetDesc1）。
            var (devVendor, devDesc) = LingFan.Media.GPUShare.D3D11.D3D11Interop.GetDeviceAdapterInfo(dev);
            Console.WriteLine($"[GPU-PROBE] 设备归属: vendor=0x{devVendor:X4} [{devDesc}] " +
                              (useAdapter ? "(首选适配器)" : "(默认路径)"));
            try
            {
                IntPtr vdev = LingFan.Media.GPUShare.D3D11.D3D11Interop.QueryInterface(dev, LingFan.Media.GPUShare.D3D11.D3D11Interop.IID_ID3D11VideoDevice);
                try
                {
                    var desc = new LingFan.Media.GPUShare.D3D11.D3D11VideoProcessorContentDescription
                    {
                        InputFrameFormat = 0,
                        InputFrameRate = new LingFan.Media.GPUShare.D3D11.DxgiRational { Numerator = 60, Denominator = 1 },
                        InputWidth = 1080,
                        InputHeight = 1920,
                        OutputFrameRate = new LingFan.Media.GPUShare.D3D11.DxgiRational { Numerator = 60, Denominator = 1 },
                        OutputWidth = 1080,
                        OutputHeight = 1920,
                        Usage = 0,
                    };
                    IntPtr enumPtr = LingFan.Media.GPUShare.D3D11.D3D11Interop.CreateVideoProcessorEnumerator(vdev, desc);
                    try
                    {
                        uint nv12 = LingFan.Media.GPUShare.D3D11.D3D11Interop.CheckVideoProcessorFormat(enumPtr, 41);
                        uint bgra = LingFan.Media.GPUShare.D3D11.D3D11Interop.CheckVideoProcessorFormat(enumPtr, 87);
                        Console.WriteLine($"[GPU-PROBE] {name}: NV12=0x{nv12:X}[IN={(nv12 & 1) != 0} OUT={(nv12 & 2) != 0}] " +
                                          $"BGRA=0x{bgra:X}[IN={(bgra & 1) != 0} OUT={(bgra & 2) != 0}]");
                    }
                    finally
                    {
                        LingFan.Media.GPUShare.D3D11.D3D11Interop.Release(enumPtr);
                    }
                }
                finally
                {
                    LingFan.Media.GPUShare.D3D11.D3D11Interop.Release(vdev);
                }
            }
            finally
            {
                LingFan.Media.GPUShare.D3D11.D3D11Interop.Release(ctx);
                LingFan.Media.GPUShare.D3D11.D3D11Interop.Release(dev);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GPU-PROBE] {name} 探测失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>优先用输出目录下随工程复制的 Resources；回退向上找仓库根。</summary>
    private static string? ResolveDefaultMedia(bool wantVideo)
    {
        string rel = wantVideo ? Path.Combine("Resources", "Video", "m1.mp4")
                               : Path.Combine("Resources", "Audio", "crickets_night01.mp3");
        string local = Path.Combine(AppContext.BaseDirectory, rel);
        if (File.Exists(local)) return local;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string candidate = Path.Combine(dir.FullName, rel);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

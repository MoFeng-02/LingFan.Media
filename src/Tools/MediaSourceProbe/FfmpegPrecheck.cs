using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MediaSourceProbe;

/// <summary>
/// FFmpeg 原生库预检：逐个尝试加载核心共享库，把缺失或加载失败直接打在启动段。
/// </summary>
/// <remarks>
/// 仅在 Windows 上执行；其他平台由自绑定加载器的递归扫描兜底。
/// 预检失败意味着后续打开必然失败，故在构造源之前返回失败，避免把“原生库问题”误读成“源问题”。
/// </remarks>
internal static class FfmpegPrecheck
{
    private static readonly string[] CoreLibraries = { "avutil-60", "swresample-6", "swscale-9", "avcodec-62", "avformat-62" };

    /// <summary>执行预检。返回 false 表示原生库不可用，调用方应以退出码 3 结束。</summary>
    public static bool Run()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        string directory = AppContext.BaseDirectory;
        Console.WriteLine($"FFmpeg 原生库预检（目录: {directory}）:");

        bool ok = true;
        foreach (var library in CoreLibraries)
        {
            string dll = Path.Combine(directory, library + ".dll");
            if (!File.Exists(dll))
            {
                Console.WriteLine($"  [缺失] {library}.dll");
                ok = false;
                continue;
            }

            if (NativeLibrary.TryLoad(dll, out _))
            {
                Console.WriteLine($"  [OK]   {library}.dll");
                continue;
            }

            int errorCode = Marshal.GetLastWin32Error();
            string message = errorCode != 0 ? new Win32Exception(errorCode).Message : "未知（可能为 ABI/版本不兼容）";
            Console.WriteLine($"  [失败] {library}.dll —— Win32 0x{errorCode:X8}: {message}");
            ok = false;
        }

        if (!ok)
            Console.WriteLine("FFmpeg 原生库加载失败：确认 VC++ 运行库已装、ThirdParty/ffmpeg 已解包。");

        Console.WriteLine();
        return ok;
    }
}

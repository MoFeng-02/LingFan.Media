using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FfmpegHeadfulPlaybackProbe;

// Program 的参数/资源解析段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
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

    private static string? ResolveDefaultMedia(bool wantVideo)
    {
        string rel = wantVideo ? Path.Combine("Resources", "Video", "m2.mp4")
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

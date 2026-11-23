using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaPlaybackProbe;

// Program 的格式化/WAV/参数解析段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    private static string FormatTs(TimeSpan ts) =>
        ts == TimeSpan.MinValue ? "(无)" : $"{ts.TotalSeconds:F2}s";

    private static int BytesPerSample(SampleFormat f) => f switch
    {
        SampleFormat.S16 => 2,
        _ => 4
    };

    /// <summary>
    /// 把原始 PCM 写成标准 WAV。S16 用 WAVE_FORMAT_PCM(1)，F32/S32 用 IEEE_FLOAT(3)/PCM(1)。
    /// </summary>
    private static void WriteWav(string path, byte[] pcm, int sampleRate, int channels, SampleFormat fmt)
    {
        int bps = BytesPerSample(fmt);
        int bits = bps * 8;
        short audioFormat = fmt == SampleFormat.F32 ? (short)3 : (short)1;   // 3 = IEEE float
        int blockAlign = channels * bps;
        int byteRate = sampleRate * blockAlign;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs, Encoding.ASCII);

        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                       // PCM fmt chunk 大小
        w.Write(audioFormat);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bits);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
    }

    private static string? ParseOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>优先用输出目录下随工程复制的 Resources\Video\m1.mp4。</summary>
    private static string? ResolveDefaultMedia()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "Resources", "Video", "m1.mp4");
        if (File.Exists(local)) return local;

        // 回退：从当前目录向上找仓库根的 Resources
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Resources", "Video", "m1.mp4");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

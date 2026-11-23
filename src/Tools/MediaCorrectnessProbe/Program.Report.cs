using System.Diagnostics;
using System.Text;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaCorrectnessProbe;

// Program 的校验/统计/JSON 报告段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    private static void Check(ref int fails, bool ok, string title, string detail)
    {
        if (!ok) fails++;
        Console.WriteLine($"  {(ok ? "OK" : "FAIL")} {title,-32} {detail}");
    }

    /// <summary>计算一段 PCM 的平方和 / 峰值 / 样本数（归一化 ±1.0）。</summary>
    private static (double SumSq, double Peak, int N) Level(ReadOnlySpan<byte> span, SampleFormat fmt)
    {
        double sumSq = 0, peak = 0;
        int n;
        switch (fmt)
        {
            case SampleFormat.S16:
            {
                var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(span);
                n = s.Length;
                for (int i = 0; i < n; i++)
                {
                    double v = s[i] / 32768.0;
                    sumSq += v * v;
                    double a = Math.Abs(v); if (a > peak) peak = a;
                }
                break;
            }
            case SampleFormat.F32:
            {
                var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(span);
                n = s.Length;
                for (int i = 0; i < n; i++)
                {
                    double v = s[i];
                    sumSq += v * v;
                    double a = Math.Abs(v); if (a > peak) peak = a;
                }
                break;
            }
            default:
            {
                var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(span);
                n = s.Length;
                for (int i = 0; i < n; i++)
                {
                    double v = s[i] / 2147483648.0;
                    sumSq += v * v;
                    double a = Math.Abs(v); if (a > peak) peak = a;
                }
                break;
            }
        }
        return (sumSq, peak, n);
    }

    private static string BuildJson(
        int videoCount, int audioCount, bool hasAudio, int nv12Count, int nonNv12Count,
        int fw, int fh, int sessVW, int sessVH, int sessAR, int sessACH,
        bool ptsMonotonic, bool dimsConsistent, ImageUtil.LumaStats luma,
        double audioRms, double audioPeak, TimeSpan duration,
        TimeSpan lastVideoTs, TimeSpan lastAudioTs,
        string? contactSheet, string? waveform, string? wav)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"videoFrames\": {videoCount},");
        sb.AppendLine($"  \"audioFrames\": {audioCount},");
        sb.AppendLine($"  \"hasAudio\": {(hasAudio ? "true" : "false")},");
        sb.AppendLine($"  \"nv12Frames\": {nv12Count},");
        sb.AppendLine($"  \"nonNv12Frames\": {nonNv12Count},");
        sb.AppendLine($"  \"frameWidth\": {fw},");
        sb.AppendLine($"  \"frameHeight\": {fh},");
        sb.AppendLine($"  \"sessionVideoWidth\": {sessVW},");
        sb.AppendLine($"  \"sessionVideoHeight\": {sessVH},");
        sb.AppendLine($"  \"sessionAudioSampleRate\": {sessAR},");
        sb.AppendLine($"  \"sessionAudioChannels\": {sessACH},");
        sb.AppendLine($"  \"durationSec\": {duration.TotalSeconds:F3},");
        sb.AppendLine($"  \"lastVideoTsSec\": {(lastVideoTs == TimeSpan.MinValue ? 0 : lastVideoTs.TotalSeconds):F3},");
        sb.AppendLine($"  \"lastAudioTsSec\": {(lastAudioTs == TimeSpan.MinValue ? 0 : lastAudioTs.TotalSeconds):F3},");
        sb.AppendLine($"  \"ptsMonotonic\": {(ptsMonotonic ? "true" : "false")},");
        sb.AppendLine($"  \"dimsConsistent\": {(dimsConsistent ? "true" : "false")},");
        sb.AppendLine($"  \"lumaMin\": {luma.Min},");
        sb.AppendLine($"  \"lumaMax\": {luma.Max},");
        sb.AppendLine($"  \"lumaMean\": {luma.Mean:F2},");
        sb.AppendLine($"  \"lumaStd\": {luma.Std:F2},");
        sb.AppendLine($"  \"audioRms\": {audioRms:E6},");
        sb.AppendLine($"  \"audioPeak\": {audioPeak:E6},");
        sb.AppendLine($"  \"artifacts\": {{ \"contactSheet\": \"{J(contactSheet)}\", \"waveform\": \"{J(waveform)}\", \"wav\": \"{J(wav)}\" }},");
        sb.AppendLine($"  \"generatedAt\": \"{J(DateTime.Now.ToString("O"))}\"");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string J(string? s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string FormatTs(TimeSpan ts) => ts == TimeSpan.MinValue ? "(无)" : $"{ts.TotalSeconds:F2}s";

    private static string? ParseOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int ParseInt(string[] args, string name, int fallback) =>
        int.TryParse(ParseOption(args, name), out var v) ? v : fallback;

    private static string? ResolveDefaultMedia()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "Resources", "Video", "m1.mp4");
        if (File.Exists(local)) return local;
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Resources", "Video", "m1.mp4");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>默认产出目录：仓库根 <c>TestInfo\Diagnostics</c>（与既有测试工件同处，便于对比）。</summary>
    private static string ResolveDiagnosticsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LingFan.Media.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "TestInfo", "Diagnostics");
    }
}

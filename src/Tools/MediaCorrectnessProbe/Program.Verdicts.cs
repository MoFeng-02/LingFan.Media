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

// Program 的逐条判定段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>逐条正确性判定（帧产出/尺寸/PTS 单调/亮度/音频电平/时长覆盖），返回失败项数。</summary>
    private static int RunVerdicts(
        int videoCount, int audioCount, bool hasAudio, int nonNv12Count,
        int firstW, int firstH, int sessVW, int sessVH,
        bool ptsMonotonic, bool dimsConsistent, ImageUtil.LumaStats luma,
        double audioRms, double audioPeak, TimeSpan duration, TimeSpan lastTs, TimeSpan lastAudioTs,
        double maxSeconds, double firstSilentSec)
    {
    // ================= 逐条判定 =================
    Console.WriteLine();
    Console.WriteLine("=== 判定 ===");
    bool dimsMatchSession = sessVW <= 0 || (dimsConsistent && firstW == sessVW && firstH == sessVH);
    int fails = 0;

    Check(ref fails, videoCount > 0, "视频产出帧", $"videoFrames={videoCount}");
    if (hasAudio)
        Check(ref fails, audioCount > 0, "音频产出 PCM 帧", $"audioFrames={audioCount}");
    Check(ref fails, dimsConsistent, "所有视频帧尺寸一致", $"{firstW}x{firstH}");
    Check(ref fails, dimsMatchSession, "帧尺寸 == 解封装轨尺寸", $"帧 {firstW}x{firstH} vs 轨 {sessVW}x{sessVH}");
    Check(ref fails, ptsMonotonic, "视频 PTS 单调非递减", $"末帧 {FormatTs(lastTs)}");
    Check(ref fails, luma.Max - luma.Min > 8, "解码画面亮度存在真实变化（非空白/乱码）",
          $"luma=[{luma.Min},{luma.Max}] std={luma.Std:F2}");
    if (hasAudio)
        Check(ref fails, audioRms > 1e-4, "音频 RMS > 0（真实信号而非全零）",
              $"RMS={audioRms:E3} Peak={audioPeak:E3}");

    // 完整性：解码覆盖的时间跨度应接近 min(容器时长, --max-seconds)
    // 注：当用户传 --max-seconds 主动截断时，断言以截断上限为准（不为 8s 截断报"未覆盖 31s 完整时长"假警）
    if (duration > TimeSpan.Zero && hasAudio && lastAudioTs != TimeSpan.MinValue)
    {
        double targetSeconds = Math.Min(duration.TotalSeconds, maxSeconds);
        Check(ref fails, lastAudioTs.TotalSeconds >= targetSeconds - 2.0,
              "音频解码覆盖完整时长",
              $"末帧时间戳 {lastAudioTs.TotalSeconds:F2}s / 目标 {targetSeconds:F2}s" +
              $"（容器 {duration.TotalSeconds:F2}s，max-seconds={maxSeconds}s）");
    }

    if (firstSilentSec > 0)
        Console.WriteLine($"  注意：墙钟 {firstSilentSec:F2}s 起出现「解出静音」窗口（电平 < -80dBFS）。");
    if (nonNv12Count > 0)
        Console.WriteLine($"  提示：{nonNv12Count} 帧非 NV12 软件帧（GPU 资源或其它像素格式），未纳入联系表。");

    Console.WriteLine();
    if (fails == 0)
    {
        Console.WriteLine("  全部正确性断言通过 ⇒ 解封装 + 解码链路无辜。");
        Console.WriteLine("     下一步：打开上面的 decoded.wav 与 contact_sheet.png 做肉眼终审——");
        Console.WriteLine("     WAV 完整有声 + 联系表画面正常 ⇒ 任何「听不到/看不到」只可能在渲染/输出侧（见 b1 / b3）。");
    }
    else
    {
        Console.WriteLine($"  {fails} 项判定失败 ⇒ 问题落在解封装/解码，与 WASAPI、D3D11 无关。");
    }
    Console.WriteLine();
    Console.WriteLine("=== b2 完成。把以上输出整段贴回即可。 ===");
        return fails;
    }
}

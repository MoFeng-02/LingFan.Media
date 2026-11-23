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

// Program 的重播验收段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>重播验收：Ended→Playing 无缝从头（状态机转换/时钟归零/双管线重启呈现计数）。</summary>
    private static async Task<bool> RunReplayAsync(
        IMediaPlayer player, CountingVideoRendererFactory? countingFactory,
        bool doVideo, bool visible, bool verbose, int presentCount)
    {
            Console.WriteLine();
            Console.WriteLine("[HEADFUL-REPLAY] 第一次播放已 Ended，立即二次 PlayAsync 验证重播无缝从头…");
            double posBeforeReplay = player.Position.TotalSeconds;
            int presentBeforeReplay = presentCount;
            await player.PlayAsync();
            double posAfterReplay = player.Position.TotalSeconds;
            Console.WriteLine($"  [HEADFUL-REPLAY] 二次 PlayAsync 后：state={player.State} " +
                              $"pos={player.Position:g}（重播前 pos={posBeforeReplay:F2}s）");
            bool replayStateOk = player.State == MediaState.Playing && posAfterReplay < 1.0;

            var replayPoll = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(500);
                if (doVideo && countingFactory?.Last is not null)
                    presentCount = countingFactory.Last.PresentCount;
                if (visible || verbose)
                    Console.WriteLine($"  [REPLAY] t={replayPoll.Elapsed.TotalSeconds:F1}s " +
                                      $"pos={player.Position:g} present={presentCount} state={player.State}");
                if (player.State == MediaState.Ended) break;
                double cap = player.Duration.TotalSeconds + 8;
                if (replayPoll.Elapsed.TotalSeconds > cap)
                {
                    Console.WriteLine("  [HEADFUL-REPLAY] 超时未收到第二次 Ended（兜底退出）");
                    break;
                }
            }
            int presentAfterReplay = presentCount;
            int replayPresentDelta = presentAfterReplay - presentBeforeReplay;
            bool replayPresentOk = !doVideo || replayPresentDelta >= 900;
            bool replayPass = replayStateOk && replayPresentOk;
            Console.WriteLine($"  [HEADFUL-REPLAY] 二次播放结束 state={player.State} present 增量={replayPresentDelta}");
            Console.WriteLine($"  [HEADFUL-REPLAY] => {(replayPass ? "PASS" : "FAIL")} " +
                              $"(state重启={replayStateOk}, 二次呈现≈{replayPresentDelta}{(doVideo ? "" : "[无视频跳过计数]")})");
        return replayPass;
    }
}

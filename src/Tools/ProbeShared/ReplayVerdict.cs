using System.Diagnostics;
using LingFan.Media.Abstractions;

namespace ProbeShared;

/// <summary>
/// 重播验收：Ended→Playing 无缝从头（状态机转换 / 时钟归零 / 双管线重启呈现计数）。
/// </summary>
/// <remarks>
/// 第一次自然结束后立即二次 PlayAsync，验证：
/// (a) 状态机 Ended→Playing 的合法转换；
/// (b) 时钟归零、位置回绕到 0（PlayAsync 在 Ended 态走 SeekAsync(0) + 时钟重置分支）；
/// (c) 双管线从排干态重启，present 从 0 重新开始累计。
/// </remarks>
public static class ReplayVerdict
{
    /// <summary>执行重播验收并打印判据。</summary>
    /// <param name="player">已自然 Ended 的播放器。</param>
    /// <param name="countingFactory">计数装饰器工厂（无视频用例传 null）。</param>
    /// <param name="doVideo">是否有视频轨。</param>
    /// <param name="visible">是否打印轮询明细。</param>
    /// <param name="verbose">是否打印轮询明细。</param>
    /// <param name="presentCount">重播前呈现计数。</param>
    /// <param name="minPresentDelta">二次播放呈现增量下限（实时能力内素材 ≈900；超能力软解素材按收尾语义放宽）。</param>
    /// <param name="allowOverCapabilityGrace">超软解实时能力素材（丢帧&gt;0）是否按收尾语义放宽：
    /// 二次自然 Ended + 有真实呈现增量即合格（丢帧属预期降级，全量呈现物理不可达）。</param>
    /// <returns>重播判据是否通过。</returns>
    public static async Task<bool> RunAsync(
        IMediaPlayer player, CountingVideoRendererFactory? countingFactory,
        bool doVideo, bool visible, bool verbose, int presentCount, int minPresentDelta = 900,
        bool allowOverCapabilityGrace = false)
    {
        Console.WriteLine();
        Console.WriteLine("[HEADFUL-REPLAY] 第一次播放已 Ended，立即二次 PlayAsync 验证重播无缝从头…");
        int presentBeforeReplay = presentCount;
        double posBeforeReplay = player.Position.TotalSeconds;
        await player.PlayAsync();
        double posAfterReplay = player.Position.TotalSeconds;
        Console.WriteLine($"  [HEADFUL-REPLAY] 二次 PlayAsync 后：state={player.State} " +
                          $"pos={player.Position:g}（重播前 pos={posBeforeReplay:F2}s）");
        // 重播合法 + 位置已归零（非 Ended 态遗留的末尾位置）
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
        // 无视频用例跳过呈现计数判定；超能力素材（丢帧>0）按收尾语义放宽（可选档）
        bool overCapability = allowOverCapabilityGrace && player.VideoDroppedFrames > 0;
        bool replayPresentOk = !doVideo
            || replayPresentDelta >= minPresentDelta
            || (overCapability && player.State == MediaState.Ended && replayPresentDelta > 0);
        bool replayPass = replayStateOk && replayPresentOk;
        Console.WriteLine($"  [HEADFUL-REPLAY] 二次播放结束 state={player.State} " +
                          $"present 增量={replayPresentDelta} Duration={player.Duration:g}");
        Console.WriteLine($"  [HEADFUL-REPLAY] => {(replayPass ? "PASS" : "FAIL")} " +
                          $"(state重启={replayStateOk}, 二次呈现≈{replayPresentDelta}{(doVideo ? "" : "[无视频跳过计数]")}" +
                          $"{(overCapability && doVideo ? ", 超软解能力素材按收尾语义判定]" : "")})");

        return replayPass;
    }
}
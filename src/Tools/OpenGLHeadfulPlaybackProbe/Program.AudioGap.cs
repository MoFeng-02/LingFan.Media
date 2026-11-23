using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Backends.VLCNative;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.OpenGL;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenGLHeadfulPlaybackProbe;

// Program 的音频间隙检测段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>音频间隙检测：主时钟位置 vs 已提交音频时长（backlog>150ms 记间隙；提交停滞单独计数）。</summary>
    private static void DetectAudioGap(
        IMediaPlayer player, Stopwatch poll, bool verbose, long submittedSamples, int sampleRate,
        ref double maxAudioBacklogMs, ref int audioGapCount, ref int audioStallCount,
        ref long prevSubmitted, ref double prevPosSec)
    {
        if (player.State != MediaState.Playing || sampleRate <= 0) return;
            double subSec = submittedSamples / (double)sampleRate;
            double posSec = player.Position.TotalSeconds;
            double backlogMs = (posSec - subSec) * 1000.0;
            if (backlogMs > maxAudioBacklogMs) maxAudioBacklogMs = backlogMs;
            if (backlogMs > 150)
            {
                audioGapCount++;
                if (verbose)
                    Console.WriteLine($"  [HEADFUL-AUDIO-GAP] t={poll.Elapsed.TotalSeconds:F1}s " +
                                      $"backlog={backlogMs:F0}ms（音频落后主时钟，疑似断音/欠载）");
            }
            // 提交停滞：主时钟前进 >100ms 但采样数未变（prevSubmitted!=0 跳过首轮误报）
            if (prevSubmitted != 0 && submittedSamples == prevSubmitted && posSec > prevPosSec + 0.1)
            {
                audioStallCount++;
                if (verbose)
                    Console.WriteLine($"  [HEADFUL-AUDIO-GAP] STALL t={poll.Elapsed.TotalSeconds:F1}s " +
                                      $"提交停滞，主时钟已前进 {(posSec - prevPosSec) * 1000:F0}ms（批量提交节奏，非欠载；以 backlog 为准）");
            }
            prevSubmitted = submittedSamples;
            prevPosSec = posSec;
    }
}

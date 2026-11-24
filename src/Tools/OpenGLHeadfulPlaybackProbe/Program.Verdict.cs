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
using ProbeShared;

namespace OpenGLHeadfulPlaybackProbe;

// Program 的验收判定段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>播放结束后的视频/音频验收判定（呈现计数、零拷贝出餐计数、音频 backlog 间隙）。</summary>
    private static (bool VideoPass, bool AudioPass) EvaluateResults(
        IMediaPlayer player, CountingVideoRendererFactory? countingFactory,
        bool doVideo, bool doAudio, bool hwMode, string? backendArg,
        double playedSec, double seconds, long gpuServed, long cpuServed,
        long submittedSamples, int sampleRate, double maxAudioBacklogMs,
        int audioGapCount, int audioStallCount, ref int presentCount)
    {
        bool videoPass = !doVideo;
        bool audioPass = !doAudio;
        if (doVideo)
        {
            if (countingFactory?.Last is not null)
                presentCount = countingFactory.Last.PresentCount;
            videoPass = presentCount >= 5;
            Console.WriteLine($"[HEADFUL-VIDEO] glPresentCount={presentCount}  => " +
                              $"{(videoPass ? "PASS" : "FAIL (present<5)")}");
            // 诊断：视频丢帧数。与 present 计数对照可判断尾帧是被 Synchronizer 判定丢弃还是已呈现。
            Console.WriteLine($"[HEADFUL-VIDEO-DROP] droppedFrames={player.VideoDroppedFrames} present={presentCount}");
            // —— 全链路零拷贝判定（出餐端实测，仅 --hw 有意义）——
            if (hwMode)
            {
                Console.WriteLine($"[HEADFUL-ZEROCOPY] GPU纹理帧={gpuServed} CPU内存帧={cpuServed}");
                if (backendArg == "ffmpeg")
                    Console.WriteLine($"[HEADFUL-ZEROCOPY] ffmpeg硬解零拷贝 => {(gpuServed > 0 ? "PASS" : "FAIL (无 GPU 纹理帧，已回落 CPU)")}");
                else
                    Console.WriteLine($"[HEADFUL-ZEROCOPY] 注：--backend {backendArg} 设计上不走零拷贝（VLC 恒回拷 CPU / MF 半 DXVA），仅作计数参考");
            }
        }
        if (doAudio)
        {
            int sr = sampleRate > 0 ? sampleRate : 44100;
            double subSec = submittedSamples / (double)sr;
            double minPlayed = Math.Max(2.0, seconds - 1.0);
            audioPass = playedSec >= minPlayed;
            Console.WriteLine($"[HEADFUL-AUDIO] played={playedSec:F1}s submitted≈{subSec:F1}s  => " +
                              $"{(audioPass ? "PASS" : $"FAIL (played<{minPlayed:F0}s)")}");
            bool realAudioGap = maxAudioBacklogMs > 150;
            Console.WriteLine($"[HEADFUL-AUDIO-GAP] maxBacklog={maxAudioBacklogMs:F0}ms " +
                              $"gaps(>150ms)={audioGapCount} stalls={audioStallCount} " +
                              $"=> {(realAudioGap ? "检测到音频间隙（真欠载）" : "未检测到间隙")}");
            if (audioStallCount > 0 && !realAudioGap)
                Console.WriteLine($"  [HEADFUL-AUDIO-GAP] 注：stalls={audioStallCount} 均发生在 backlog=0 的连续播放中，" +
                                  $"属批量提交节奏（非欠载/断音）；真欠载以 backlog>150ms 为准。");
        }
        return (videoPass, audioPass);
    }
}

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
using LingFan.Media.Renderers.Vulkan;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace VulkanHeadfulPlaybackProbe;

// Program 的验收判定段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>播放结束后的视频/音频验收判定（呈现计数、零拷贝出餐计数、分相计时、音频 backlog 间隙）。</summary>
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
            Console.WriteLine($"[HEADFUL-VIDEO] vulkanPresentCount={presentCount}  => " +
                              $"{(videoPass ? "PASS" : "FAIL (present<5)")}");
            // 诊断：视频丢帧数。与 present 计数对照可判断尾帧是被 Synchronizer 判定丢弃还是已呈现。
            Console.WriteLine($"[HEADFUL-VIDEO-DROP] droppedFrames={player.VideoDroppedFrames} present={presentCount}");
            // 零拷贝验收（仅 --hw 打印）：ffmpeg 经 D3D11VA→NV12→RGBA 共享句柄由 VulkanGpuFrameProducer
            // 导入零拷贝上屏，GPU 纹理帧>0 才算真零拷贝；VLC 恒回拷 CPU / MF 半 DXVA 仅作计数参考，不判 PASS。
            if (hwMode)
            {
                Console.WriteLine($"[HEADFUL-ZEROCOPY] GPU纹理帧={gpuServed} CPU内存帧={cpuServed}");
                if (backendArg == "ffmpeg")
                    Console.WriteLine($"[HEADFUL-ZEROCOPY] ffmpeg硬解零拷贝 => {(gpuServed > 0 ? "PASS" : "FAIL (无 GPU 纹理帧，已回落 CPU)")}");
                else
                    Console.WriteLine($"[HEADFUL-ZEROCOPY] 注：--backend {backendArg} 设计上不走零拷贝（VLC 恒回拷 CPU / MF 半 DXVA），仅作计数参考");
            }
            // 诊断：分相计时——定位每帧开销归属（CPU 转换 vs GPU 同步 QueueWaitIdle）。
            // 若 GPU 同步占比极高 → 瓶颈在每帧全队列同步，需改 fence 环消除串行；
            // 若 CPU 转换占比极高 → 瓶颈在软解 NV12→BGRA，需 SIMD/降分辨率上传等。
            string? prof = countingFactory?.Last?.GetInnerProfile();
            if (prof is not null) Console.WriteLine($"[HEADFUL-VIDEO-PROFILE] {prof}");
        }
        if (doAudio)
        {
            int sr = sampleRate > 0 ? sampleRate : 44100;
            double subSec = submittedSamples / (double)sr;
            double minPlayed = Math.Max(2.0, seconds - 1.0);
            audioPass = playedSec >= minPlayed;
            Console.WriteLine($"[HEADFUL-AUDIO] played={playedSec:F1}s submitted≈{subSec:F1}s  => " +
                              $"{(audioPass ? "PASS" : $"FAIL (played<{minPlayed:F0}s)")}");
            // 间隙诊断摘要：真欠载信号只有「主时钟跑过已提交音频时长」，即 backlog>150ms（设备把缓冲播干）。
            // stalls（submittedSamples 跨轮未变）是批量提交造成的测量假象：SubmitBatch 整段 WaitForBufferSpace
            // 期间 submittedSamples 不变而主时钟平滑前进，故不能作欠载依据，断言只认 maxBacklog。
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

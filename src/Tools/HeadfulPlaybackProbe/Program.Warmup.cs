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
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Renderers.OpenGL;
using LingFan.Media.Renderers.Vulkan;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HeadfulPlaybackProbe;

// Program 的预热段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>MF 解码器与 WASAPI 音频引擎预热（opt-in）。失败一律降级为未预热，不影响播放。</summary>
    private static async Task WarmupAsync(ServiceProvider sp, string? backendArg, bool audioWarmup, string file)
    {
    // —— MF 预热：把「解码器 MFT 首次激活」的冷启动成本挪到可见窗口出现之前 ——
        // 预热提前打开一次目标文件、强制激活 H.264/AAC 解码器，正式 OpenAsync 复用已加载的
        // 解码器，几乎瞬时完成，窗口一出现即可出画面，避免窗口出现后长时间空屏。
        // 仅 MF 后端需要（FFmpeg/VLC 无此预热路径）。
        if (backendArg == "mf")
        {
        var warmSw = Stopwatch.StartNew();
        try
        {
            var mf = sp.GetRequiredService<MFBackend>();
            mf.Warmup(file);
            Console.WriteLine($"[HEADFUL-MF] 预热耗时 {warmSw.Elapsed.TotalSeconds:F2}s（已提前激活解码器，正式打开将显著加快）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HEADFUL-MF] 预热失败（忽略，正式打开仍可用）: {ex.Message}");
        }
        }

        // —— WASAPI 预热（opt-in，--audio-warmup）——
        // 音频引擎/端点句柄由 Infrastructure 层单例持有（IAudioEngine / WasapiAudioEngine）。
        // 预热时在专用 STA 线程建立一条常驻 anchor 流（Initialize + 默认 Start），使 OS 音频引擎
        // 在进程内保持热态；后续每个 Transient 会话（WasapiOutput）的 IAudioClient.Initialize 走热路径，
        // 免去冷启动等待。会话状态对象保持 Transient，不升为单例：长期原生资源用单例、会话状态用
        // Transient，二者仅通过「OS 引擎已热」这一进程级副作用间接协作。
        // 预热失败一律降级为未预热，不影响播放。
        if (audioWarmup)
        {
            var audioWarmSw = Stopwatch.StartNew();
            try
            {
                var audioEngine = sp.GetRequiredService<IAudioEngine>();
                await audioEngine.WarmupAsync(CancellationToken.None);
                Console.WriteLine($"[HEADFUL-WASAPI] 预热耗时 {audioWarmSw.Elapsed.TotalSeconds:F2}s " +
                                  $"(EngineWarm={audioEngine.IsWarm}) 已拉起音频引擎，正式打开应显著加快");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HEADFUL-WASAPI] 预热失败（忽略，正式打开仍可用）: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("[HEADFUL-WASAPI] 未启用音频预热（加 --audio-warmup 开启；引擎常驻保活可消除冷启动等待）");
        }
    }
}

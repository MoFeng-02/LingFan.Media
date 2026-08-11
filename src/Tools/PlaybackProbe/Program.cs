using System.Diagnostics;
using System.Threading;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Backends.VLCNative;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PlaybackProbe;

/// <summary>
/// 开箱即用示例 / 标准测试入口：演示「一个 IMediaPlayer 接口 + DI 注入 → 直接可用，无头有头均可」。
/// </summary>
/// <remarks>
/// <para><b>设计哲学（与用户对齐）</b>：库只负责调度已注册的后端接口，自身不知道任何后端 / GPU。
/// 注册几个后端就按注册顺序逐个试，运行时单次判断失败即回退下一顺位（不预探测、不硬编码能力表）。
/// 命中即缓存，后续同样源直接命中；都不行抛 <see cref="MediaBackendUnsupportedException"/>。</para>
/// <para><b>无头 / 有头同一接口</b>：无头 = 订阅 <see cref="IMediaPlayer.VideoFrameAvailable"/> 做数据出餐；
/// 有头 = 在 UI 框架里把同一事件接到控件级 Present Sink。两者都走同一条无头管线，只是渲染/输出注册不同。</para>
/// <para><b>帧计算零假设</b>：逐帧转发真实 <see cref="VideoFrame.Timestamp"/> / <see cref="VideoFrame.Duration"/>，
/// 30/60/120fps 自适应完全由 Core 管线基于时间戳完成，本示例与中间层绝不引入「帧计数×固定间隔」。</para>
/// <para>用法：</para>
/// <code>
/// dotnet run --project src\Tools\PlaybackProbe
/// dotnet run --project src\Tools\PlaybackProbe -- --file "Resources/Video/m1.mp4"   // H264/MP4：MF 直接命中
/// dotnet run --project src\Tools\PlaybackProbe -- --file "xxx.webm"                  // WebM：MF 失败→回退 ffmpeg
/// dotnet run --project src\Tools\PlaybackProbe -- --file "xxx.hevc.mp4"              // HEVC：MF 失败→回退 ffmpeg
/// dotnet run --project src\Tools\PlaybackProbe -- --sound                           // 真实 WASAPI 出声（默认静音）
/// dotnet run --project src\Tools\PlaybackProbe -- --gpu                             // 用 D3D11 渲染器（默认无头 NoOp）
/// dotnet run --project src\Tools\PlaybackProbe -- --repeat                          // 同一源开两次，演示缓存命中
/// dotnet run --project src\Tools\PlaybackProbe -- --no-fusion                       // MF 零拷贝定界：关解码一体，走自管 MFT
/// </code>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var file = ParseOption(args, "--file") ?? "Resources/Video/m1.mp4";
        bool useSound = args.Contains("--sound");
        bool useGpu = args.Contains("--gpu");
        bool repeat = args.Contains("--repeat");
        bool verbose = args.Contains("-v") || args.Contains("--verbose");
        // 零拷贝定界开关：关闭 SourceReader「解封装+解码一体」，强制走 MFVideoDecoder 自管 MFT。
        //    自管 MFT 若能出 DXGI ⇒ 读回在 SourceReader 封装层；若同样出系统内存 ⇒ 读回在 MFT/驱动层。
        bool noFusion = args.Contains("--no-fusion");
        double seconds = ParseDouble(args, "--seconds") ?? 8.0;

        Console.WriteLine("=== LingFan.Media 开箱即用示例（多后端自动回退 + 数据出餐） ===");
        if (!File.Exists(file))
        {
            Console.WriteLine($"找不到媒体文件：{file}（用 --file 指定；WebM/HEVC 可演示回退）");
            return 2;
        }
        Console.WriteLine($"媒体文件 : {file}");
        Console.WriteLine($"渲染器   : {(useGpu ? "D3D11（有头路径，需 UI Present Sink 才实际上屏）" : "NoOp 无头（视频帧经事件出餐）")}");
        Console.WriteLine($"音频输出 : {(useSound ? "WASAPI 真实出声" : "静音（仅数据出餐）")}");
        if (noFusion)
            Console.WriteLine("MF 诊断   : --no-fusion 已开启 → 关闭 SourceReader 解码一体，强制 MFVideoDecoder 自管 MFT（零拷贝定界对照）");
        Console.WriteLine();

        var services = new ServiceCollection();
        services.AddLogging(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        // 🔑 开箱即用：注册多个后端（顺序=回退优先级）+ 一个渲染器 + 一个输出。
        // 之后 GetRequiredService<IMediaPlayer>() 拿到的就是「回退中间件」，完全不知道后端。
        // 顺序 MF → FFmpeg → VLC：贴合“264/MP4 由 MF 命中、WebM/HEVC 由 MF 失败再回退 ffmpeg”的回退叙事。
        var builder = services.AddLingFanMedia()
            .AddMediaFoundation(o => { if (noFusion) o.EnableReaderDecodeFusion = false; })
            .AddFFmpeg()
            .AddVLCNative();
        if (useGpu) builder.AddD3D11Renderer(); else builder.AddHeadlessRenderer();
        if (useSound) builder.AddWasapiOutput(); else builder.AddSilentAudioOutput();

        await using var sp = services.BuildServiceProvider();

        // 只读检视已注册后端组（证明多后端集合注册与回退顺序）。
        var registry = sp.GetRequiredService<IBackendRegistry>();
        Console.WriteLine($"已注册后端（回退顺序）: {string.Join(" > ", registry.Backends.Select(b => b.Name))}");
        Console.WriteLine();

        var player = sp.GetRequiredService<IMediaPlayer>();

        // —— 数据出餐计数（逐帧真实时间戳，证明零固定帧假设）——
        long videoFrames = 0, audioCallbacks = 0, audioSamples = 0;
        int sampleRate = 0;
        TimeSpan firstVidTs = TimeSpan.MinValue, lastVidTs = TimeSpan.MinValue;
        long lastVidDurMs = -1;

        // 出餐端帧路径检视（「全程零拷贝」的端到端证据）：
        // 解码器内部的 [FFMPEG-FRAMEPATH]/[DXVA-FRAMEPATH] 只能证明「解码器产出了 GPU 纹理」；
        // 这里检视的是**出餐那一刻**帧携带的资源类型——只有 IGpuTextureResource 才说明
        // 纹理一路借到了消费者手上，中途没有被下载回系统内存。两者都为 GPU 才算全链路零拷贝。
        long gpuServed = 0, cpuServed = 0;
        string gpuFmt = "-";
        void OnVideo(VideoFrame f)
        {
            Interlocked.Increment(ref videoFrames);
            if (f.Resource is IGpuTextureResource) { Interlocked.Increment(ref gpuServed); gpuFmt = f.Format.ToString(); }
            else Interlocked.Increment(ref cpuServed);
            if (firstVidTs == TimeSpan.MinValue) firstVidTs = f.Timestamp;
            lastVidTs = f.Timestamp;
            lastVidDurMs = (long)(f.Duration.TotalMilliseconds);   // 随帧率变化（30→33ms, 60→16ms, 120→8ms）
        }
        void OnAudio(AudioFrame f)
        {
            Interlocked.Increment(ref audioCallbacks);
            Interlocked.Add(ref audioSamples, f.FrameCount);
            if (sampleRate == 0) sampleRate = f.SampleRate;
        }

        player.VideoFrameAvailable += OnVideo;
        player.AudioDataAvailable += OnAudio;
        player.ErrorOccurred += (_, e) => Console.WriteLine($"[错误] {e.Message}");

        int opens = repeat ? 2 : 1;
        for (int n = 1; n <= opens; n++)
        {
            Console.WriteLine($"--- 第 {n} 次 Open（演示{(n == 2 ? "缓存命中" : "首次/回退")}）---");
            try
            {
                await player.OpenAsync(new FileMediaSource(file));
            }
            catch (MediaBackendUnsupportedException ex)
            {
                Console.WriteLine($"[失败] {ex.Message}");
                return 3;
            }
            Console.WriteLine($"时长={player.Duration:g} 状态={player.State}");

            await player.PlayAsync();

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var sw = Stopwatch.StartNew();
            try
            {
                while (!cts.IsCancellationRequested && player.State == MediaState.Playing)
                {
                    await Task.Delay(500, cts.Token);
                    Console.WriteLine($"  t={player.Position:g} 视频帧={videoFrames} 音频回调={audioCallbacks}");
                }
            }
            catch (OperationCanceledException) { /* 时间到 */ }
            sw.Stop();

            await player.StopAsync();
            Console.WriteLine($"第 {n} 次结束：视频帧累计={videoFrames}, 音频回调={audioCallbacks}, 丢帧={player.VideoDroppedFrames}");
            if (repeat && n == 1)
            {
                await player.DisposeAsync();   // 释放本次 session；下次 Open 重新建（缓存保留后端选择）
                player = sp.GetRequiredService<IMediaPlayer>();
                // 重新挂事件（Dispose 已解绑）；复用同一处理器，避免两份逻辑走偏（帧路径统计也随之延续）
                player.VideoFrameAvailable += OnVideo;
                player.AudioDataAvailable += OnAudio;
                player.ErrorOccurred += (_, e) => Console.WriteLine($"[错误] {e.Message}");
            }
        }

        await player.DisposeAsync();

        // —— 帧率自适应证据 ——
        Console.WriteLine();
        Console.WriteLine("=== 出餐汇总 ===");
        Console.WriteLine($"视频帧数     : {videoFrames}");
        Console.WriteLine($"首帧时间戳   : {firstVidTs:g}  末帧时间戳: {lastVidTs:g}");
        Console.WriteLine($"末帧时长(ms) : {lastVidDurMs}  (随源帧率变化，非固定值即证明无「固定帧」假设)");
        Console.WriteLine($"音频回调     : {audioCallbacks}  采样数: {audioSamples}  采样率: {sampleRate}");
        Console.WriteLine($"丢帧         : {player.VideoDroppedFrames}");

        // —— 全链路零拷贝判定（出餐端实测，非解码器自报）——
        Console.WriteLine();
        Console.WriteLine("=== 零拷贝判定（出餐端实测）===");
        Console.WriteLine($"GPU 纹理帧   : {gpuServed}   (IGpuTextureResource，格式 {gpuFmt})");
        Console.WriteLine($"CPU 内存帧   : {cpuServed}");
        Console.WriteLine(gpuServed > 0 && cpuServed == 0
            ? "判定         : 全程零拷贝——每一帧出餐时都是 GPU 纹理，解码到消费者之间无系统内存往返"
            : gpuServed > 0
                ? $"判定         : 部分零拷贝——{cpuServed} 帧回落到了 CPU 内存（常见于起播前若干软解帧 / 硬解中途失败）"
                : "判定         : 全程 CPU 帧——硬件没被用上。请查上方是否有「已请求硬件解码，但…」告警，" +
                  "或用 --gpu 注册 D3D11 渲染器与解码器共享设备");
        Console.WriteLine("解码器侧统计请见上方 [FFMPEG-FRAMEPATH] / [DXVA-FRAMEPATH] 日志（两侧都为 GPU 才是真·全链路零拷贝）");
        Console.WriteLine();
        Console.WriteLine("回退行为请见上方 [Playback] 日志：MF 不支持的源会显示「无法打开…回退下一顺位」→「已用后端 FFmpeg 打开」");
        return 0;
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static double? ParseDouble(string[] args, string name)
    {
        var s = ParseOption(args, name);
        return double.TryParse(s, out var v) ? v : null;
    }
}

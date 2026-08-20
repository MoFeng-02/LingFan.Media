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
using LingFan.Media.Backends.VulkanVideo;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.Vulkan;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VulkanVideoHardDecodeProbe;

/// <summary>
/// Vulkan 硬解零拷贝全链路验证程序：VulkanVideo 后端（VK_KHR_video_decode_h264）硬解出 NV12 VkImage，
/// 与 Vulkan 渲染器共用同一 VkDevice → 渲染器经 pattern matching 直接 blit 同设备纹理零拷贝上屏（GPU Present），
/// 音频走 FFmpeg 解封装/解码 + WASAPI 出声。两者共用同一个 MediaPlayer。
/// </summary>
/// <remarks>
/// <para>命令行开关用于隔离变量：</para>
/// <list type="bullet">
///   <item><c>--visible</c>：窗口真正可见，便于肉眼确认上屏。</item>
///   <item><c>--no-video</c>：只验 WASAPI 出声。</item>
///   <item><c>--no-audio</c>：只验 Vulkan 硬解上屏。</item>
///   <item><c>--category</c>：启用 IAudioClient2 会话分类做对照（默认 Movie）。</item>
///   <item><c>--scale</c>：Vulkan 缩放模式（fill=拉伸全屏 / uniform=信箱默认 / uniformtofill=高保真全屏）。</item>
/// </list>
/// <para>窗口代码使用 <c>[LibraryImport]</c> 以满足 AOT 要求。</para>
/// <para>硬解前提：源须为 H.264（VulkanVideoDecoder 仅支持 H.264），且 GPU 设备启用 VK_KHR_video_decode_* 并存在
/// video-decode 队列族（IGpuDeviceContext.VideoQueueFamilyIndex 有效）。不满足则 VulkanVideo 回落 FFmpeg 软解——
/// 本探针会打印明确信号，便于判断真走硬解还是回退。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int DefaultWindowW = 640;
    private const int DefaultWindowH = 480;
    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (HANDLE)-4。</summary>
    private static readonly IntPtr DpiAwarePerMonitorV2 = new(-4);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            // 必须在创建任何窗口之前调用：非 DPI-aware 进程下 DWM 会把后备位图按缩放比
            // （125%/150%）非整数倍拉伸到物理像素，这层拉伸发生在 D3D11 之外，
            // 会造出随画面运动游走的竖条纹摩尔，且 backbuffer 回读看不见它。
            // --no-dpi-aware 保留反向对照能力。
            if (!HasFlag(args, "--no-dpi-aware"))
            {
                bool ok = NativeMethods.SetProcessDpiAwarenessContext(DpiAwarePerMonitorV2);
                if (!ok)
                    Console.WriteLine($"[HEADFUL-DPI] SetProcessDpiAwarenessContext 失败(err={Marshal.GetLastPInvokeError()})；" +
                                      "可能已由 manifest 设定，或系统 <Win10 1703。");
            }
            else
            {
                Console.WriteLine("[HEADFUL-DPI] --no-dpi-aware：进程保持 DPI 非感知（DWM 位图拉伸对照组）。");
            }
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        bool verbose = HasFlag(args, "-v", "--verbose");
        bool doVideo = !HasFlag(args, "--no-video");
        bool doAudio = !HasFlag(args, "--no-audio");
        bool visible = HasFlag(args, "--visible");
        bool enableCategory = HasFlag(args, "--category");
        // —— WASAPI 模式对照开关 ——
        // --exclusive：强制独占模式（默认共享）。--polling：关闭事件驱动改用轮询（默认事件驱动）。
        // --audio-warmup：开启音频预热（默认关闭，见下方说明）。
        bool exclusiveMode = HasFlag(args, "--exclusive");
        bool pollingMode = HasFlag(args, "--polling");
        bool audioWarmup = HasFlag(args, "--audio-warmup");
        bool fullPlayback = HasFlag(args, "--full");
        bool repeat = HasFlag(args, "--repeat");
        // 重播验证：--full 或 --repeat 时默认启用（必须先到达 Ended 才能重播），--no-replay 可关闭。
        if (repeat) fullPlayback = true;   // --repeat 隐含 --full（重播要求先自然结束）
        bool replayTest = (fullPlayback || repeat) && !HasFlag(args, "--no-replay");
        Console.WriteLine($"[HEADFUL-WASAPI-MODE] Exclusive={exclusiveMode} EventDriven={!pollingMode} AudioWarmup={audioWarmup} Full={fullPlayback} Replay={replayTest}");
        // 诊断开关须在构建播放器前设置（相关静态字段只读取一次）。
        if (fullPlayback)
        {
            // 音频相位诊断：让 AudioPipeline 打点 ReadAsync/Decode/解码间隙。
            Environment.SetEnvironmentVariable("LINGFAN_AUDIO_DIAG", "1");
            // EOS 时序诊断：让 Video/AudioPipeline 在自然完成瞬间打印主时钟位置。
            Environment.SetEnvironmentVariable("LINGFAN_EOS_DIAG", "1");
            // A/V 同步诊断：让 VideoPipeline 在每次呈现瞬间打印 videoPTS − audioClock。
            Environment.SetEnvironmentVariable("LINGFAN_SYNC_DIAG", "1");
            // 音频播放时钟：不打开则 SetMasterClockProvider 不执行，主时钟回落到按提交帧时间戳同步。
            Environment.SetEnvironmentVariable("LINGFAN_CLOCK_AUDIO_POS", "1");
        }
        // 音画同步微调：--sync-lead=NN 把主时钟前移 N 毫秒以吸收视频呈现的恒定领先（默认 0=不补偿）。
        double syncLeadMs = ParseDouble(args, "--sync-lead", 0);
        if (syncLeadMs != 0)
            Environment.SetEnvironmentVariable("LINGFAN_SYNC_LEAD_MS", syncLeadMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        int saveFrames = (int)ParseDouble(args, "--save-frames", 0);
        string saveDir = ParseOption(args, "--save-dir")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "TestInfo", "Diagnostics", "VulkanVideoHardDecodeProbe");
        string? file = ParseOption(args, "--file");
        double seconds = ParseDouble(args, "--seconds", 12);
        // Vulkan 缩放模式：fill=拉伸全屏 / uniform=信箱(默认) / uniformtofill(=cover)=高保真全屏
        string scaleArg = ParseOption(args, "--scale")?.ToLowerInvariant() ?? "uniform";
        AspectRatioMode scaleMode = scaleArg switch
        {
            "fill" => AspectRatioMode.Fill,
            "uniform" => AspectRatioMode.Uniform,
            "uniformtofill" or "cover" => AspectRatioMode.UniformToFill,
            _ => AspectRatioMode.Uniform,
        };
        Console.WriteLine($"[HEADFUL-VULKAN-SCALE] {scaleMode} (--scale={scaleArg})");
        int windowW = (int)ParseDouble(args, "--window-w", DefaultWindowW);
        int windowH = (int)ParseDouble(args, "--window-h", DefaultWindowH);
        if (windowW <= 0 || windowH <= 0)
        {
            Console.Error.WriteLine($"窗口尺寸非法：{windowW}x{windowH}");
            return 2;
        }

        if (saveFrames > 0)
            Console.WriteLine($"[HEADFUL-SAVE] 每 {saveFrames} 帧落盘 -> {saveDir}");

        if (!doVideo && !doAudio)
        {
            Console.Error.WriteLine("--no-video 与 --no-audio 不能同时使用。");
            return 2;
        }

        if (file is null)
            file = ResolveDefaultMedia(doVideo);
        if (file is null || !File.Exists(file))
        {
            Console.Error.WriteLine($"找不到媒体文件：{file ?? "(null)"}");
            return 2;
        }

        var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        // AddVulkanVideo / AddFFmpeg / AddWasapiOutput 都是 MediaBuilder 的扩展，
        // 必须接在 builder 链上调用，不能对 ServiceCollection 直接调用。
        // 解码后端注册顺序即运行时回退优先级：VulkanVideo → FFmpeg。
        //    VulkanVideo 经 VK_KHR_video_decode_h264 硬解出 NV12 VkImage，与渲染器同设备 → 零拷贝上屏；
        //    仅 H.264，且要求 IGpuDeviceContext.VideoQueueFamilyIndex 有效（设备启用 video-decode 扩展）；
        //    不满足（非 H.264 / GPU 无 video-decode）时 Initialize 抛 NotSupportedException 由管线回落 FFmpeg。
        //    FFmpeg 作下家：解封装 + 音频解码 + H.264 软解回退（VulkanVideo 不可用时）。
        var builder = services.AddLingFanMedia().AddVulkanVideo().AddFFmpeg(options => options.FFmpegLibraryPath = AppContext.BaseDirectory);

        // —— 视频侧 ——
        CountingVideoRendererFactory? countingFactory = null;
        IGpuDeviceContext? gpuCtx = null;
        if (doVideo)
        {
            // 必须显式注册 Vulkan 工厂（装饰器包裹）。AddLingFanMedia / AddVulkanRenderer 默认不注册具体渲染器；
            // 缺失会在解析 IMediaPlayer 时抛 "No service for type 'IVideoRendererFactory'"。
            // 装饰器包裹真实 VulkanRendererFactory 以精确统计 Present 调用。
            // 关键：渲染器 Context 实现 IGpuDeviceContext，须注册为单例供 VulkanVideoDecoder 注入——
            // 两者共用同一 VkDevice + video-decode 队列族，零拷贝闭环成立。
            // （本探针手动注册，不调 AddVulkanRenderer()，以免与下方 IVideoRendererFactory 双重注册冲突。）
            var vulkanFactory = new VulkanRendererFactory(loggerFactory);
            vulkanFactory.ScaleMode = scaleMode;
            gpuCtx = vulkanFactory.Context;
            countingFactory = new CountingVideoRendererFactory(vulkanFactory, saveFrames, saveDir);
            builder.Services.AddSingleton<IVideoRendererFactory>(countingFactory);
            builder.Services.AddSingleton<IGpuDeviceContext>(gpuCtx);
        }
        else
        {
            // 纯音频用例：用 NoOp 视频渲染器隔离变量
            builder.AddHeadlessRenderer();
        }

        // —— 音频侧 ——
        if (doAudio)
            builder.AddWasapiOutput(o =>
            {
                o.EnableBackgroundCapableSession = enableCategory;
                o.ExclusiveMode = exclusiveMode;       // --exclusive 强制独占
                o.EventDrivenMode = !pollingMode;       // --polling 退回轮询模式
            });
        else
            builder.AddSilentAudioOutput();

        // —— 硬解能力诊断（先于 OpenAsync）：直接读渲染器设备的 VideoQueueFamilyIndex ——
        if (doVideo && gpuCtx is not null)
        {
            bool videoDecodeCapable = gpuCtx.VideoQueueFamilyIndex != uint.MaxValue;
            Console.WriteLine($"[HEADFUL-VULKANVIDEO] 渲染器设备 VideoQueueFamilyIndex={gpuCtx.VideoQueueFamilyIndex} " +
                              $"video-decode 能力={(videoDecodeCapable ? "有效（硬解零拷贝可期）" : "无效（将回落 FFmpeg 软件解码）")}");
            Console.WriteLine($"[HEADFUL-VULKANVIDEO] 组合根：AddVulkanVideo() 先于 AddFFmpeg() → H.264 优先走 VK_KHR_video_decode_h264 硬解");
        }

        await using var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        // 音频进度观测量：累计进入提交链的采样数（区分「真丢帧」与「时钟 lag」）
        long submittedSamples = 0;
        int sampleRate = 0;
        player.AudioDataAvailable += f =>
        {
            Interlocked.Add(ref submittedSamples, f.FrameCount);
            if (sampleRate == 0) sampleRate = f.SampleRate;
        };

        // 音频间隙诊断：对比「主时钟位置」与「已提交音频时长」。
        double maxAudioBacklogMs = 0;
        int audioGapCount = 0;
        int audioStallCount = 0;
        long prevSubmitted = 0;
        double prevPosSec = 0;

        RenderWindow? win = null;
        int presentCount = 0;
        bool videoPass = !doVideo;
        bool audioPass = !doAudio;
        // 重播 Ended→Playing 判定（外层声明，使 try 内的重播块与 try 外的 overall 共享作用域）。
        bool replayPass = !replayTest;

        try
        {
            // —— FFmpeg 预热：强制解析 FFmpegBackend 单例以在窗口出现前完成原生库初始化，
            // 使正式 OpenAsync 复用已加载的库，几乎瞬时完成，避免窗口出现后长时间空屏。
            // 预热失败一律降级为未预热，不影响播放。
            var warmSw = Stopwatch.StartNew();
            try
            {
                var ff = sp.GetRequiredService<FFmpegBackend>();
                Console.WriteLine($"[HEADFUL-FFMPEG] 预热耗时 {warmSw.Elapsed.TotalSeconds:F2}s（已拉起 FFmpeg 原生后端，正式打开将显著加快）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HEADFUL-FFMPEG] 预热失败（忽略，正式打开仍可用）: {ex.Message}");
            }

            // —— WASAPI 预热（opt-in，--audio-warmup）——
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

            if (doVideo)
            {
                // 真实可见窗口：专用 STA 线程 + 消息泵（Vulkan SwapChain 需要有效 HWND + 消息循环）。
                win = new RenderWindow(windowW, windowH, visible);
                if (win.Hwnd == IntPtr.Zero)
                {
                    Console.WriteLine("窗口创建失败（HWND=0），视频有头验证将不可靠。");
                }
                else
                {
                    double scale = win.Dpi == 0 ? 1.0 : win.Dpi / 96.0;
                    bool clientMatches = win.ClientW == windowW && win.ClientH == windowH;
                    Console.WriteLine(
                        $"[HEADFUL-WND] 请求={windowW}x{windowH} 实测客户区={win.ClientW}x{win.ClientH} " +
                        $"DPI={win.Dpi}({scale:P0}) | 客户区与请求{(clientMatches ? "一致" : "不一致")} | " +
                        $"SwapChain 将按实测客户区建立");
                    if (!clientMatches)
                        Console.WriteLine("[HEADFUL-WND] 客户区≠请求：说明存在窗口几何虚拟化，DWM 会额外拉伸一层。");
                }
            }

            Console.WriteLine($"OpenAsync: {file}");
            var sw = Stopwatch.StartNew();
            await player.OpenAsync(new FileMediaSource(file), CancellationToken.None);
            double openSec = sw.Elapsed.TotalSeconds;
            Console.WriteLine($"  OpenAsync 耗时 {openSec:F2}s  Duration={player.Duration:g}  " +
                              $"AudioTracks={player.Session?.AudioTracks.Count ?? 0}");

            if (doVideo && win is not null && win.Hwnd != IntPtr.Zero)
            {
                if (countingFactory!.Last is null)
                    Console.WriteLine("Vulkan 渲染器未创建（环境无 GPU/显示或 Vulkan 不可用），跳过视频有头验证。");
                else
                    // 用「实测客户区」而非构造入参：两者不等时按入参建 SwapChain 会让 DXGI 再叠一层拉伸。
                    countingFactory.Last.Attach(new HwndRenderTarget(win.Hwnd,
                        win.ClientW > 0 ? win.ClientW : windowW,
                        win.ClientH > 0 ? win.ClientH : windowH));
                // Vulkan 经统一 FrameChannel 订阅 Present（与 D3D11GpuPresenter 行为一致）。
                player.VideoFrameAvailable += f => countingFactory.Last?.Present(f);
            }

            await player.PlayAsync();
            Console.WriteLine("  PlayAsync 完成，开始轮询…");

            var startPos = player.Position;
            var poll = Stopwatch.StartNew();
            // 播放结束判定：--full 时播到真实结束（player.State == MediaState.Ended），
            // 否则按 --seconds（默认 12）计时。带安全上限防 Ended 未触发导致死循环。
            while (true)
            {
                await Task.Delay(500);
                if (doVideo && countingFactory?.Last is not null)
                    presentCount = countingFactory.Last.PresentCount;
                if (visible || verbose)
                    Console.WriteLine($"  t={poll.Elapsed.TotalSeconds:F1}s pos={player.Position:g} " +
                                      $"present={presentCount} state={player.State}");
                // 音频间隙检测：主时钟位置 vs 已提交音频时长
                if (doAudio && sampleRate > 0 && player.State == MediaState.Playing)
                {
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
                if (fullPlayback)
                {
                    if (player.State == MediaState.Ended) break;
                    double cap = player.Duration.TotalSeconds + 8;
                    if (poll.Elapsed.TotalSeconds > cap)
                    {
                        Console.WriteLine("  [HEADFUL] 超时未收到 Ended（兜底退出），可能播放完成检测未触发");
                        break;
                    }
                }
                else if (poll.Elapsed.TotalSeconds >= seconds)
                {
                    break;
                }
            }

            Console.WriteLine($"  [HEADFUL] 播放结束 state={player.State} pos={player.Position:g} " +
                              $"Duration={player.Duration:g} present={presentCount} dropped={player.VideoDroppedFrames}");

            var playedSec = (player.Position - startPos).TotalSeconds;

            if (doVideo)
            {
                if (countingFactory?.Last is not null)
                    presentCount = countingFactory.Last.PresentCount;
                videoPass = presentCount >= 5;
                Console.WriteLine($"[HEADFUL-VIDEO] vulkanPresentCount={presentCount}  => " +
                                  $"{(videoPass ? "PASS (硬解NV12→RGBA→SwapChain 零拷贝上屏)" : "FAIL (present<5)")}");
                // 诊断：视频丢帧数。与 present 计数对照可判断尾帧是被 Synchronizer 判定丢弃还是已呈现。
                Console.WriteLine($"[HEADFUL-VIDEO-DROP] droppedFrames={player.VideoDroppedFrames} present={presentCount}");
                // 诊断：分相计时——定位每帧开销归属（CPU 转换 vs GPU 同步 QueueWaitIdle）。
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
                bool realAudioGap = maxAudioBacklogMs > 150;
                Console.WriteLine($"[HEADFUL-AUDIO-GAP] maxBacklog={maxAudioBacklogMs:F0}ms " +
                                  $"gaps(>150ms)={audioGapCount} stalls={audioStallCount} " +
                                  $"=> {(realAudioGap ? "检测到音频间隙（真欠载）" : "未检测到间隙")}");
                if (audioStallCount > 0 && !realAudioGap)
                    Console.WriteLine($"  [HEADFUL-AUDIO-GAP] 注：stalls={audioStallCount} 均发生在 backlog=0 的连续播放中，" +
                                      $"属批量提交节奏（非欠载/断音）；真欠载以 backlog>150ms 为准。");
            }

            // —— 重播：Ended→Playing 无缝从头（--repeat/--full 启用，--no-replay 关闭）——
            if (replayTest)
            {
                Console.WriteLine();
                Console.WriteLine("[HEADFUL-REPLAY] 第一次播放已 Ended，立即二次 PlayAsync 验证重播无缝从头…");
                int presentBeforeReplay = presentCount;
                double posBeforeReplay = player.Position.TotalSeconds;
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
                replayPass = replayStateOk && replayPresentOk;
                Console.WriteLine($"  [HEADFUL-REPLAY] 二次播放结束 state={player.State} " +
                                  $"present 增量={replayPresentDelta} Duration={player.Duration:g}");
                Console.WriteLine($"  [HEADFUL-REPLAY] => {(replayPass ? "PASS" : "FAIL")} " +
                                  $"(state重启={replayStateOk}, 二次呈现≈{replayPresentDelta}{(doVideo ? "" : "[无视频跳过计数]")})");
            }

            await player.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
            if (verbose) Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            try { await player.DisposeAsync(); } catch { }
            try { win?.Dispose(); } catch { }
        }

        bool overall = videoPass && audioPass && replayPass;
        Console.WriteLine();
        if (saveFrames > 0)
            Console.WriteLine($"[HEADFUL-SAVE] 共落盘 {FrameDumper.DumpedCount} 张帧 -> {saveDir}");
        Console.WriteLine(overall ? "总体 PASS" : "总体 FAIL");
        return overall ? 0 : 1;
    }

    // ── 计数装饰器：包裹真实 IVideoRenderer，统计 Present 调用 ──

    private sealed class CountingVideoRendererFactory : IVideoRendererFactory
    {
        private readonly IVideoRendererFactory _inner;
        private readonly int _saveFrames;
        private readonly string _saveDir;
        public CountingVideoRenderer? Last { get; private set; }
        public CountingVideoRendererFactory(IVideoRendererFactory inner, int saveFrames, string saveDir)
        {
            _inner = inner;
            _saveFrames = saveFrames;
            _saveDir = saveDir;
        }
        public IVideoRenderer Create()
        {
            var wrapped = new CountingVideoRenderer(_inner.Create(), _saveFrames, _saveDir);
            Last = wrapped;
            return wrapped;
        }
    }

    private sealed class CountingVideoRenderer : IVideoRenderer
    {
        private readonly IVideoRenderer _inner;
        private readonly int _saveFrames;
        private readonly string _saveDir;
        public int PresentCount;
        public CountingVideoRenderer(IVideoRenderer inner, int saveFrames, string saveDir)
        {
            _inner = inner;
            _saveFrames = saveFrames;
            _saveDir = saveDir;
        }
        public void Attach(IRenderTarget target) => _inner.Attach(target);
        public void Detach() => _inner.Detach();
        public void Present(VideoFrame frame)
        {
            int n = Interlocked.Increment(ref PresentCount);
            // 诊断：把渲染器收到的真实帧落 PNG（在 _inner.Present 之前同步拷贝，不持有帧引用）。
            // 硬解纹理（VulkanVideoFrameResource : IGpuTextureResource）走 ReadbackToCpu；软解帧走 NV12/BGRA 转换。
            // 若落盘图干净而窗口画面异常，问题在上传/Present；若落盘图同样异常，问题在上游解码。
            if (_saveFrames > 0 && n % _saveFrames == 0)
                FrameDumper.DumpFrame(frame, n, _saveDir);
            _inner.Present(frame);
        }
        public TimeSpan PresentationLatency => TimeSpan.Zero;

        public void Clear() => _inner.Clear();
        public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);
        public void Dispose() => _inner.Dispose();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        /// <summary>诊断：转发底层 VulkanRenderer 的分相计时（CPU 转换 vs GPU 同步）。</summary>
        public string? GetInnerProfile() => _inner is IRendererProfiler p ? p.GetProfile() : null;
    }

    // ── 渲染目标：把 HWND 包装成 IRenderTarget（Window 类型） ──

    private sealed class HwndRenderTarget : IRenderTarget
    {
        private readonly IntPtr _hwnd;
        private readonly int _w, _h;
        public HwndRenderTarget(IntPtr hwnd, int w, int h) { _hwnd = hwnd; _w = w; _h = h; }
        public RenderTargetType Type => RenderTargetType.Window;
        public RenderHandleType HandleType => RenderHandleType.Pointer;
        public object NativeHandle => _hwnd;
        public int Width => _w;
        public int Height => _h;
        public float Scale => 1f;
    }

    // ── 真实窗口（专用 STA 线程 + 消息泵）；[LibraryImport] 重写（AOT 合规）──

    private sealed class RenderWindow : IDisposable
    {
        public IntPtr Hwnd { get; private set; } = IntPtr.Zero;
        /// <summary>窗口真实客户区宽（GetClientRect 实测，不是构造入参）。SwapChain 必须按它建，否则 DXGI 会再拉伸一层。</summary>
        public int ClientW { get; private set; }
        /// <summary>窗口真实客户区高（GetClientRect 实测）。</summary>
        public int ClientH { get; private set; }
        /// <summary>窗口所在监视器有效 DPI（96=100%）。≠96 且进程非 DPI-aware ⇒ DWM 会做位图拉伸。</summary>
        public uint Dpi { get; private set; }
        private readonly ManualResetEventSlim _ready = new();
        private readonly Thread _thread;
        private volatile bool _alive = true;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_VISIBLE = 0x10000000;
        private readonly bool _visible;
        private readonly int _w, _h;

        // —— 自注册窗口类（黑底，消除解码期间的启动白屏）——
        private const string WindowClassName = "LingFanVulkanVideoProbeWnd";
        private static readonly object _classLock = new();
        private static bool _classRegistered;
        // 必须保持根引用：类过程委托一旦被 GC，RegisterClassExW 注册的 lpfnWndProc 即悬空 ⇒ 野调用崩溃。
        private static WndProcDelegate? _wndProcKeepAlive;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public RenderWindow(int w, int h, bool visible)
        {
            _w = w; _h = h; _visible = visible;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA); // Vulkan SwapChain 依赖 STA 线程 + 消息泵
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("窗口线程未就绪（HWND 创建超时）。");
        }

        private void Run()
        {
            RegisterWindowClass();
            Hwnd = NativeMethods.CreateWindowExW(0, WindowClassName, "", _visible ? (WS_POPUP | WS_VISIBLE) : WS_POPUP,
                0, 0, _w, _h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Hwnd == IntPtr.Zero)
            {
                _ready.Set();
                return;
            }
            if (_visible) NativeMethods.ShowWindow(Hwnd, 1);
            if (NativeMethods.GetClientRect(Hwnd, out var rc))
            {
                ClientW = rc.Right - rc.Left;
                ClientH = rc.Bottom - rc.Top;
            }
            try { Dpi = NativeMethods.GetDpiForWindow(Hwnd); } catch { Dpi = 0; }
            _ready.Set();
            while (_alive)
            {
                while (NativeMethods.PeekMessageW(out var msg, Hwnd, 0, 0, 1))
                {
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessageW(ref msg);
                }
                Thread.Sleep(1);
            }
            NativeMethods.DestroyWindow(Hwnd);
            Hwnd = IntPtr.Zero;
        }

        public void Dispose()
        {
            _alive = false;
            try { _thread.Join(500); } catch { }
            _ready.Dispose();
        }

        /// <summary>进程级注册一次黑底窗口类（替代 "Static" 系统类的白色背景，消除启动白屏）。幂等。</summary>
        private static void RegisterWindowClass()
        {
            lock (_classLock)
            {
                if (_classRegistered) return;
                IntPtr namePtr = IntPtr.Zero;
                try
                {
                    namePtr = Marshal.StringToHGlobalUni(WindowClassName);
                    _wndProcKeepAlive = StaticWndProc; // 保活，防止类过程被 GC
                    var wc = new NativeMethods.WNDCLASSEXW
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                        style = 0,
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                        hInstance = IntPtr.Zero,
                        hbrBackground = NativeMethods.GetStockObject(4), // BLACK_BRUSH=4
                        lpszMenuName = IntPtr.Zero,
                        lpszClassName = namePtr,
                        hCursor = NativeMethods.LoadCursorW(IntPtr.Zero, (IntPtr)32512), // IDC_ARROW
                    };
                    ushort atom = NativeMethods.RegisterClassExW(ref wc);
                    if (atom == 0)
                        Console.WriteLine($"[HEADFUL-WND] 窗口类注册失败(err={Marshal.GetLastPInvokeError()})，回退默认类");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HEADFUL-WND] 窗口类注册异常（忽略）: {ex.Message}");
                }
                finally
                {
                    if (namePtr != IntPtr.Zero) Marshal.FreeHGlobal(namePtr);
                    _classRegistered = true;
                }
            }
        }

        private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            => NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ── 参数 / 资源解析辅助 ──

    private static bool HasFlag(string[] args, params string[] flags)
    {
        foreach (var a in args)
            foreach (var f in flags)
                if (string.Equals(a, f, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static double ParseDouble(string[] args, string name, double fallback)
    {
        var v = ParseOption(args, name);
        return double.TryParse(v, out var d) ? d : fallback;
    }

    /// <summary>优先用输出目录下随工程复制的 Resources；回退向上找仓库根。</summary>
    private static string? ResolveDefaultMedia(bool wantVideo)
    {
        string rel = wantVideo ? Path.Combine("Resources", "Video", "m1.mp4")
                               : Path.Combine("Resources", "Audio", "crickets_night01.mp3");
        string local = Path.Combine(AppContext.BaseDirectory, rel);
        if (File.Exists(local)) return local;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string candidate = Path.Combine(dir.FullName, rel);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

// 帧落盘诊断：把渲染器收到的真实帧（CPU 软解 / GPU 硬解回读）转 RGBA 后写极简 PNG。
// 自包含零依赖，不引用任何渲染器/后端模块，保持探针的独立取证地位。
internal static class FrameDumper
{
    internal static int DumpedCount;

    internal static void DumpFrame(VideoFrame frame, int presentIndex, string dir)
    {
        // 诊断上限：硬解零拷贝诊断只关注前 3 帧（关键帧 + 紧随其后的 P 帧），避免落大量 PNG。
        if (DumpedCount >= 3) return;
        try
        {
            byte[]? rgba = null; int w = 0, h = 0;
            if (frame.Resource is SoftwareFrameResource sfr)
            {
                w = sfr.Width; h = sfr.Height;
                var span = sfr.Data.Span;
                switch (sfr.Format)
                {
                    case PixelFormat.NV12:
                        rgba = new byte[w * h * 4];
                        SemiPlanarToRgba(span, w, h, rgba, false);
                        break;
                    case PixelFormat.NV21:
                        rgba = new byte[w * h * 4];
                        SemiPlanarToRgba(span, w, h, rgba, true);
                        break;
                    case PixelFormat.BGRA32:
                        rgba = new byte[w * h * 4];
                        ReorderBgraToRgba(span, w * h, rgba);
                        break;
                    case PixelFormat.RGBA32:
                        rgba = new byte[w * h * 4];
                        span.Slice(0, w * h * 4).CopyTo(rgba);
                        break;
                    default:
                        Console.WriteLine($"  [HEADFUL-SAVE] 帧 {presentIndex} 格式 {sfr.Format} 不可落盘(CPU)，跳过");
                        return;
                }
            }
            else if (frame.Resource is IGpuTextureResource gpu)
            {
                using var rb = gpu.ReadbackToCpu();
                w = rb.Width; h = rb.Height;
                var span = rb.Data.Span;
                rgba = new byte[w * h * 4];
                ReorderBgraToRgba(span, w * h, rgba);
            }
            else
            {
                Console.WriteLine($"  [HEADFUL-SAVE] 帧 {presentIndex} 资源类型未知，跳过");
                return;
            }

            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"frame_{presentIndex:D5}.png");
            EncodeRgbaPng(path, w, h, rgba);
            PrintPixelStats(presentIndex, rgba, w, h);
            Interlocked.Increment(ref DumpedCount);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [HEADFUL-SAVE] 帧 {presentIndex} 落盘失败: {ex.Message}");
        }
    }

    private static void SemiPlanarToRgba(ReadOnlySpan<byte> nv, int w, int h, byte[] rgba, bool nv21)
    {
        int ySize = w * h;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int yi = y * w + x;
                int ui = ySize + (y / 2) * w + (x / 2) * 2;
                int Y = nv[yi], U = nv[ui], V = nv[ui + 1];
                if (nv21) (U, V) = (V, U);
                int c = Y - 16, d = U - 128, e = V - 128;
                int o = yi * 4;
                rgba[o] = (byte)Clamp((298 * c + 409 * e + 128) >> 8);
                rgba[o + 1] = (byte)Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                rgba[o + 2] = (byte)Clamp((298 * c + 516 * d + 128) >> 8);
                rgba[o + 3] = 255;
            }
        }
    }

    private static void ReorderBgraToRgba(ReadOnlySpan<byte> bgra, int pixels, byte[] rgba)
    {
        for (int i = 0; i < pixels; i++)
        {
            int si = i * 4, di = i * 4;
            rgba[di] = bgra[si + 2];
            rgba[di + 1] = bgra[si + 1];
            rgba[di + 2] = bgra[si];
            rgba[di + 3] = bgra[si + 3];
        }
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// <summary>
    /// 量化落盘帧像素统计（绿屏数据驱动诊断）：直接回答「解码输出是否为空」——
    /// 若 maxR/G/B≈0 且非零像素 ≈0%，说明 DPB 内容为空（解码静默失败）；
    /// 若非零像素占比高，说明解码有真实数据，绿屏在显示/采样路径。
    /// </summary>
    private static void PrintPixelStats(int presentIndex, byte[] rgba, int w, int h)
    {
        ulong n = (ulong)((long)w * h);
        if (n == 0) return;
        long nonZero = 0;
        ulong sumR = 0, sumG = 0, sumB = 0;
        int maxR = 0, maxG = 0, maxB = 0;
        for (ulong i = 0; i < n; i++)
        {
            int r = rgba[(int)(i * 4)], g = rgba[(int)(i * 4 + 1)], b = rgba[(int)(i * 4 + 2)];
            if (r != 0 || g != 0 || b != 0) nonZero++;
            sumR += (ulong)r; sumG += (ulong)g; sumB += (ulong)b;
            if (r > maxR) maxR = r;
            if (g > maxG) maxG = g;
            if (b > maxB) maxB = b;
        }
        double pct = nonZero * 100.0 / (double)n;
        ulong meanR = sumR / n, meanG = sumG / n, meanB = sumB / n;
        Console.WriteLine($"  [HEADFUL-SAVE] 帧 {presentIndex} 统计: 尺寸={w}x{h} 非零像素={pct:F1}% " +
                          $"均值(R,G,B)=({meanR},{meanG},{meanB}) max(R,G,B)=({maxR},{maxG},{maxB})");
        if (pct < 1.0)
            Console.WriteLine($"  [HEADFUL-SAVE]   → DPB 内容疑似为空（解码静默失败 / 起始码 / SPS / 参考帧配置问题）");
        else if (meanR == 0 && meanG == 135 && meanB == 0)
            // 均值(0,135,0) = YuvToRgb(0,0,0) = NV12 全零 → 解码器一个像素都没写进去（绿屏真因，非显示/采样路径问题）
            Console.WriteLine($"  [HEADFUL-SAVE]   → DPB 仍为全零 NV12（均值(0,135,0)=YUV(0,0,0)），解码未写入真实像素");
        else
            Console.WriteLine($"  [HEADFUL-SAVE]   → DPB 含真实像素（解码有输出），绿屏在显示/采样/布局路径");
    }

    // 极简 PNG 编码器（RGBA / color type 6，ZLibStream 走 BCL，无第三方依赖）
    private static readonly uint[] CrcTable = BuildCrc();

    internal static void EncodeRgbaPng(string path, int w, int h, ReadOnlySpan<byte> rgba)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 0x08; ihdr[9] = 0x06;
        WriteChunk(fs, "IHDR", ihdr);

        using var ms = new MemoryStream();
        using (var zs = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[w * 4 + 1];
            row[0] = 0;
            for (int y = 0; y < h; y++)
            {
                rgba.Slice(y * w * 4, w * 4).CopyTo(row.AsSpan(1));
                zs.Write(row);
            }
        }
        WriteChunk(fs, "IDAT", ms.ToArray());
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream fs, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        fs.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        fs.Write(typeBytes);
        fs.Write(data);
        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, Crc(typeBytes, data));
        fs.Write(crcBuf);
    }

    private static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte x in a) crc = (crc >> 8) ^ CrcTable[(crc ^ x) & 0xFF];
        foreach (byte x in b) crc = (crc >> 8) ^ CrcTable[(crc ^ x) & 0xFF];
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
}

// [LibraryImport] 源生成要求载体类型为顶级 partial 类型：嵌套 partial 类会被 SYSLIB1050 拒绝，
// 且错误信息会误报为外层 Program 未标记 partial，故 user32 P/Invoke 放在本顶级 partial 类。
// 全部使用 EntryPoint="XxxW" + Utf16 封送以满足 AOT 要求。
internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    public static partial uint GetDpiForWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("gdi32.dll", EntryPoint = "GetStockObject")]
    public static partial IntPtr GetStockObject(int i);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [LibraryImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(IntPtr value);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public int message;
        public IntPtr wParam;
        public IntPtr lParam;
        public int time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x; public int y; }
}

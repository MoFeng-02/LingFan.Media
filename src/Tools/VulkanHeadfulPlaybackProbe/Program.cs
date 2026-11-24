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
using ProbeShared;

namespace VulkanHeadfulPlaybackProbe;

/// <summary>
/// 有头（Headful）全链路验证程序：真实 Vulkan SwapChain 上屏（GPU Present）与真实 WASAPI 出声，
/// 二者共用同一个 MediaPlayer 与 MF 解码链。
/// </summary>
/// <remarks>
/// <para>命令行开关用于隔离变量：</para>
/// <list type="bullet">
///   <item><c>--backend</c>：后端选择 <c>mf</c>（默认）/ <c>ffmpeg</c> / <c>vlc</c>。默认（无 <c>--hw</c>）三者强制软件解码以产出 CPU 帧喂给 Vulkan 上屏；<c>--hw</c> 时按后端能力开启硬解零拷贝（仅 ffmpeg 经 D3D11VA→NV12→RGBA 共享句柄由 VulkanGpuFrameProducer 导入零拷贝，VLC/MF 仍回拷 CPU 仅作计数参考）。</item>
///   <item><c>--hw</c>：硬解零拷贝验收开关。开启后按后端打开硬解，并打印 <c>[HEADFUL-ZEROCOPY]</c> GPU 纹理帧 / CPU 内存帧计数；<c>--backend ffmpeg</c> 时 GPU 纹理帧=0 判 FAIL。</item>
///   <item><c>--visible</c>：窗口真正可见，便于肉眼确认上屏。</item>
///   <item><c>--no-video</c>：只验 WASAPI 出声。</item>
///   <item><c>--no-audio</c>：只验 Vulkan 上屏。</item>
    ///   <item><c>--category</c>：启用 IAudioClient2 会话分类做对照（默认 Movie）。</item>
    ///   <item><c>--scale</c>：Vulkan 软帧缩放模式（fill=拉伸全屏 / uniform=信箱默认 / uniformtofill=高保真全屏）。</item>
    /// </list>
/// <para>窗口代码使用 <c>[LibraryImport]</c> 以满足 AOT 要求。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class Program
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
        bool softwareDecode = HasFlag(args, "--software-decode");
        bool hwMode = HasFlag(args, "--hw");
        // —— 后端选择开关：让同一套 Vulkan 渲染路径覆盖 MF / FFmpeg / VLC 三条后端 ——
        // 默认软解（three隔离变量）；--hw 时按后端能力开启硬解零拷贝：ffmpeg 经 D3D11VA→NV12→RGBA 共享句柄
        // 由 VulkanGpuFrameProducer 导入零拷贝上屏；VLC 恒回拷 CPU / MF 半 DXVA 不走零拷贝（仅作计数参考）。
        Console.WriteLine($"[HEADFUL-HW] hwMode={hwMode}");
        string backendArg = ParseOption(args, "--backend")?.ToLowerInvariant() ?? "mf";
        if (backendArg is not ("mf" or "ffmpeg" or "vlc"))
        {
            Console.Error.WriteLine($"--backend 仅支持 mf|ffmpeg|vlc，收到：{backendArg}");
            return 2;
        }
        Console.WriteLine($"[HEADFUL-BACKEND] {backendArg}");
        // VLC 原生库定位：必须在解析 VLC 后端单例之前把 libvlc 目录前置 PATH，
        // 使原生加载器（LoadLibrary 搜 PATH）稳定找到 libvlc.dll。
        if (backendArg == "vlc")
        {
            string? vlcDir = LocateLibVlc();
            if (vlcDir is null)
            {
                Console.Error.WriteLine("[HEADFUL-VLC] 未找到原生 libvlc.dll（原生包未分发且系统未装 VLC），无法验收 VLC 后端。");
                return 3;
            }
            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (existingPath.IndexOf(vlcDir, StringComparison.OrdinalIgnoreCase) < 0)
                Environment.SetEnvironmentVariable("PATH", vlcDir + Path.PathSeparator + existingPath);
            Console.WriteLine($"[HEADFUL-VLC] libvlc 目录(已前置 PATH): {vlcDir}");
        }
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
            // 完成由管线驱动而非时钟驱动；若 masterTime 明显小于 Duration，说明包队列/帧队列提前耗尽。
            Environment.SetEnvironmentVariable("LINGFAN_EOS_DIAG", "1");
            // A/V 同步诊断：让 VideoPipeline 在每次呈现瞬间打印 videoPTS − audioClock，
            // 用于定量观察音画偏差量级及其随播放时间的变化。
            Environment.SetEnvironmentVariable("LINGFAN_SYNC_DIAG", "1");
            // 音频播放时钟：不打开则 SetMasterClockProvider 不执行，主时钟回落到按提交帧时间戳同步，
            // 播放位置校准不生效。该开关默认关（ClockTuning.UseAudioPlaybackClock=false），建议常开。
            Environment.SetEnvironmentVariable("LINGFAN_CLOCK_AUDIO_POS", "1");
        }
        // 音画同步微调：--sync-lead=NN 把主时钟前移 N 毫秒以吸收视频呈现的恒定领先（默认 0=不补偿）。
        // 注入 LINGFAN_SYNC_LEAD_MS 供 WasapiRenderLoop 每次 Start 读取，须在构建播放器前设置。
        double syncLeadMs = ParseDouble(args, "--sync-lead", 0);
        if (syncLeadMs != 0)
            Environment.SetEnvironmentVariable("LINGFAN_SYNC_LEAD_MS", syncLeadMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        int saveFrames = (int)ParseDouble(args, "--save-frames", 0);
        string saveDir = ParseOption(args, "--save-dir")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "TestInfo", "Diagnostics", "HeadfulPlaybackProbe");
        string? file = ParseOption(args, "--file");
        double seconds = ParseDouble(args, "--seconds", 12);
        // Vulkan 软帧缩放模式：fill=拉伸全屏 / uniform=信箱(默认) / uniformtofill(=cover)=高保真全屏
        string scaleArg = ParseOption(args, "--scale")?.ToLowerInvariant() ?? "uniform";
        AspectRatioMode scaleMode = scaleArg switch
        {
            "fill" => AspectRatioMode.Fill,
            "uniform" => AspectRatioMode.Uniform,
            "uniformtofill" or "cover" => AspectRatioMode.UniformToFill,
            _ => AspectRatioMode.Uniform,
        };
        Console.WriteLine($"[HEADFUL-VULKAN-SCALE] {scaleMode} (--scale={scaleArg})");
        // 缩小混叠对照实验：平面纹理已开 MipLevels=0 + GenerateMips（mipmap 三线性抑制摩尔纹）；
        // 双线性抽头在多倍缩小下会高频欠采样，表现为随运动游走的摩尔纹竖条。
        // 用 --window-w / --window-h 指定与源同尺寸可做 1:1 对照。
        int windowW = (int)ParseDouble(args, "--window-w", DefaultWindowW);
        int windowH = (int)ParseDouble(args, "--window-h", DefaultWindowH);
        if (windowW <= 0 || windowH <= 0)
        {
            Console.Error.WriteLine($"窗口尺寸非法：{windowW}x{windowH}");
            return 2;
        }

        if (saveFrames > 0)
            Console.WriteLine($"[HEADFUL-SAVE] 每 {saveFrames} 帧落盘 -> {saveDir}");
        if (softwareDecode)
            Console.WriteLine("[HEADFUL-SAVE] 已强制软件解码（帧为 CPU NV12，便于对照 VLC）");

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

        var (sp, player, countingFactory) = BuildPlayer(
            backendArg, hwMode, verbose, doVideo, doAudio,
            enableCategory, exclusiveMode, pollingMode, saveFrames, saveDir, scaleMode);
        await using var _sp = sp;

        // 音频进度观测量：累计进入提交链的采样数（区分「真丢帧」与「时钟 lag」）
        long submittedSamples = 0;
        int sampleRate = 0;
        player.AudioDataAvailable += f =>
        {
            Interlocked.Add(ref submittedSamples, f.FrameCount);
            if (sampleRate == 0) sampleRate = f.SampleRate;
        };

        // 音频间隙诊断：对比「主时钟位置」与「已提交音频时长」。
        //   backlog = pos - submittedSec（秒）：大于 0 表示音频落后主时钟，即提交停滞/欠载导致静音间隙；
        //   stall = 主时钟前进超过 100ms 但 submittedSamples 未变，即音频管线停止喂数据。
        double maxAudioBacklogMs = 0;
        int audioGapCount = 0;
        int audioStallCount = 0;
        long prevSubmitted = 0;
        double prevPosSec = 0;

        RenderWindow? win = null;
        int presentCount = 0;
        long gpuServed = 0, cpuServed = 0;
        bool videoPass = !doVideo;
        bool audioPass = !doAudio;
        // 重播 Ended→Playing 判定（外层声明，使 try 内的重播块与 try 外的 overall 共享作用域）。
        bool replayPass = !replayTest;

        try
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

            if (doVideo)
            {
                // 真实可见窗口：专用 STA 线程 + 消息泵（D3D11 SwapChain 需要有效 HWND + 消息循环）。
                // 加 --visible 可肉眼确认上屏。
                win = new RenderWindow(windowW, windowH, visible, "LingFanProbeWnd", 0);
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
                // 不订阅则 PresentCount 恒为 0，验证失效。
                player.VideoFrameAvailable += f => countingFactory.Last?.Present(f);
            }
            // 零拷贝计数：出餐端按资源类型区分 GPU 纹理帧 / CPU 内存帧（全链路判定依据）。
            player.VideoFrameAvailable += f =>
            {
                if (f.Resource is IGpuTextureResource) Interlocked.Increment(ref gpuServed);
                else Interlocked.Increment(ref cpuServed);
            };

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
                if (doAudio)
                    DetectAudioGap(player, poll, verbose, submittedSamples, sampleRate,
                        ref maxAudioBacklogMs, ref audioGapCount, ref audioStallCount,
                        ref prevSubmitted, ref prevPosSec);
                if (fullPlayback)
                {
                    // --full：播到播放器自然发出的 Ended 为止。
                    // 注意：pos 来自音频设备时钟，流末之后仍会推进并越过 Duration；若以 pos 提前退出，
                    // 会抢在 A/V 双管线 drain 完成之前退出而看不到 Ended。故不以 pos 作结束信号，
                    // 仅用 Duration 之后 8s 作兜底，防播放完成检测失效时死等。
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

            (videoPass, audioPass) = EvaluateResults(
                player, countingFactory, doVideo, doAudio, hwMode, backendArg,
                playedSec, seconds, gpuServed, cpuServed,
                submittedSamples, sampleRate, maxAudioBacklogMs,
                audioGapCount, audioStallCount, ref presentCount);

            if (replayTest)
                replayPass = await RunReplayAsync(player, countingFactory, doVideo, visible, verbose, presentCount);

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

        bool overall = videoPass && audioPass && replayPass && (!hwMode || backendArg != "ffmpeg" || gpuServed > 0);
        Console.WriteLine();
        if (saveFrames > 0)
            Console.WriteLine($"[HEADFUL-SAVE] 共落盘 {FrameDumper.DumpedCount} 张帧 -> {saveDir}");
        Console.WriteLine(overall ? "总体 PASS" : "总体 FAIL");
        return overall ? 0 : 1;
    }


}

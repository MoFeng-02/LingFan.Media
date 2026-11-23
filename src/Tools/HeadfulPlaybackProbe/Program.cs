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

/// <summary>
/// 有头（Headful）全链路验证程序：真实 SwapChain 上屏（GPU Present）与真实 WASAPI 出声，
/// 后端与渲染器均可经命令行独立指定，共用同一个 MediaPlayer 解码链。
/// </summary>
/// <remarks>
/// <para>命令行开关用于隔离变量：</para>
/// <list type="bullet">
///   <item><c>--backend</c>：后端选择 <c>mf</c> / <c>ffmpeg</c> / <c>vlc</c>；<b>缺省 = 全部注册</b>
///        （mf+ffmpeg+vlc 链式，按序自动回退选首个能打开媒体源的后端）。默认强制软件解码以产出 CPU 帧喂给渲染器上屏。</item>
///   <item><c>--renderer</c>：渲染器选择 <c>d3d11</c>（默认）/ <c>vulkan</c> / <c>opengl</c>；
///        仅当 <c>--backend</c> 也未指定时二者才各自取缺省（后端全部注册走回退链，渲染器取 d3d11）。</item>
///   <item><c>--hw</c>：开启硬解零拷贝验收。ffmpeg 走 D3D11VA（D3D11 直连 / Vulkan·OpenGL 经 D3D11 共享句柄导入）；
///        mf 走半 DXVA（非真零拷贝）；vlc 硬解回拷 CPU（非零拷贝）。</item>
///   <item><c>--visible</c>：窗口真正可见，便于肉眼确认上屏。</item>
///   <item><c>--no-video</c>：只验 WASAPI 出声。</item>
///   <item><c>--no-audio</c>：只验上屏。</item>
///   <item><c>--category</c>：启用 IAudioClient2 会话分类做对照（默认 Movie）。</item>
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
        // --probe-gpus：枚举全部 DXGI 适配器，逐 GPU 查 VideoProcessor 的 NV12/YUY2/BGRA 支持
        //（诊断 AMD 核显视频配置不支持 NV12 的原因；N 卡独显对照）。
        if (HasFlag(args, "--probe-gpus"))
        {
            ProbeGpuVideoProcessorSupport();
            return 0;
        }

        bool verbose = HasFlag(args, "-v", "--verbose");
        bool doVideo = !HasFlag(args, "--no-video");
        bool doAudio = !HasFlag(args, "--no-audio");
        bool visible = HasFlag(args, "--visible");
        bool enableCategory = HasFlag(args, "--category");
        bool softwareDecode = HasFlag(args, "--software-decode");
        // --hw：开启硬解零拷贝验收。默认（不带 --hw）恒强制软件解码以隔离上屏路径；
        // 带 --hw 时 ffmpeg 走 D3D11VA 零拷贝（D3D11 直连 / OpenGL·Vulkan 经 D3D11 共享句柄导入），
        // mf 走半 DXVA（非真零拷贝），vlc 恒回拷 CPU（设计上不走零拷贝）。
        bool hwMode = HasFlag(args, "--hw");
        // —— 后端选择开关 ——
        // --backend mf|ffmpeg|vlc：指定 → 只注册该后端；未指定 → 注册全部后端（mf+ffmpeg+vlc 链式，
        // BackendFallbackMediaPlayerFactory 按序自动回退，选首个能打开媒体源的后端）。
        string? backendArg = ParseOption(args, "--backend")?.ToLowerInvariant();
        if (backendArg is not (null or "mf" or "ffmpeg" or "vlc"))
        {
            Console.Error.WriteLine($"--backend 仅支持 mf|ffmpeg|vlc（缺省=全部注册自动回退），收到：{backendArg}");
            return 2;
        }
        // —— 渲染器选择开关 ——
        // --renderer d3d11|vulkan|opengl：指定 → 只注册该渲染器；未指定 → 默认 d3d11（Windows 主渲染器）。
        // 注：IVideoRendererFactory 是 DI 单值契约（无回退链），「全部注册」只对后端成立；渲染器须显式指定或取默认。
        string rendererArg = ParseOption(args, "--renderer")?.ToLowerInvariant() ?? "d3d11";
        if (rendererArg is not ("d3d11" or "vulkan" or "opengl"))
        {
            Console.Error.WriteLine($"--renderer 仅支持 d3d11|vulkan|opengl（缺省=d3d11），收到：{rendererArg}");
            return 2;
        }
        Console.WriteLine($"[HEADFUL-BACKEND] {backendArg ?? "(全部: mf+ffmpeg+vlc 自动回退)"}");
        Console.WriteLine($"[HEADFUL-RENDERER] {rendererArg}");
        Console.WriteLine($"[HEADFUL-HW] hwMode={hwMode}");
        // VLC 原生库定位：必须在解析 VLC 后端单例之前把 libvlc 目录前置 PATH，
        // 使原生加载器（LoadLibrary 搜 PATH）稳定找到 libvlc.dll。
        // 显式 --backend vlc 时必须找到；全部注册模式找不到仅告警（回退链会跳过 VLC）。
        if (backendArg is null or "vlc")
        {
            string? vlcDir = LocateLibVlc();
            if (vlcDir is null)
            {
                if (backendArg == "vlc")
                {
                    Console.Error.WriteLine("[HEADFUL-VLC] 未找到原生 libvlc.dll（原生包未分发且系统未装 VLC），无法验收 VLC 后端。");
                    return 3;
                }
                Console.WriteLine("[HEADFUL-VLC] 未找到原生 libvlc.dll；全部注册模式下 VLC 后端将被回退链跳过。");
            }
            else
            {
                var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (existingPath.IndexOf(vlcDir, StringComparison.OrdinalIgnoreCase) < 0)
                    Environment.SetEnvironmentVariable("PATH", vlcDir + Path.PathSeparator + existingPath);
                Console.WriteLine($"[HEADFUL-VLC] libvlc 目录(已前置 PATH): {vlcDir}");
            }
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
            Console.WriteLine("[HEADFUL-SAVE] --software-decode 已忽略（硬解零拷贝验收请用 --hw；默认已强制软件解码隔离上屏路径）");

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
            backendArg!, rendererArg, hwMode, verbose, doVideo, doAudio,
            enableCategory, exclusiveMode, pollingMode, saveFrames, saveDir);
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
        long gpuServed = 0, cpuServed = 0;   // 零拷贝验收：出餐端 IGpuTextureResource 计数
        bool videoPass = !doVideo;
        bool audioPass = !doAudio;
        // 重播 Ended→Playing 判定（外层声明，使 try 内的重播块与 try 外的 overall 共享作用域）。
        bool replayPass = !replayTest;

        try
        {
                await WarmupAsync(sp, backendArg, audioWarmup, file);

            if (doVideo)
            {
                // 真实可见窗口：专用 STA 线程 + 消息泵（D3D11 SwapChain 需要有效 HWND + 消息循环）。
                // 加 --visible 可肉眼确认上屏。
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
                    Console.WriteLine("D3D11 渲染器未创建（环境无 GPU/显示），跳过视频有头验证。");
                else
                    // 用「实测客户区」而非构造入参：两者不等时按入参建 SwapChain 会让 DXGI 再叠一层拉伸。
                    countingFactory.Last.Attach(new HwndRenderTarget(win.Hwnd,
                        win.ClientW > 0 ? win.ClientW : windowW,
                        win.ClientH > 0 ? win.ClientH : windowH));
                // D3D11 经统一 FrameChannel 订阅 Present（与 D3D11GpuPresenter 行为一致）。
                // 不订阅则 PresentCount 恒为 0，验证失效。
                player.VideoFrameAvailable += f => countingFactory.Last?.Present(f);
                player.VideoFrameAvailable += f =>
                {
                    if (f.Resource is IGpuTextureResource) Interlocked.Increment(ref gpuServed);
                    else Interlocked.Increment(ref cpuServed);
                };
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

            if (doVideo)
            {
                if (countingFactory?.Last is not null)
                    presentCount = countingFactory.Last.PresentCount;
                videoPass = presentCount >= 5;
                Console.WriteLine($"[HEADFUL-VIDEO] d3d11PresentCount={presentCount}  => " +
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

            // —— 重播：Ended→Playing 无缝从头（--repeat/--full 启用，--no-replay 关闭）——
            // 第一次自然结束后立即二次 PlayAsync，验证：
            //   (a) 状态机 Ended→Playing 的合法转换；
            //   (b) 时钟归零、位置回绕到 0（PlayAsync 在 Ended 态走 SeekAsync(0) + 时钟重置分支）；
            //   (c) 双管线从排干态重启，present 从 0 重新开始累计。
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
                // 无视频用例跳过呈现计数判定
                bool replayPresentOk = !doVideo || replayPresentDelta >= 900;
                replayPass = replayStateOk && replayPresentOk;
                Console.WriteLine($"  [HEADFUL-REPLAY] 二次播放结束 state={player.State} " +
                                  $"present 增量={replayPresentDelta} Duration={player.Duration:g}");
                Console.WriteLine($"  [HEADFUL-REPLAY] => {(replayPass ? "PASS" : "FAIL")} " +
                                  $"(state重启={replayStateOk}, 二次呈现≈{replayPresentDelta}{(doVideo ? "" : "[无视频跳过计数]")})");
            }

            // 退出慢定位：Console 直写（不经日志队列，进程快速退出也不丢行）。
            var exitStopwatch = System.Diagnostics.Stopwatch.StartNew();
            await player.StopAsync(CancellationToken.None);
            Console.WriteLine($"[HEADFUL-EXIT] StopAsync={exitStopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
            if (verbose) Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            var disposeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            try { await player.DisposeAsync(); } catch { }
            Console.WriteLine($"[HEADFUL-EXIT] DisposeAsync={disposeStopwatch.ElapsedMilliseconds}ms（逐步明细见 [DISPOSE] 行）");
            var winStopwatch = System.Diagnostics.Stopwatch.StartNew();
            try { win?.Dispose(); } catch { }
            Console.WriteLine($"[HEADFUL-EXIT] 窗口销毁={winStopwatch.ElapsedMilliseconds}ms");
        }

        bool overall = videoPass && audioPass && replayPass && (!hwMode || backendArg != "ffmpeg" || gpuServed > 0);
        Console.WriteLine();
        if (saveFrames > 0)
            Console.WriteLine($"[HEADFUL-SAVE] 共落盘 {FrameDumper.DumpedCount} 张帧 -> {saveDir}");
        Console.WriteLine(overall ? "总体 PASS" : "总体 FAIL");
        return overall ? 0 : 1;
    }

}

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.VLCNative;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VlcHeadfulPlaybackProbe;

/// <summary>
/// VLC 后端的有头（Headful）全链路验证程序：真实 D3D11 SwapChain 上屏（GPU Present）与真实 WASAPI 出声，
/// 二者共用同一个 MediaPlayer 与 VLC 解码链（经 SetVideoCallbacks 内存捕获 BGRA32 帧）。
/// </summary>
/// <remarks>
/// <para>VLC 引擎内部一体化完成解封装与解码，通过 <c>SetVideoCallbacks</c> 把已解码的 BGRA32 帧推给管线。
/// 因此 VLC 路径始终交付 CPU 内存帧，不存在 GPU 零拷贝；上屏由 D3D11Renderer 经 CopyResource 快路径
/// 上传纹理完成。</para>
/// <para><c>--vout=dummy</c>（<c>VLCOptions.Headless=true</c>）禁止 VLC 自建原生窗口，视频帧经回调全部交给
/// 本管线，由 D3D11Renderer 的 Present Sink 上屏。区别于 <c>--no-video</c>（会停止解码），Headless 仅屏蔽
/// VLC 自有绘图。</para>
/// <para>两套有头测试场景：</para>
/// <list type="bullet">
///   <item>默认（软件解码有头）：不加 <c>--hw</c>。VLC 软件解码得到 BGRA32 软件帧，经 D3D11 上传上屏；
///        真实 WASAPI 出声，开启 <c>LINGFAN_CLOCK_AUDIO_POS=1</c> 后主时钟由音频设备硬件游标驱动。</item>
///   <item><c>--hw</c>（VLC 内部硬解有头）：VLC 内部走 D3D11VA 硬解，但回调仍交付 CPU BGRA32；
///        上屏与时钟口径同上。</item>
/// </list>
/// <para>隔离变量开关：<c>--no-video</c>/<c>--no-audio</c>/<c>--visible</c>/
/// <c>--exclusive</c>/<c>--polling</c>/<c>--audio-warmup</c>/<c>--category</c>/<c>--full</c>/<c>--no-replay</c>。</para>
/// <para>窗口与 P/Invoke 使用 <c>[LibraryImport]</c> 以满足 AOT 要求，帧落盘实现零第三方依赖。</para>
/// <para>原生 libvlc 随输出目录自带分发，启动期定位并前置进当前进程 PATH，使原生加载器稳定找到 libvlc.dll。</para>
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
            // 必须在创建任何窗口之前调用，避免 DWM 非整数倍位图拉伸造成摩尔纹。
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

            // VLC 原生库定位：必须在解析 VLC 后端单例之前把 libvlc 目录前置 PATH，
            // 使原生加载器（LoadLibrary 搜 PATH）稳定找到 libvlc.dll。
            Console.WriteLine("VLC 原生库定位:");
            string? vlcDir = LocateLibVlc();
            if (vlcDir is null)
            {
                Console.WriteLine("  [失败] 未找到原生 libvlc.dll（原生包未随输出分发，且系统也未安装 VLC）。");
                Console.WriteLine("         请确认探针工程已引用 VLC 原生包，或在系统中安装 VLC。");
                return 3;
            }
            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (existingPath.IndexOf(vlcDir, StringComparison.OrdinalIgnoreCase) < 0)
                Environment.SetEnvironmentVariable("PATH", vlcDir + Path.PathSeparator + existingPath);
            Console.WriteLine($"  [OK]   libvlc 目录(已前置 PATH): {vlcDir}");
            Console.WriteLine();

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
        // —— 两套有头测试的选择开关 ——
        // 默认（不加 --hw）= 软件解码有头；加 --hw = VLC 内部 D3D11VA 硬解（仍 CPU BGRA32 交付，无零拷贝）。
        bool useHw = HasFlag(args, "--hw");
        bool exclusiveMode = HasFlag(args, "--exclusive");
        bool pollingMode = HasFlag(args, "--polling");
        bool audioWarmup = HasFlag(args, "--audio-warmup");
        bool fullPlayback = !HasFlag(args, "--no-full");   // 默认放满全程（--no-full 才截断 12s 采样窗口）
        // 重播验证：需显式 --replay 才启用（默认只放满一次，避免默认跑两遍）。
        bool replayTest = fullPlayback && HasFlag(args, "--replay");
        Console.WriteLine($"[VLC-HEADFUL] 测试形态 = {(useHw ? "VLC 内部硬解(D3D11VA)有头(--hw，仍 CPU BGRA32 交付)" : "VLC 软件解码有头(默认)")}");
        Console.WriteLine($"[VLC-HEADFUL] 视频上屏={doVideo} 音频出声={doAudio} " +
                          $"时钟={(doAudio ? "真实WASAPI硬件游标(LINGFAN_CLOCK_AUDIO_POS=1)" : "无头软件主时钟")}");
        Console.WriteLine($"[VLC-HEADFUL] Exclusive={exclusiveMode} EventDriven={!pollingMode} AudioWarmup={audioWarmup} Full={fullPlayback} Replay={replayTest}");
        // 音画同步主时钟：接真实 WASAPI 设备时打开 LINGFAN_CLOCK_AUDIO_POS=1，使 SetMasterClockProvider 生效，
        // 主时钟直接读音频设备硬件游标。纯无头（NoOp 输出）下不可开启，否则位置恒为 0 会破坏调度，
        // 故仅在 doAudio 时设置。
        if (doAudio)
            Environment.SetEnvironmentVariable("LINGFAN_CLOCK_AUDIO_POS", "1");
        // 同步诊断（仅 full 开启）：每次呈现打印 videoPTS − audioClock，定量观察音画偏差。
        if (fullPlayback)
            Environment.SetEnvironmentVariable("LINGFAN_SYNC_DIAG", "1");

        double syncLeadMs = ParseDouble(args, "--sync-lead", 0);
        if (syncLeadMs != 0)
            Environment.SetEnvironmentVariable("LINGFAN_SYNC_LEAD_MS", syncLeadMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        int saveFrames = (int)ParseDouble(args, "--save-frames", 0);
        string saveDir = ParseOption(args, "--save-dir")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "TestInfo", "Diagnostics", "VlcHeadfulPlaybackProbe");
        string? file = ParseOption(args, "--file");
        double seconds = ParseDouble(args, "--seconds", 12);
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

        var (sp, player, countingFactory) = BuildPlayer(
            verbose, doVideo, doAudio, enableCategory, exclusiveMode, pollingMode, saveFrames, saveDir);
        await using var _sp = sp;

        // 音频进度观测量（确认音频经 VLC 回调捕获并真实出声）
        long submittedSamples = 0;
        int sampleRate = 0;
        player.AudioDataAvailable += f =>
        {
            Interlocked.Add(ref submittedSamples, f.FrameCount);
            if (sampleRate == 0) sampleRate = f.SampleRate;
        };

        double maxAudioBacklogMs = 0;
        int audioGapCount = 0;
        int audioStallCount = 0;
        long prevSubmitted = 0;
        double prevPosSec = 0;

        RenderWindow? win = null;
        int presentCount = 0;
        bool videoPass = !doVideo;
        bool audioPass = !doAudio;
        bool replayPass = !replayTest;

        try
        {
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
                        $"DPI={win.Dpi}({scale:P0}) | 客户区与请求{(clientMatches ? "一致" : "不一致")} | SwapChain 将按实测客户区建立");
                    if (!clientMatches)
                        Console.WriteLine("[HEADFUL-WND] 客户区≠请求：说明存在窗口几何虚拟化，DWM 会额外拉伸一层。");
                }
            }

            Console.WriteLine($"OpenAsync: {file}");
            var sw = Stopwatch.StartNew();
            await player.OpenAsync(new FileMediaSource(file), CancellationToken.None);
            double openSec = sw.Elapsed.TotalSeconds;
            Console.WriteLine($"  OpenAsync 耗时 {openSec:F2}s  Duration={player.Duration:g}  " +
                              $"VideoTracks={player.Session?.VideoTracks.Count ?? 0}  " +
                              $"AudioTracks={player.Session?.AudioTracks.Count ?? 0}");
            Console.WriteLine($"  视频编码: {player.Session?.VideoTracks.FirstOrDefault()?.VideoCodec}  " +
                              $"音频编码: {player.Session?.AudioTracks.FirstOrDefault()?.AudioCodec}");

            if (doVideo && win is not null && win.Hwnd != IntPtr.Zero)
            {
                if (countingFactory!.Last is null)
                    Console.WriteLine("D3D11 渲染器未创建（环境无 GPU/显示），跳过视频有头验证。");
                else
                    countingFactory.Last.Attach(new HwndRenderTarget(win.Hwnd,
                        win.ClientW > 0 ? win.ClientW : windowW,
                        win.ClientH > 0 ? win.ClientH : windowH));
                // D3D11 经统一的 VideoFrameAvailable 订阅 Present；是否零拷贝取决于 Sink 能力，不走额外分支。
                player.VideoFrameAvailable += f => countingFactory.Last?.Present(f);
            }

            await player.PlayAsync();
            Console.WriteLine("  PlayAsync 完成，开始轮询…");

            var startPos = player.Position;
            var poll = Stopwatch.StartNew();
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
                            Console.WriteLine($"  [HEADFUL-AUDIO-GAP] t={poll.Elapsed.TotalSeconds:F1}s backlog={backlogMs:F0}ms");
                    }
                    if (prevSubmitted != 0 && submittedSamples == prevSubmitted && posSec > prevPosSec + 0.1)
                    {
                        audioStallCount++;
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
                        Console.WriteLine("  [HEADFUL] 超时未收到 Ended（兜底退出）");
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
                Console.WriteLine($"[HEADFUL-VIDEO-DROP] droppedFrames={player.VideoDroppedFrames} present={presentCount}");
                if (player.VideoDroppedFrames > 0)
                    Console.WriteLine($"  [HEADFUL-VIDEO-DROP] 存在丢帧（同步器判定丢弃或 Present 滞后），可结合 SYNC_DIAG 进一步定位。");
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
                Console.WriteLine($"[HEADFUL-AUDIO-GAP] maxBacklog={maxAudioBacklogMs:F0}ms gaps(>150ms)={audioGapCount} " +
                                  $"stalls={audioStallCount} => {(realAudioGap ? "检测到音频间隙（真欠载）" : "未检测到间隙")}");
                if (audioStallCount > 0 && !realAudioGap)
                    Console.WriteLine($"  [HEADFUL-AUDIO-GAP] 注：stalls={audioStallCount} 均发生在 backlog=0 的连续播放中，属批量提交节奏（非欠载）。");
            }

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

        bool overall = videoPass && audioPass && replayPass;
        Console.WriteLine();
        if (saveFrames > 0)
            Console.WriteLine($"[HEADFUL-SAVE] 共落盘 {FrameDumper.DumpedCount} 张帧 -> {saveDir}");
        Console.WriteLine(overall ? "总体 PASS" : "总体 FAIL");
        return overall ? 0 : 1;
    }


}

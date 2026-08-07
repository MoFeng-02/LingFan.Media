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
using LingFan.Media.Backends.VLC;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VlcHeadfulPlaybackProbe;

/// <summary>
/// 最小可验证程序 <b>VLC 有头（Headful）全链路</b>——参照 <c>HeadfulPlaybackProbe</c>（MF）与
/// <c>FfmpegHeadfulPlaybackProbe</c>（FFmpeg），把后端换成 <b>VLC</b>：真实 D3D11 SwapChain 上屏（GPU Present）
/// + 真实 WASAPI 出声，二者共用真实 MediaPlayer + 真实 VLC 解码（经 SetVideoCallbacks 内存捕获 BGRA32 帧）。
/// </summary>
/// <remarks>
/// <para>VLC 服务端经 LibVLCSharp 驱动 VLC 引擎，内部一体化完成解封装+解码；通过 <c>SetVideoCallbacks</c>
/// 把<b>已解码 BGRA32 帧</b>推给我们管线（直通解码器）。因此 VLC 路径<b>永远是 CPU 内存帧</b>，
/// 走不到 ffmpeg D3D11VA 那样的 GPU 零拷贝——上屏由 D3D11Renderer 经 V1 CopyResource 快路径上传纹理完成。</para>
/// <para><c>--vout=dummy</c>（<c>VLCOptions.Headless=true</c>）禁止 VLC 自建原生窗口，视频帧经回调全交给我们管线，
/// 由 D3D11Renderer 的 Present Sink 上屏——即架构「有头=无头管线+订阅式 Present Sink」。区别于 <c>--no-video</c>
/// （会停解码），Headless 仅屏蔽 VLC 自有绘图。</para>
/// <para>两套有头测试场景：</para>
/// <list type="bullet">
///   <item><b>默认（软件解码有头）</b>：不加 <c>--hw</c>。VLC 软件解码 → BGRA32 软件帧 → D3D11 上传上屏；
///        真实 WASAPI 出声；开启 <c>LINGFAN_CLOCK_AUDIO_POS=1</c> → 主时钟由<b>音频设备硬件游标</b>驱动。</item>
///   <item><b><c>--hw</c>（VLC 内部硬解有头）</b>：VLC 内部走 D3D11VA 硬解，但<b>回调仍交付 CPU BGRA32</b>
///        （VLC 无零拷贝路径）；D3D11 上传上屏；同样硬件游标严格时钟。</item>
/// </list>
/// <para>隔离变量（同 MF/FFmpeg）：<c>--no-video</c>/<c>--no-audio</c>/<c>--visible</c>/
/// <c>--exclusive</c>/<c>--polling</c>/<c>--audio-warmup</c>/<c>--category</c>/<c>--full</c>/<c>--no-replay</c>。</para>
/// <para>窗口与 P/Invoke 用 <c>[LibraryImport]</c>（AOT 合规），帧落盘零依赖（与 MF/FFmpeg 探针同款）。</para>
/// <para>原生 libvlc 经 <b>VideoLAN.LibVLC.Windows</b> NuGet 包随输出目录自带分发（LGPL），启动期定位并前置进
/// 当前进程 PATH，使 LibVLCSharp 原生加载器稳定找到 libvlc.dll。</para>
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
            // 🔴 必须在创建任何窗口之前（同 MF/FFmpeg 探针，避免 DWM 非整数倍位图拉伸造摩尔纹）。
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

            // 🔴 VLC 原生库定位（必须在解析 VLCBackend Singleton 之前把 libvlc 目录前置 PATH，
            // 使 LibVLCSharp 的原生加载器(LoadLibrary 搜 PATH)稳定找到 libvlc.dll）。
            Console.WriteLine("VLC 原生库定位:");
            string? vlcDir = LocateLibVlc();
            if (vlcDir is null)
            {
                Console.WriteLine("  [失败] 未找到原生 libvlc.dll（VideoLAN.LibVLC.Windows 原生包未随输出分发，且系统也未装 VLC）。");
                Console.WriteLine("         请确认 VlcHeadfulPlaybackProbe.csproj 已引用 VideoLAN.LibVLC.Windows，或安装 VLC。");
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
        bool fullPlayback = HasFlag(args, "--full");
        // 🔴 重播验证：headful 下默认启用（需先到达 Ended 才能重播）；--no-replay 可关闭。
        bool replayTest = fullPlayback && !HasFlag(args, "--no-replay");
        Console.WriteLine($"[VLC-HEADFUL] 测试形态 = {(useHw ? "VLC 内部硬解(D3D11VA)有头(--hw，仍 CPU BGRA32 交付)" : "VLC 软件解码有头(默认)")}");
        Console.WriteLine($"[VLC-HEADFUL] 视频上屏={doVideo} 音频出声={doAudio} " +
                          $"时钟={((doAudio && !fullPlayback) || (doAudio) ? "真实WASAPI硬件游标(LINGFAN_CLOCK_AUDIO_POS=1)" : "无头软件主时钟")}");
        Console.WriteLine($"[VLC-HEADFUL] Exclusive={exclusiveMode} EventDriven={!pollingMode} AudioWarmup={audioWarmup} Full={fullPlayback} Replay={replayTest}");
        // 🔴 音画同步主时钟（严格锚定根治路径）：接真实 WASAPI 设备时打开 LINGFAN_CLOCK_AUDIO_POS=1，
        // 使 MediaPlayer.cs 的 SetMasterClockProvider 生效 → 主时钟直接读音频设备硬件游标（GetPlaybackPositionDirect），
        // 即 MF/FFmpeg 那套「严格锚定」。纯无头(NoOp)不可开（会恒返回 0 反而搞坏调度），故仅 doAudio 时开。
        if (doAudio)
            Environment.SetEnvironmentVariable("LINGFAN_CLOCK_AUDIO_POS", "1");
        // 🔴 同步诊断（仅 full 开，零架构风险）：每次呈现打印 videoPTS − audioClock，定量判断音画偏差。
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
            Console.Error.WriteLine($"⚠ 窗口尺寸非法：{windowW}x{windowH}");
            return 2;
        }

        if (saveFrames > 0)
            Console.WriteLine($"[HEADFUL-SAVE] 每 {saveFrames} 帧落盘 -> {saveDir}");

        if (!doVideo && !doAudio)
        {
            Console.Error.WriteLine("⚠ --no-video 与 --no-audio 不能同时使用。");
            return 2;
        }

        if (file is null)
            file = ResolveDefaultMedia(doVideo);
        if (file is null || !File.Exists(file))
        {
            Console.Error.WriteLine($"⚠ 找不到媒体文件：{file ?? "(null)"}");
            return 2;
        }

        var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        var builder = services.AddLingFanMedia()
            .AddVLC(o =>
            {
                // 🔴 VLC 经 SetVideoCallbacks 内存捕获已解码帧，自身不依赖原生窗口。
                // Headless=true → 注入 --vout=dummy，禁止 VLC 自建窗口；视频帧经回调全交给我们管线，
                // 由下方 D3D11Renderer 的 Present Sink 上屏（有头=无头管线+订阅式 Present Sink）。
                // 区别于「--no-video」（会停解码），Headless 仅屏蔽 VLC 自有绘图。
                // --hw 仅让 VLC 内部走 D3D11VA，但回调仍交付 CPU BGRA32（与 ffmpeg D3D11VA 零拷贝不同）。
                o.EnableHardwareDecoding = useHw;
                o.Headless = true;
            });

        // —— 视频侧（同 MF/FFmpeg 探针：手动装饰 D3D11RendererFactory 统计 Present，并补回 IGpuDeviceContext）——
        // VLC 经回调交付 BGRA32 软件帧，D3D11Renderer 走 V1 CopyResource 快路径上传上屏（非零拷贝）。
        CountingVideoRendererFactory? countingFactory = null;
        if (doVideo)
        {
            var d3d11Factory = new D3D11RendererFactory(loggerFactory);
            countingFactory = new CountingVideoRendererFactory(d3d11Factory, saveFrames, saveDir);
            builder.Services.AddSingleton<IVideoRendererFactory>(countingFactory);
            // 🔴 保住 IGpuDeviceContext：VLC 帧为 BGRA32 CPU 内存，D3D11Renderer 用它自己的共享设备上传纹理上屏
            //（VLC 无零拷贝路径，与 ffmpeg D3D11VA 零拷贝不同）。
            builder.Services.AddSingleton<IGpuDeviceContext>(sp => d3d11Factory.Context);
        }
        else
        {
            builder.AddHeadlessRenderer();
        }

        // —— 音频侧 ——
        if (doAudio)
            builder.AddWasapiOutput(o =>
            {
                o.EnableBackgroundCapableSession = enableCategory;
                o.ExclusiveMode = exclusiveMode;
                o.EventDrivenMode = !pollingMode;
            });
        else
            builder.AddSilentAudioOutput();

        await using var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

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
            // —— WASAPI 预热（opt-in，--audio-warmup；对应 #7 冷启动 3s 修复）——
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
                Console.WriteLine("[HEADFUL-WASAPI] 未启用音频预热（加 --audio-warmup 开启；引擎常驻保活可消除 ~2.5s 冷启动）");
            }

            if (doVideo)
            {
                win = new RenderWindow(windowW, windowH, visible);
                if (win.Hwnd == IntPtr.Zero)
                {
                    Console.WriteLine("⚠ 窗口创建失败（HWND=0），视频有头验证将不可靠。");
                }
                else
                {
                    double scale = win.Dpi == 0 ? 1.0 : win.Dpi / 96.0;
                    bool clientMatches = win.ClientW == windowW && win.ClientH == windowH;
                    Console.WriteLine(
                        $"[HEADFUL-WND] 请求={windowW}x{windowH} 实测客户区={win.ClientW}x{win.ClientH} " +
                        $"DPI={win.Dpi}({scale:P0}) | 客户区与请求{(clientMatches ? "一致" : "不一致⚠")} | SwapChain 将按实测客户区建立");
                    if (!clientMatches)
                        Console.WriteLine("[HEADFUL-WND] ⚠ 客户区≠请求：说明存在窗口几何虚拟化，DWM 会额外拉伸一层。");
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
                    Console.WriteLine("⚠ D3D11 渲染器未创建（环境无 GPU/显示），跳过视频有头验证。");
                else
                    countingFactory.Last.Attach(new HwndRenderTarget(win.Hwnd,
                        win.ClientW > 0 ? win.ClientW : windowW,
                        win.ClientH > 0 ? win.ClientH : windowH));
                // 🔴 收敛后 D3D11 经统一 VideoFrameAvailable 订阅 Present（与 MF/FFmpeg 探针同款；零拷贝是 Sink 能力差异，非分支）。
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
                // 🔴 音频间隙检测：主时钟位置 vs 已提交音频时长（与 MF/FFmpeg 探针同口径）
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
                    Console.WriteLine($"  [HEADFUL-VIDEO-DROP] ⚠ 仍有丢帧（通道未丢+同步器判 Drop 或 Present 滞后），需结合 SYNC_DIAG 定位。");
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
                                  $"stalls={audioStallCount} => {(realAudioGap ? "⚠ 检测到音频间隙（真欠载）" : "未检测到间隙")}");
                if (audioStallCount > 0 && !realAudioGap)
                    Console.WriteLine($"  [HEADFUL-AUDIO-GAP] 注：stalls={audioStallCount} 均发生在 backlog=0 的连续播放中，属批量提交节奏（非欠载）。");
            }

            // —— 重播验证（边界①：Ended→Playing 无缝从头；回答「首次启动后能否重播无缝」）——
            if (replayTest)
            {
                Console.WriteLine();
                Console.WriteLine("[HEADFUL-REPLAY] 第一次播放已 Ended，立即二次 PlayAsync 验证重播无缝从头…");
                double posBeforeReplay = player.Position.TotalSeconds;
                int presentBeforeReplay = presentCount;
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
                Console.WriteLine($"  [HEADFUL-REPLAY] 二次播放结束 state={player.State} present 增量={replayPresentDelta}");
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
        Console.WriteLine(overall ? "✅ 总体 PASS" : "❌ 总体 FAIL");
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
            if (_saveFrames > 0 && n % _saveFrames == 0)
                FrameDumper.DumpFrame(frame, n, _saveDir);
            _inner.Present(frame);
        }
        public TimeSpan PresentationLatency => TimeSpan.Zero;
        public void Clear() => _inner.Clear();
        public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);
        public void Dispose() => _inner.Dispose();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    // ── 渲染目标：把 HWND 包装成 IRenderTarget ──

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
        public int ClientW { get; private set; }
        public int ClientH { get; private set; }
        public uint Dpi { get; private set; }
        private readonly ManualResetEventSlim _ready = new();
        private readonly Thread _thread;
        private volatile bool _alive = true;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_VISIBLE = 0x10000000;
        private readonly bool _visible;
        private readonly int _w, _h;
        private const string WindowClassName = "LingFanVlcProbeWnd";
        private static readonly object _classLock = new();
        private static bool _classRegistered;
        private static WndProcDelegate? _wndProcKeepAlive;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public RenderWindow(int w, int h, bool visible)
        {
            _w = w; _h = h; _visible = visible;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
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

        private static void RegisterWindowClass()
        {
            lock (_classLock)
            {
                if (_classRegistered) return;
                IntPtr namePtr = IntPtr.Zero;
                try
                {
                    namePtr = Marshal.StringToHGlobalUni(WindowClassName);
                    _wndProcKeepAlive = StaticWndProc;
                    var wc = new NativeMethods.WNDCLASSEXW
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                        style = 0,
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                        hInstance = IntPtr.Zero,
                        hbrBackground = NativeMethods.GetStockObject(4),
                        lpszMenuName = IntPtr.Zero,
                        lpszClassName = namePtr,
                        hCursor = NativeMethods.LoadCursorW(IntPtr.Zero, (IntPtr)32512),
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

    /// <summary>定位原生 libvlc 目录（libvlc.dll 所在目录）：优先探针自带分发的 NuGet 原生包，其次本机已装 VLC。</summary>
    private static string? LocateLibVlc()
    {
        string baseDir = AppContext.BaseDirectory;

        // 1) 探针输出目录树（VideoLAN.LibVLC.Windows 原生包随构建复制到此；位置不固定，
        //    可能在根目录、libvlc 子目录或 win-x64/libvlc 等）。深度受限搜索避免误入无关目录。
        string? bundled = FindBundledLibVlc(baseDir);
        if (bundled is not null) return bundled;

        // 2) 进程 PATH 中各目录
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "libvlc.dll")))
                    return dir;
            }
            catch { /* 忽略无权限目录 */ }
        }

        // 3) 常见系统安装位置（无自带包时的回退）
        var candidates = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string? pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string? pfX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (pf is not null) candidates.Add(Path.Combine(pf, "VideoLAN", "VLC"));
            if (pfX86 is not null) candidates.Add(Path.Combine(pfX86, "VideoLAN", "VLC"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            candidates.Add("/usr/lib/x86_64-linux-gnu");
            candidates.Add("/usr/lib");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add("/Applications/VLC.app/Contents/MacOS/lib");
            candidates.Add("/usr/local/lib");
        }

        foreach (var cand in candidates)
        {
            try
            {
                if (File.Exists(Path.Combine(cand, "libvlc.dll")) ||
                    File.Exists(Path.Combine(cand, "libvlc.so")) ||
                    File.Exists(Path.Combine(cand, "libvlc.dylib")))
                    return cand;
            }
            catch { /* 忽略 */ }
        }

        return null;
    }

    /// <summary>在输出目录树中找含 libvlc.dll 的目录（NuGet 原生包随构建复制的位置不固定）。优先与当前进程架构匹配的那一份。</summary>
    private static string? FindBundledLibVlc(string startDir)
    {
        // VideoLAN.LibVLC.Windows 把原生库放在 libvlc/win-<arch>/ 下。
        // 必须按进程架构选对子目录——把 x86 的 libvlc.dll 注入 64 位进程会导致 LoadLibrary 失败（BAD_EXE_FORMAT）。
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => "x64"
        };
        string archDir = Path.Combine(startDir, "libvlc", "win-" + arch);
        if (File.Exists(Path.Combine(archDir, "libvlc.dll")))
            return archDir;

        // 回退：深度受限扫描（覆盖根目录或其它布局）
        if (File.Exists(Path.Combine(startDir, "libvlc.dll")))
            return startDir;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(startDir))
            {
                if (File.Exists(Path.Combine(sub, "libvlc.dll")))
                    return sub;
                foreach (var sub2 in Directory.EnumerateDirectories(sub))
                {
                    if (File.Exists(Path.Combine(sub2, "libvlc.dll")))
                        return sub2;
                }
            }
        }
        catch { /* 忽略无权限/并发删除 */ }
        return null;
    }
}

// 🔴 帧落盘诊断：把渲染器收到的真实帧（CPU 软解 / GPU 硬解回读）转 RGBA 后写极简 PNG。自包含零依赖。
internal static class FrameDumper
{
    internal static int DumpedCount;

    internal static void DumpFrame(VideoFrame frame, int presentIndex, string dir)
    {
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

// 🔴 [LibraryImport] 源生成要求载体类型为「顶级 partial」，故 user32 P/Invoke 放在本顶级 partial 类（与 MF/FFmpeg 探针同款）。
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

    [LibraryImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(IntPtr value);

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
    public struct POINT { public int x, y; }
}

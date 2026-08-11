using System.Diagnostics;
using System.Text;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaCorrectnessProbe;

/// <summary>
/// 最小可验证程序 <b>b2</b>：<b>无头解码正确性</b>（真实 MF 解码 → NoOp 渲染 + 静音输出，<b>不碰音频设备</b>）。
/// </summary>
/// <remarks>
/// <para>对应单元测试 <c>MediaCorrectnessProbeTests.Probe_VideoM1_HeadlessDecode_VerifiesVideoAndAudioCorrectness</c>。</para>
///
/// <para><b>存在意义（排除力）</b>：本工程<b>不引用 Outputs</b>，音频走 NoOp 静音输出，
/// 因此 WASAPI 完全不在链路里。判定结果纯粹反映「解封装 + 解码」是否正确：</para>
/// <list type="bullet">
///   <item>全绿 + 落盘 WAV 完整有声 ⇒ 解码链路 100% 无辜，任何「听不到 / 花屏」只可能在渲染/输出侧。</item>
///   <item>WAV 后半段静音或联系表花屏 ⇒ 责任在解码或源文件，与 WASAPI / D3D11 无关。</item>
/// </list>
///
/// <para><b>相对测试的关键增强</b>：</para>
/// <list type="number">
///   <item>产出 <c>decoded.wav</c>——测试只画波形图，本程序把解码 PCM 原样落盘，可直接用播放器听。
///     这是「WAV 完整有声吗」这一问题最干净的答案（不掺任何 WASAPI 因素）。</item>
///   <item>逐秒进度 + 逐窗口音频电平（dBFS），能指出「第几秒开始解码出静音」。</item>
///   <item>视频帧<b>采样保留</b>（默认每 40 帧留 1 张，上限 24 张），避免整片 NV12 常驻内存吃掉数 GB；
///     但 PTS 单调性与尺寸一致性仍覆盖<b>全部</b>帧。</item>
///   <item>挂真实 Console Logger + 逐条 PASS/FAIL + 进程退出码（0 全绿 / 1 有失败）。</item>
/// </list>
///
/// <para>用法：</para>
/// <code>
/// dotnet run --project src\Tools\MediaCorrectnessProbe
/// dotnet run --project src\Tools\MediaCorrectnessProbe -- --file "D:\x.mp4" -v
/// dotnet run --project src\Tools\MediaCorrectnessProbe -- --out "D:\out" --keep-every 20
/// </code>
/// </remarks>
internal static class Program
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private static async Task<int> Main(string[] args)
    {
        bool verbose = args.Contains("-v") || args.Contains("--verbose");
        bool noWav = args.Contains("--no-wav");
        int keepEvery = ParseInt(args, "--keep-every", 40);
        int maxKeep = ParseInt(args, "--max-keep", 24);
        int maxSeconds = ParseInt(args, "--max-seconds", 120);
        int dumpFull = ParseInt(args, "--dump-full", 0);
        string? file = ParseOption(args, "--file") ?? ResolveDefaultMedia();
        string outDir = ParseOption(args, "--out") ?? ResolveDiagnosticsDir();

        Console.WriteLine("=== b2 · 无头解码正确性验证（不碰音频设备 / 不建 GPU 设备） ===");
        if (file is null || !File.Exists(file))
        {
            Console.WriteLine($"找不到媒体文件：{file ?? "(null)"}。请用 --file <路径> 指定。");
            return 2;
        }
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"媒体文件      : {file}");
        Console.WriteLine($"输出目录      : {outDir}");
        Console.WriteLine($"视频渲染      : NoOp 无头（AddHeadlessRenderer）");
        Console.WriteLine($"音频输出      : NoOp 静音（AddSilentAudioOutput，按真实节奏节流驱动主时钟）");
        Console.WriteLine($"帧采样保留    : 每 {keepEvery} 帧留 1 张，上限 {maxKeep} 张");
        Console.WriteLine($"日志级别      : {(verbose ? "Debug" : "Information")}");
        Console.WriteLine();

        var services = new ServiceCollection();
        services.AddLogging(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));
        services.AddLingFanMedia()
                .AddMediaFoundation()
                .AddHeadlessRenderer()
                .AddSilentAudioOutput();

        await using var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        // ---- 抓取容器（回调内只做拷贝：帧为只读借用，sink 不得 Dispose）----
        var gate = new object();
        var keptFrames = new List<(byte[] Nv12, int W, int H, TimeSpan Ts)>();
        var audioCaps = new List<(byte[] Pcm, int Rate, int Ch, SampleFormat Fmt, TimeSpan Ts)>();
        var pcmDump = noWav ? null : new MemoryStream(1 << 23);

        int videoCount = 0, audioCount = 0, nv12Count = 0, nonNv12Count = 0;
        int firstW = 0, firstH = 0;
        bool dimsConsistent = true, ptsMonotonic = true;
        var lastTs = TimeSpan.MinValue;
        var lastAudioTs = TimeSpan.MinValue;
        SampleFormat dumpFmt = SampleFormat.S16;
        int dumpCh = 2, dumpRate = 0;

        // 逐窗口电平（定位「第几秒开始解出静音」）
        double winSumSq = 0, winPeak = 0;
        long winSamples = 0;
        double firstSilentSec = -1;

        var sw = new Stopwatch();

        using var videoSink = new ProcessingFrameSink(onFrame: frame =>
        {
            lock (gate)
            {
                videoCount++;
                if (firstW == 0) { firstW = frame.Width; firstH = frame.Height; }
                else if (frame.Width != firstW || frame.Height != firstH) dimsConsistent = false;

                if (lastTs != TimeSpan.MinValue && frame.Timestamp < lastTs) ptsMonotonic = false;
                lastTs = frame.Timestamp;

                if (frame.Resource is SoftwareFrameResource sw2 && sw2.Format == PixelFormat.NV12)
                {
                    nv12Count++;

                    // ── STRIDE 自检（首帧一次）──────────────────────────────────
                    // ── 行间错位检测（SKEW-CHK）：眼睛无关的客观检测 ──────────────
                    // 原理：自然图像相邻两行内容高度相关，把下一行水平平移 d 去匹配上一行，
                    //       最佳 d 必然是 0。若解码时用错了源 stride（假定 A，实际 S），
                    //       则解出来的图像每往下一行就整体平移 d = A - S 像素、并每 S 像素回绕一次
                    //       —— 肉眼即斜条纹/剪切感，数值上表现为「相邻行最佳位移恒定非零」。
                    // 这种检测同时覆盖裁剪路径与整块拷贝路径，且不需要知道 codedWidth。
                    if (nv12Count == 1)
                    {
                        int skW = frame.Width, skH = frame.Height;
                        var skY = sw2.Data.Span[..(skW * skH)];
                        const int skR = 48;        // 水平搜索半径（像素）
                        const int skMargin = 96;   // 忽略左右边缘，避开回绕区污染（须 > skR）
                        var skVotes = new Dictionary<int, int>();
                        int skRows = 0;
                        long skBestSadSum = 0, skZeroSadSum = 0;
                        for (int y = 8; y < skH; y += 17) // 质数步长，避开画面自身的周期性共振
                        {
                            long best = long.MaxValue; int bestD = 0; long sadAtZero = 0;
                            for (int d = -skR; d <= skR; d++)
                            {
                                long sad = 0;
                                for (int x = skMargin; x < skW - skMargin; x += 3)
                                    sad += Math.Abs(skY[y * skW + x] - skY[(y - 1) * skW + x + d]);
                                if (d == 0) sadAtZero = sad;
                                if (sad < best) { best = sad; bestD = d; }
                            }
                            skVotes[bestD] = skVotes.GetValueOrDefault(bestD) + 1;
                            skBestSadSum += best; skZeroSadSum += sadAtZero;
                            skRows++;
                        }
                        int skMode = 0, skModeCount = 0;
                        foreach (var kv in skVotes)
                            if (kv.Value > skModeCount) { skModeCount = kv.Value; skMode = kv.Key; }
                        int skZero = skVotes.GetValueOrDefault(0);
                        Console.WriteLine(
                            $"[SKEW-CHK] 采样 {skRows} 行 | 相邻行最佳水平位移众数 d={skMode}（{skModeCount}/{skRows} 行）| " +
                            $"d=0 的行数 {skZero}/{skRows} | SAD(best)/SAD(d=0)={(skZeroSadSum > 0 ? (double)skBestSadSum / skZeroSadSum : 1.0):F4}");
                        if (skMode == 0 && skZero * 2 >= skRows)
                            Console.WriteLine("           => 行对齐正常，解码布局无错位 ⇒ stride 假定成立，花屏不在解码器");
                        else
                            Console.WriteLine($"           => ★恒定错位 {skMode} px/行 ⇒ 真实源 stride = 假定stride - ({skMode})，解码器 stride 假定错误★");
                    }

                    // 采样保留：整片 NV12 常驻会吃数 GB，这里只留均匀分布的若干张做视觉证据。
                    if (keptFrames.Count < maxKeep && (nv12Count - 1) % keepEvery == 0)
                    {
                        var buf = new byte[sw2.Data.Length];
                        sw2.Data.Span.CopyTo(buf);
                        keptFrames.Add((buf, frame.Width, frame.Height, frame.Timestamp));
                    }
                }
                else nonNv12Count++;
            }
        });

        using var audioSink = new ProcessingAudioSink(onAudio: frame =>
        {
            var buf = new byte[frame.Data.Length];
            frame.Data.Span.CopyTo(buf);
            int bps = frame.SampleFormat == SampleFormat.S16 ? 2 : 4;
            int byteLen = Math.Min(buf.Length, frame.FrameCount * frame.Channels * bps);
            var (sumSq, peak, n) = Level(buf.AsSpan(0, byteLen), frame.SampleFormat);

            lock (gate)
            {
                audioCount++;
                lastAudioTs = frame.Timestamp;
                dumpFmt = frame.SampleFormat;
                dumpCh = frame.Channels;
                if (dumpRate == 0) dumpRate = frame.SampleRate;
                audioCaps.Add((buf, frame.SampleRate, frame.Channels, frame.SampleFormat, frame.Timestamp));
                pcmDump?.Write(buf, 0, byteLen);
                winSumSq += sumSq;
                winSamples += n;
                if (peak > winPeak) winPeak = peak;
            }
        });

        bool hasAudio = false;
        int sessVW = 0, sessVH = 0, sessAR = 0, sessACH = 0;
        TimeSpan duration = TimeSpan.Zero;

        try
        {
            videoSink.Attach(player);
            audioSink.Attach(player);

            sw.Start();
            await player.OpenAsync(new FileMediaSource(file), CancellationToken.None);
            var session = player.Session!;
            duration = player.Duration;
            hasAudio = session.AudioTracks.Count > 0;
            if (session.VideoTracks.Count > 0)
            {
                sessVW = session.VideoTracks[0].VideoInfo?.Width ?? 0;
                sessVH = session.VideoTracks[0].VideoInfo?.Height ?? 0;
            }
            if (hasAudio)
            {
                sessAR = session.AudioTracks[0].AudioInfo?.SampleRate ?? 0;
                sessACH = session.AudioTracks[0].AudioInfo?.Channels ?? 0;
            }

            Console.WriteLine($"OpenAsync 完成: Duration={duration:g}  Video={sessVW}x{sessVH}  " +
                              $"Audio={(hasAudio ? $"{sessAR}Hz/{sessACH}ch" : "无音轨")}");
            Console.WriteLine();

            await player.PlayAsync();

            Console.WriteLine("  t(s)   pos(s)  video  audio  keptPNG   dBFS   peak  备注");
            Console.WriteLine("  ----  -------  -----  -----  -------  -----  -----  ------------");

            int stableRounds = 0, lastTotal = -1;
            while (sw.Elapsed < TimeSpan.FromSeconds(maxSeconds))
            {
                await Task.Delay(SampleInterval);

                int v, a, kept;
                double rms, peak;
                lock (gate)
                {
                    v = videoCount; a = audioCount; kept = keptFrames.Count;
                    rms = winSamples > 0 ? Math.Sqrt(winSumSq / winSamples) : 0;
                    peak = winPeak;
                    winSumSq = 0; winSamples = 0; winPeak = 0;
                }

                double t = sw.Elapsed.TotalSeconds;
                double dbfs = rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity;
                string note = "";
                if (a > 0 && dbfs < -80)
                {
                    note = "解出静音";
                    if (firstSilentSec < 0) firstSilentSec = t;
                }

                Console.WriteLine($"  {t,4:F0}  {player.Position.TotalSeconds,7:F2}  {v,5}  {a,5}  {kept,7}  " +
                                  $"{(double.IsNegativeInfinity(dbfs) ? "  -inf" : dbfs.ToString("F1").PadLeft(5))}  " +
                                  $"{peak,5:F3}  {note}");

                // EOF 判定：帧总数连续 3 个采样窗口不变
                int total = v + a;
                stableRounds = total == lastTotal ? stableRounds + 1 : 0;
                lastTotal = total;
                if (v >= 10 && (!hasAudio || a >= 10) && stableRounds >= 3) break;
            }

            await player.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            videoSink.Detach();
            audioSink.Detach();
            await player.DisposeAsync();
            await Task.Delay(300);
        }

        // ================= 产出工件 =================
        Console.WriteLine();
        Console.WriteLine("=== 产出工件 ===");

        var luma = new ImageUtil.LumaStats();
        string? contactSheet = null;
        if (keptFrames.Count > 0)
        {
            contactSheet = Path.Combine(outDir, "contact_sheet.png");
            ImageUtil.BuildContactSheet(keptFrames.OrderBy(f => f.Ts).ToList(), contactSheet, luma);
            Console.WriteLine($"  联系表(肉眼看画面) : {contactSheet}");
            Console.WriteLine($"   注意：联系表每格仅 {Math.Min(keptFrames.Min(f => f.W), 320)} 宽" +
                              $"（原始 {keptFrames[0].W} → 缩小 {keptFrames[0].W / (double)Math.Min(keptFrames.Min(f => f.W), 320):F2}x），" +
                              $"不可用于判定画质。判画质请用 --dump-full N 看 1:1 原图。");
        }

        // ---- 1:1 原图落盘 + 方向性频域体检（眼睛无关检测）----
        if (dumpFull > 0 && keptFrames.Count > 0)
        {
            var ordered = keptFrames.OrderBy(f => f.Ts).ToList();
            string fullDir = Path.Combine(outDir, "fullframes");
            var files = ImageUtil.DumpFullFrames(ordered, fullDir, dumpFull);
            Console.WriteLine();
            Console.WriteLine($"  1:1 原图({files.Count} 个文件) : {fullDir}");
            foreach (var p in files) Console.WriteLine($"      {Path.GetFileName(p)}");

            Console.WriteLine();
            Console.WriteLine("  --- 方向性频域体检（首帧，眼睛无关）---");
            var f0 = ordered[0];
            int w0 = f0.W, h0 = f0.H, ySize0 = w0 * h0;
            int uvW0 = (w0 + 1) / 2, uvH0 = (h0 + 1) / 2;

            Console.WriteLine("  " + ImageUtil.AnalyzePlane(f0.Nv12.AsSpan(0, ySize0), w0, h0, "Y"));

            // UV 交错平面拆成独立 U / V 再分析（交错直接分析会把两个通道混为一谈）
            var uPlane = new byte[uvW0 * uvH0];
            var vPlane = new byte[uvW0 * uvH0];
            for (int i = 0; i < uvW0 * uvH0; i++)
            {
                uPlane[i] = f0.Nv12[ySize0 + i * 2];
                vPlane[i] = f0.Nv12[ySize0 + i * 2 + 1];
            }
            Console.WriteLine("  " + ImageUtil.AnalyzePlane(uPlane, uvW0, uvH0, "U"));
            Console.WriteLine("  " + ImageUtil.AnalyzePlane(vPlane, uvW0, uvH0, "V"));
            Console.WriteLine("  判读：三行全部『方向性正常』⇒ 解码出的 NV12 无条纹，脏必在渲染侧；");
            Console.WriteLine("        任意一行告警 ⇒ 脏已在解码输出中，与 D3D11 无关。");
        }

        string? waveform = null;
        double audioRms = 0, audioPeak = 0;
        if (audioCaps.Count > 0)
        {
            waveform = Path.Combine(outDir, "audio_waveform.png");
            (audioRms, audioPeak) = ImageUtil.BuildWaveform(audioCaps.OrderBy(a => a.Ts).ToList(), waveform);
            Console.WriteLine($"  波形图(肉眼看信号) : {waveform}");
        }

        string? wavPath = null;
        if (pcmDump is not null && pcmDump.Length > 0 && dumpRate > 0)
        {
            wavPath = Path.Combine(outDir, "decoded.wav");
            var pcm = pcmDump.ToArray();
            ImageUtil.WriteWav(wavPath, pcm, dumpRate, dumpCh, dumpFmt);
            double wavSec = pcm.Length / (double)(dumpRate * dumpCh * (dumpFmt == SampleFormat.S16 ? 2 : 4));
            Console.WriteLine($"  解码 PCM(可直接听) : {wavPath}");
            Console.WriteLine($"                       {pcm.Length:N0} 字节, {dumpFmt}, {dumpCh}ch, {dumpRate}Hz, 时长 {wavSec:F2}s");
        }

        var jsonPath = Path.Combine(outDir, "correctness_report.json");
        await File.WriteAllTextAsync(jsonPath, BuildJson(
            videoCount, audioCount, hasAudio, nv12Count, nonNv12Count,
            firstW, firstH, sessVW, sessVH, sessAR, sessACH,
            ptsMonotonic, dimsConsistent, luma, audioRms, audioPeak,
            duration, lastTs, lastAudioTs, contactSheet, waveform, wavPath));
        Console.WriteLine($"  指标报告           : {jsonPath}");

        // ================= 逐条判定 =================
        Console.WriteLine();
        Console.WriteLine("=== 判定 ===");
        bool dimsMatchSession = sessVW <= 0 || (dimsConsistent && firstW == sessVW && firstH == sessVH);
        int fails = 0;

        Check(ref fails, videoCount > 0, "视频产出帧", $"videoFrames={videoCount}");
        if (hasAudio)
            Check(ref fails, audioCount > 0, "音频产出 PCM 帧", $"audioFrames={audioCount}");
        Check(ref fails, dimsConsistent, "所有视频帧尺寸一致", $"{firstW}x{firstH}");
        Check(ref fails, dimsMatchSession, "帧尺寸 == 解封装轨尺寸", $"帧 {firstW}x{firstH} vs 轨 {sessVW}x{sessVH}");
        Check(ref fails, ptsMonotonic, "视频 PTS 单调非递减", $"末帧 {FormatTs(lastTs)}");
        Check(ref fails, luma.Max - luma.Min > 8, "解码画面亮度存在真实变化（非空白/乱码）",
              $"luma=[{luma.Min},{luma.Max}] std={luma.Std:F2}");
        if (hasAudio)
            Check(ref fails, audioRms > 1e-4, "音频 RMS > 0（真实信号而非全零）",
                  $"RMS={audioRms:E3} Peak={audioPeak:E3}");

        // 完整性：解码覆盖的时间跨度应接近 min(容器时长, --max-seconds)
        // 注：当用户传 --max-seconds 主动截断时，断言以截断上限为准（不为 8s 截断报"未覆盖 31s 完整时长"假警）
        if (duration > TimeSpan.Zero && hasAudio && lastAudioTs != TimeSpan.MinValue)
        {
            double targetSeconds = Math.Min(duration.TotalSeconds, maxSeconds);
            Check(ref fails, lastAudioTs.TotalSeconds >= targetSeconds - 2.0,
                  "音频解码覆盖完整时长",
                  $"末帧时间戳 {lastAudioTs.TotalSeconds:F2}s / 目标 {targetSeconds:F2}s" +
                  $"（容器 {duration.TotalSeconds:F2}s，max-seconds={maxSeconds}s）");
        }

        if (firstSilentSec > 0)
            Console.WriteLine($"  注意：墙钟 {firstSilentSec:F2}s 起出现「解出静音」窗口（电平 < -80dBFS）。");
        if (nonNv12Count > 0)
            Console.WriteLine($"  提示：{nonNv12Count} 帧非 NV12 软件帧（GPU 资源或其它像素格式），未纳入联系表。");

        Console.WriteLine();
        if (fails == 0)
        {
            Console.WriteLine("  全部正确性断言通过 ⇒ 解封装 + 解码链路无辜。");
            Console.WriteLine("     下一步：打开上面的 decoded.wav 与 contact_sheet.png 做肉眼终审——");
            Console.WriteLine("     WAV 完整有声 + 联系表画面正常 ⇒ 任何「听不到/看不到」只可能在渲染/输出侧（见 b1 / b3）。");
        }
        else
        {
            Console.WriteLine($"  {fails} 项判定失败 ⇒ 问题落在解封装/解码，与 WASAPI、D3D11 无关。");
        }
        Console.WriteLine();
        Console.WriteLine("=== b2 完成。把以上输出整段贴回即可。 ===");
        return fails == 0 ? 0 : 1;
    }

    private static void Check(ref int fails, bool ok, string title, string detail)
    {
        if (!ok) fails++;
        Console.WriteLine($"  {(ok ? "OK" : "FAIL")} {title,-32} {detail}");
    }

    /// <summary>计算一段 PCM 的平方和 / 峰值 / 样本数（归一化 ±1.0）。</summary>
    private static (double SumSq, double Peak, int N) Level(ReadOnlySpan<byte> span, SampleFormat fmt)
    {
        double sumSq = 0, peak = 0;
        int n;
        switch (fmt)
        {
            case SampleFormat.S16:
            {
                var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(span);
                n = s.Length;
                for (int i = 0; i < n; i++)
                {
                    double v = s[i] / 32768.0;
                    sumSq += v * v;
                    double a = Math.Abs(v); if (a > peak) peak = a;
                }
                break;
            }
            case SampleFormat.F32:
            {
                var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(span);
                n = s.Length;
                for (int i = 0; i < n; i++)
                {
                    double v = s[i];
                    sumSq += v * v;
                    double a = Math.Abs(v); if (a > peak) peak = a;
                }
                break;
            }
            default:
            {
                var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(span);
                n = s.Length;
                for (int i = 0; i < n; i++)
                {
                    double v = s[i] / 2147483648.0;
                    sumSq += v * v;
                    double a = Math.Abs(v); if (a > peak) peak = a;
                }
                break;
            }
        }
        return (sumSq, peak, n);
    }

    private static string BuildJson(
        int videoCount, int audioCount, bool hasAudio, int nv12Count, int nonNv12Count,
        int fw, int fh, int sessVW, int sessVH, int sessAR, int sessACH,
        bool ptsMonotonic, bool dimsConsistent, ImageUtil.LumaStats luma,
        double audioRms, double audioPeak, TimeSpan duration,
        TimeSpan lastVideoTs, TimeSpan lastAudioTs,
        string? contactSheet, string? waveform, string? wav)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"videoFrames\": {videoCount},");
        sb.AppendLine($"  \"audioFrames\": {audioCount},");
        sb.AppendLine($"  \"hasAudio\": {(hasAudio ? "true" : "false")},");
        sb.AppendLine($"  \"nv12Frames\": {nv12Count},");
        sb.AppendLine($"  \"nonNv12Frames\": {nonNv12Count},");
        sb.AppendLine($"  \"frameWidth\": {fw},");
        sb.AppendLine($"  \"frameHeight\": {fh},");
        sb.AppendLine($"  \"sessionVideoWidth\": {sessVW},");
        sb.AppendLine($"  \"sessionVideoHeight\": {sessVH},");
        sb.AppendLine($"  \"sessionAudioSampleRate\": {sessAR},");
        sb.AppendLine($"  \"sessionAudioChannels\": {sessACH},");
        sb.AppendLine($"  \"durationSec\": {duration.TotalSeconds:F3},");
        sb.AppendLine($"  \"lastVideoTsSec\": {(lastVideoTs == TimeSpan.MinValue ? 0 : lastVideoTs.TotalSeconds):F3},");
        sb.AppendLine($"  \"lastAudioTsSec\": {(lastAudioTs == TimeSpan.MinValue ? 0 : lastAudioTs.TotalSeconds):F3},");
        sb.AppendLine($"  \"ptsMonotonic\": {(ptsMonotonic ? "true" : "false")},");
        sb.AppendLine($"  \"dimsConsistent\": {(dimsConsistent ? "true" : "false")},");
        sb.AppendLine($"  \"lumaMin\": {luma.Min},");
        sb.AppendLine($"  \"lumaMax\": {luma.Max},");
        sb.AppendLine($"  \"lumaMean\": {luma.Mean:F2},");
        sb.AppendLine($"  \"lumaStd\": {luma.Std:F2},");
        sb.AppendLine($"  \"audioRms\": {audioRms:E6},");
        sb.AppendLine($"  \"audioPeak\": {audioPeak:E6},");
        sb.AppendLine($"  \"artifacts\": {{ \"contactSheet\": \"{J(contactSheet)}\", \"waveform\": \"{J(waveform)}\", \"wav\": \"{J(wav)}\" }},");
        sb.AppendLine($"  \"generatedAt\": \"{J(DateTime.Now.ToString("O"))}\"");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string J(string? s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string FormatTs(TimeSpan ts) => ts == TimeSpan.MinValue ? "(无)" : $"{ts.TotalSeconds:F2}s";

    private static string? ParseOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int ParseInt(string[] args, string name, int fallback) =>
        int.TryParse(ParseOption(args, name), out var v) ? v : fallback;

    private static string? ResolveDefaultMedia()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "Resources", "Video", "m1.mp4");
        if (File.Exists(local)) return local;
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Resources", "Video", "m1.mp4");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>默认产出目录：仓库根 <c>TestInfo\Diagnostics</c>（与既有测试工件同处，便于对比）。</summary>
    private static string ResolveDiagnosticsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LingFan.Media.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "TestInfo", "Diagnostics");
    }
}

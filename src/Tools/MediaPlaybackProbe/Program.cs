using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaPlaybackProbe;

/// <summary>
/// 最小可验证播放程序：在<b>带控制台窗口的独立真实进程</b>里跑 LingFan.Media 的<b>生产链路</b>
/// （MediaPlayer + MediaFoundation 解码 + 真实 WASAPI 出声），逐秒输出可观测指标。
/// </summary>
/// <remarks>
/// <para>与 <c>WasapiDriverProbe</c> 的分工：后者用官方 <c>[ComImport]</c> 绕开本仓代码、只定性「机器/driver 行不行」；
/// 本工具恰恰相反——<b>完整走生产代码路径</b>，用于定位「生产管道在第几秒、哪一环出问题」。</para>
/// <para>相比 <c>dotnet test</c> 的关键优势：</para>
/// <list type="number">
///   <item>测试工程注入的是 <c>NullLoggerFactory</c>，生产代码里所有 <c>LogWarning</c>（含「批量提交跳过单帧（背压超时）」）
///     全部进黑洞；本工具挂真实 Console Logger，这些告警<b>直接可见</b>。</item>
///   <item>testhost.exe 是无窗口进程，音频会话归属与前台判定都受其影响；本工具是带控制台窗口的普通进程，环境干扰更少。</item>
///   <item>逐窗口打印提交速率，能精确指出「第几秒开始不再供给」，而非只看最终断言的一个总数。</item>
/// </list>
/// <para>用法：</para>
/// <code>
/// dotnet run --project src\Tools\MediaPlaybackProbe
/// dotnet run --project src\Tools\MediaPlaybackProbe -- -v            // Debug 级日志（含格式协商、时钟等细节）
/// dotnet run --project src\Tools\MediaPlaybackProbe -- --category    // 启用 IAudioClient2 会话分类做对照
/// dotnet run --project src\Tools\MediaPlaybackProbe -- --file "D:\x.mp4"
/// </code>
/// </remarks>
internal static class Program
{
    /// <summary>指标采样窗口。</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

    private static async Task<int> Main(string[] args)
    {
        bool verbose = args.Contains("-v") || args.Contains("--verbose");
        bool enableCategory = args.Contains("--category");
        bool noDump = args.Contains("--no-dump");
        string? file = ParseOption(args, "--file") ?? ResolveDefaultMedia();
        string dumpPath = ParseOption(args, "--dump")
                          ?? Path.Combine(AppContext.BaseDirectory, "capture.wav");

        Console.WriteLine("=== LingFan.Media 最小播放验证（生产链路） ===");
        if (file is null || !File.Exists(file))
        {
            Console.WriteLine($"找不到媒体文件：{file ?? "(null)"}");
            Console.WriteLine("请用 --file <路径> 指定。");
            return 2;
        }
        Console.WriteLine($"媒体文件      : {file}");
        Console.WriteLine($"会话分类      : {(enableCategory ? "启用 (IAudioClient2.SetClientProperties)" : "禁用（默认）")}");
        Console.WriteLine($"视频渲染      : NoOp 无头渲染器（隔离音频，排除 GPU/上屏干扰）");
        Console.WriteLine($"日志级别      : {(verbose ? "Debug" : "Information")}");
        Console.WriteLine($"PCM 落盘      : {(noDump ? "关闭 (--no-dump)" : dumpPath)}");
        Console.WriteLine();

        var services = new ServiceCollection();
        // 关键差异：挂真实 Console Logger。测试工程用 NullLoggerFactory，
        // 生产代码的背压超时告警等诊断信息全部不可见，故此处挂真实 Console Logger 使其可见。
        services.AddLogging(b => b
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "[HH:mm:ss.fff] ";
            })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        // IVideoRendererFactory 必须显式注册：AddLingFanMedia() 只注册契约与工厂骨架，
        // 具体渲染器由 AddD3D11Renderer()/AddHeadlessRenderer() 等模块提供，缺失会在解析
        // IMediaPlayerFactory 时抛 "No service for type 'IVideoRendererFactory'"。
        // 本工具选 NoOp 无头渲染器：视频帧被丢弃、不建 GPU 设备，把变量收敛到音频链路
        // （与 HeadfulPlaybackEndToEndTests 的「纯音频」用例同款组合：AddHeadlessRenderer + AddWasapiOutput）。
        services.AddLingFanMedia()
                .AddMediaFoundation()
                .AddHeadlessRenderer()
                .AddWasapiOutput(o => o.EnableBackgroundCapableSession = enableCategory);

        await using var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        // ---- 音频提交观测量 ----
        long submittedSamples = 0;      // 累计帧数（每声道采样数）
        long audioCallbacks = 0;        // 回调次数
        int sampleRate = 0;             // 由首帧上报
        double firstAudioWallSec = -1;  // 首次收到音频的墙钟时刻
        TimeSpan firstAudioTimestamp = TimeSpan.MinValue;
        TimeSpan lastAudioTimestamp = TimeSpan.MinValue;

        // ---- 音频「内容」观测量（区分「有数据流过」与「数据里真有声音」）----
        // 存在意义：提交计数只能证明「有字节流过」，证明不了「字节里有声音」。
        // 逐窗口 RMS/峰值 + 原样落盘 WAV，可一次性定责：
        //   WAV 完整有声 ⇒ 解码链路无辜，问题在 WASAPI 渲染侧；
        //   WAV 后半段静音 ⇒ 责任在解码/源文件，与 WASAPI 无关。
        var dumpLock = new object();
        var dumpBuffer = noDump ? null : new MemoryStream(1 << 23);   // 8MB 起，纯内存零 IO 干扰
        var dumpFormat = SampleFormat.S16;
        int dumpChannels = 2;
        double winSumSq = 0;      // 本窗口归一化平方和
        long winSampleCount = 0;  // 本窗口样本数（含声道）
        double winPeak = 0;       // 本窗口峰值（归一化）

        var sw = new Stopwatch();
        player.AudioDataAvailable += f =>
        {
            Interlocked.Add(ref submittedSamples, f.FrameCount);
            Interlocked.Increment(ref audioCallbacks);
            if (sampleRate == 0) sampleRate = f.SampleRate;
            if (firstAudioWallSec < 0)
            {
                firstAudioWallSec = sw.Elapsed.TotalSeconds;
                firstAudioTimestamp = f.Timestamp;
            }
            lastAudioTimestamp = f.Timestamp;

            int bps = BytesPerSample(f.SampleFormat);
            int byteLen = f.FrameCount * f.Channels * bps;
            if (byteLen <= 0 || f.Data.Length < byteLen) return;
            var span = f.Data.Span[..byteLen];

            // 电平统计（归一化到 ±1.0，与格式无关）
            double sumSq = 0, peak = 0;
            int n;
            switch (f.SampleFormat)
            {
                case SampleFormat.S16:
                {
                    var s = MemoryMarshal.Cast<byte, short>(span);
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
                    var s = MemoryMarshal.Cast<byte, float>(span);
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
                    var s = MemoryMarshal.Cast<byte, int>(span);
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

            lock (dumpLock)
            {
                winSumSq += sumSq;
                winSampleCount += n;
                if (peak > winPeak) winPeak = peak;
                dumpFormat = f.SampleFormat;
                dumpChannels = f.Channels;
                dumpBuffer?.Write(span);
            }
        };

        try
        {
            var source = new FileMediaSource(file);
            sw.Start();
            await player.OpenAsync(source, CancellationToken.None);
            double openSec = sw.Elapsed.TotalSeconds;

            var duration = player.Duration;
            Console.WriteLine();
            Console.WriteLine($"OpenAsync 耗时: {openSec:F2}s   Duration={duration:g}   " +
                              $"AudioTracks={player.Session?.AudioTracks.Count ?? 0}");

            if (duration <= TimeSpan.Zero)
            {
                Console.WriteLine("Duration 为 0，后端未查到容器时长，后续判定不可靠。");
                duration = TimeSpan.FromSeconds(40);
            }

            await player.PlayAsync();
            Console.WriteLine();
            Console.WriteLine("  t(s)    pos(s)   submitted(s)  rate   cb/s   dBFS   peak   state        备注");
            Console.WriteLine("  ------  -------  ------------  -----  -----  -----  -----  -----------  --------------------");

            long prevSamples = 0, prevCallbacks = 0;
            double prevT = 0;
            int stallWindows = 0;
            double stallStartSec = -1;
            double maxSubmittedSec = 0;
            int silentWindows = 0;
            double firstSilentSec = -1;

            // 播到 EOF（留 3s 冗余）或状态变为 Stopped
            double limitSec = duration.TotalSeconds + 3.0;
            while (sw.Elapsed.TotalSeconds < limitSec && player.State != MediaState.Stopped)
            {
                await Task.Delay(SampleInterval);

                double t = sw.Elapsed.TotalSeconds;
                long curSamples = Interlocked.Read(ref submittedSamples);
                long curCallbacks = Interlocked.Read(ref audioCallbacks);
                int sr = sampleRate > 0 ? sampleRate : 44100;

                double subSec = curSamples / (double)sr;
                double dt = t - prevT;
                // rate：本窗口提交的音频时长 / 墙钟时长。1.00 = 恰好实时供给；0.00 = 完全停供。
                double rate = dt > 0 ? (curSamples - prevSamples) / (double)sr / dt : 0;
                double cbPerSec = dt > 0 ? (curCallbacks - prevCallbacks) / dt : 0;

                maxSubmittedSec = Math.Max(maxSubmittedSec, subSec);

                // 取本窗口电平并重置
                double wSumSq, wPeak; long wCount;
                lock (dumpLock)
                {
                    wSumSq = winSumSq; wCount = winSampleCount; wPeak = winPeak;
                    winSumSq = 0; winSampleCount = 0; winPeak = 0;
                }
                double rms = wCount > 0 ? Math.Sqrt(wSumSq / wCount) : 0;
                double dbfs = rms > 1e-9 ? 20 * Math.Log10(rms) : double.NegativeInfinity;
                string dbfsStr = double.IsNegativeInfinity(dbfs) ? " -inf" : $"{dbfs,5:F1}";

                string note = "";
                if (rate < 0.05 && subSec > 0.1)
                {
                    stallWindows++;
                    if (stallWindows == 3) { stallStartSec = t; note = "← 停供起点"; }
                    else if (stallWindows > 3) note = $"停供 {stallWindows * SampleInterval.TotalSeconds:F1}s";
                }
                else if (stallWindows >= 3)
                {
                    note = "← 恢复供给";
                    stallWindows = 0;
                }
                else stallWindows = 0;

                // 有数据流过但电平近乎为零 = 「静音数据」，与「停供」是两回事，必须分开标注
                if (wCount > 0 && dbfs < -80)
                {
                    silentWindows++;
                    if (silentWindows == 3) { firstSilentSec = t; note += " ← 静音数据起点"; }
                }
                else if (wCount > 0) silentWindows = 0;

                Console.WriteLine($"  {t,6:F1}  {player.Position.TotalSeconds,7:F2}  {subSec,12:F2}  " +
                                  $"{rate,5:F2}  {cbPerSec,5:F1}  {dbfsStr}  {wPeak,5:F3}  " +
                                  $"{player.State,-11}  {note}");

                prevSamples = curSamples; prevCallbacks = curCallbacks; prevT = t;
            }

            double totalWall = sw.Elapsed.TotalSeconds;
            await player.StopAsync(CancellationToken.None);

            // ---- 汇总判定 ----
            int srFinal = sampleRate > 0 ? sampleRate : 44100;
            double finalSubSec = Interlocked.Read(ref submittedSamples) / (double)srFinal;

            Console.WriteLine();
            Console.WriteLine("=== 汇总 ===");
            Console.WriteLine($"  容器时长          : {duration.TotalSeconds:F2}s");
            Console.WriteLine($"  墙钟总耗时        : {totalWall:F2}s");
            Console.WriteLine($"  最终播放位置      : {player.Position.TotalSeconds:F2}s");
            Console.WriteLine($"  音频采样率        : {srFinal}Hz");
            Console.WriteLine($"  累计提交音频时长  : {finalSubSec:F2}s  ({finalSubSec / duration.TotalSeconds * 100:F1}% of duration)");
            Console.WriteLine($"  音频回调次数      : {Interlocked.Read(ref audioCallbacks)}");
            Console.WriteLine($"  首次收到音频      : 墙钟 {firstAudioWallSec:F2}s，帧时间戳 {FormatTs(firstAudioTimestamp)}");
            Console.WriteLine($"  末次音频帧时间戳  : {FormatTs(lastAudioTimestamp)}");
            if (stallStartSec > 0)
                Console.WriteLine($"  首个停供起点    : 墙钟 {stallStartSec:F2}s");
            if (firstSilentSec > 0)
                Console.WriteLine($"  静音数据起点    : 墙钟 {firstSilentSec:F2}s（有字节流过但电平 < -80dBFS）");

            // ---- 原样落盘：把链路里真实流过的 PCM 写成 WAV ----
            if (dumpBuffer is not null)
            {
                byte[] pcm;
                lock (dumpLock) { pcm = dumpBuffer.ToArray(); }
                try
                {
                    WriteWav(dumpPath, pcm, srFinal, dumpChannels, dumpFormat);
                    Console.WriteLine($"  PCM 已落盘        : {dumpPath}");
                    Console.WriteLine($"                      {pcm.Length:N0} 字节, {dumpFormat}, {dumpChannels}ch, {srFinal}Hz, " +
                                      $"时长 {pcm.Length / (double)(srFinal * dumpChannels * BytesPerSample(dumpFormat)):F2}s");
                }
                catch (Exception wex)
                {
                    Console.WriteLine($"  WAV 落盘失败    : {wex.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== 判定 ===");
            if (firstAudioWallSec > 2.0)
                Console.WriteLine($"  出声延迟异常：首帧音频在墙钟 {firstAudioWallSec:F2}s 才提交" +
                                  $"（正常应 <1s）。若帧时间戳同时约等于 0，说明是「供给侧启动阻塞」而非 seek 定位问题。");
            if (finalSubSec < duration.TotalSeconds - 2.0)
                Console.WriteLine($"  音频供给不完整：{finalSubSec:F2}s / {duration.TotalSeconds:F2}s。" +
                                  (stallStartSec > 0 ? $"自墙钟 {stallStartSec:F2}s 起停供。" : ""));
            else
                Console.WriteLine("  音频供给覆盖完整时长。");
            Console.WriteLine("  （下方 [WASAPI-DIAG] 由生产代码在 Dispose 时输出，含 droppedFrames——");
            Console.WriteLine("    若 droppedFrames 明显 >0，即背压超时丢帧，说明设备停止消费或供给节奏错配。）");
            if (dumpBuffer is not null)
            {
                Console.WriteLine();
                Console.WriteLine("  下一步（决定性定责）：用系统播放器打开上面那个 capture.wav。");
                Console.WriteLine("     · WAV 完整有声，而实听只响十几秒 ⇒ 解码链路无辜，问题 100% 在 WASAPI 渲染侧；");
                Console.WriteLine("     · WAV 也只有十几秒有声 ⇒ 责任在解码/源文件，与 WASAPI 无关。");
            }
            Console.WriteLine();
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
            await player.DisposeAsync();   // 触发生产代码的 [WASAPI-DIAG] 汇总输出
            // SimpleConsole 在后台线程排队输出：留出时间让 [WASAPI-DIAG] 先落屏，避免与结束语错序。
            await Task.Delay(500);
        }

        Console.WriteLine("=== 诊断完成。把以上输出整段贴回即可定位。 ===");
        return 0;
    }

    private static string FormatTs(TimeSpan ts) =>
        ts == TimeSpan.MinValue ? "(无)" : $"{ts.TotalSeconds:F2}s";

    private static int BytesPerSample(SampleFormat f) => f switch
    {
        SampleFormat.S16 => 2,
        _ => 4
    };

    /// <summary>
    /// 把原始 PCM 写成标准 WAV。S16 用 WAVE_FORMAT_PCM(1)，F32/S32 用 IEEE_FLOAT(3)/PCM(1)。
    /// </summary>
    private static void WriteWav(string path, byte[] pcm, int sampleRate, int channels, SampleFormat fmt)
    {
        int bps = BytesPerSample(fmt);
        int bits = bps * 8;
        short audioFormat = fmt == SampleFormat.F32 ? (short)3 : (short)1;   // 3 = IEEE float
        int blockAlign = channels * bps;
        int byteRate = sampleRate * blockAlign;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs, Encoding.ASCII);

        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                       // PCM fmt chunk 大小
        w.Write(audioFormat);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bits);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
    }

    private static string? ParseOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>优先用输出目录下随工程复制的 Resources\Video\m1.mp4。</summary>
    private static string? ResolveDefaultMedia()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "Resources", "Video", "m1.mp4");
        if (File.Exists(local)) return local;

        // 回退：从当前目录向上找仓库根的 Resources
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Resources", "Video", "m1.mp4");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

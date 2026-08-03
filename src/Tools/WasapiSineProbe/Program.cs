using System.Diagnostics;
using LingFan.Media.Abstractions;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WasapiSineProbe;

/// <summary>
/// 最小可验证程序 <b>b1</b>：<b>独立 WASAPI 出声</b>（本机生成 PCM，<b>完全不含解码</b>）。
/// </summary>
/// <remarks>
/// <para>对应单元测试 <c>StandaloneWasapiPlaybackTests.PlayAsync_StandaloneWasapi_SineTone_Sustained</c>，
/// 但改造为带控制台窗口的独立真实进程 + 真实 Console Logger + 逐窗口指标。</para>
///
/// <para><b>存在意义（排除力）</b>：本工程<b>只引用 Extensions + Outputs</b>，从工程引用层面就不可能牵扯
/// MediaFoundation 解码。因此：</para>
/// <list type="bullet">
///   <item>本程序<b>出声正常</b> ⇒ 设备枚举 / 格式协商 / IAudioClient.Start / 实时 Submit 背压 / 播放时钟
///     这条 WASAPI 渲染链路整体健康，后续任何「听不到」都不该再赖 WASAPI 基础设施。</item>
///   <item>本程序<b>就断音</b> ⇒ 与解码、MF、时钟同步全部无关，责任 100% 在 WASAPI 渲染侧或本机 driver。</item>
/// </list>
///
/// <para><b>相对测试的关键增强</b>：</para>
/// <list type="number">
///   <item><c>--sweep</c> 扫频模式：定频正弦断掉时人耳容易怀疑是错觉，扫频（200Hz→2kHz 循环上滑）
///     一旦中断立刻可辨，专治「十几秒就停」这类主观判断。</item>
///   <item>逐 500ms 打印 played / submitted / 两者滞后差 / 设备延迟，能指出「第几秒开始不推进」。</item>
///   <item>挂真实 Console Logger（测试是 <c>NullLoggerFactory</c>，生产告警全进黑洞）。</item>
/// </list>
///
/// <para>用法：</para>
/// <code>
/// dotnet run --project src\Tools\WasapiSineProbe
/// dotnet run --project src\Tools\WasapiSineProbe -- --sweep --seconds 20   // 长时扫频，验证是否十几秒断
/// dotnet run --project src\Tools\WasapiSineProbe -- --f32                  // 改用 F32 提交（另一条格式协商路径）
/// dotnet run --project src\Tools\WasapiSineProbe -- --category             // 启用 IAudioClient2 会话分类做对照
/// dotnet run --project src\Tools\WasapiSineProbe -- --rate 48000 -v
/// </code>
/// </remarks>
internal static class Program
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>每次 Submit 的帧数（每声道采样数）。远小于 WASAPI 端点缓冲，避免单帧撑爆。</summary>
    private const int FramesPerSubmit = 1024;

    private static async Task<int> Main(string[] args)
    {
        bool verbose = args.Contains("-v") || args.Contains("--verbose");
        bool enableCategory = args.Contains("--category");
        bool sweep = args.Contains("--sweep");
        bool useF32 = args.Contains("--f32");
        int seconds = ParseInt(args, "--seconds", 6);
        int sampleRate = ParseInt(args, "--rate", 44100);
        int channels = ParseInt(args, "--channels", 2);
        double freq = ParseDouble(args, "--freq", 440.0);
        double amplitude = ParseDouble(args, "--amp", 0.3);
        var format = useF32 ? SampleFormat.F32 : SampleFormat.S16;

        Console.WriteLine("=== b1 · 独立 WASAPI 出声验证（本机生成 PCM，不含解码） ===");
        Console.WriteLine($"波形          : {(sweep ? "扫频 200Hz→2000Hz 循环上滑（断音极易辨别）" : $"定频正弦 {freq:F0}Hz")}");
        Console.WriteLine($"时长          : {seconds}s   振幅 {amplitude:F2}");
        Console.WriteLine($"提交格式      : {format} {sampleRate}Hz {channels}ch");
        Console.WriteLine($"会话分类      : {(enableCategory ? "启用 (IAudioClient2.SetClientProperties)" : "禁用（默认）")}");
        Console.WriteLine($"日志级别      : {(verbose ? "Debug" : "Information")}");
        Console.WriteLine();
        Console.WriteLine("⚠ 请把音量开到能听清，全程留意声音是否<b>持续不断</b>。");
        Console.WriteLine();

        var services = new ServiceCollection();
        // 🔴 真实 Console Logger：测试工程注入 NullLoggerFactory，生产代码所有 LogWarning
        //    （含背压超时、格式回退）全部不可见，这是此前多轮误判的直接成因。
        services.AddLogging(b => b
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "[HH:mm:ss.fff] ";
            })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        // 只组装音频输出：不注册 backend / renderer，也就不解析 IMediaPlayer，
        // 因此不会触发「IVideoRendererFactory 未注册」——本探针只取 IAudioOutputFactory。
        services.AddLingFanMedia()
                .AddWasapiOutput(o => o.EnableBackgroundCapableSession = enableCategory);

        await using var sp = services.BuildServiceProvider();

        // 走公开契约 IAudioOutputFactory.Create()，而非测试里的 new WasapiOutput(...)。
        // WasapiOutput 是 internal（仅 InternalsVisibleTo 测试工程），最小程序不应也不需要破例。
        var factory = sp.GetRequiredService<IAudioOutputFactory>();
        var output = factory.Create();

        try
        {
            await output.InitializeAsync(CancellationToken.None);
            output.Initialize(sampleRate, channels);   // 同步 COM 边界：设备枚举 + 格式协商
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] WASAPI 初始化失败：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("        （无音频端点 / 设备不支持请求格式。可试 --rate 48000 或 --f32。）");
            output.Dispose();
            return 2;
        }

        Console.WriteLine($"WASAPI 已初始化。设备延迟 = {output.Latency.TotalMilliseconds:F1}ms");
        Console.WriteLine();

        var frames = BuildTone(seconds, sampleRate, channels, format, freq, amplitude, sweep);
        double totalSec = frames.Count * FramesPerSubmit / (double)sampleRate;
        Console.WriteLine($"已生成 {frames.Count} 个提交帧，合计 {totalSec:F2}s PCM。开始播放……");
        Console.WriteLine();

        // 🔴 IAudioClient.Start 只在 Resume() 内触发——必须先启动设备，否则提交进去也不出声。
        output.Resume();

        var sw = Stopwatch.StartNew();
        long submittedFrames = 0;
        Exception? submitError = null;

        // 后台持续提交：Submit 在缓冲满时阻塞（COM 背压），天然形成实时 pacing。
        var submitTask = Task.Run(() =>
        {
            try
            {
                foreach (var f in frames)
                {
                    output.Submit(f);
                    Interlocked.Add(ref submittedFrames, f.FrameCount);
                }
            }
            catch (Exception ex) { submitError = ex; }
        });

        Console.WriteLine("  t(s)  played(s)  submitted(s)   lag(s)  latency(ms)  备注");
        Console.WriteLine("  ----  ---------  ------------  -------  -----------  ----------------");

        double maxPos = 0, prevPos = -1, stallStartSec = -1;
        var deadline = TimeSpan.FromSeconds(seconds + 5);   // 播完再多等 5s 让尾巴排空
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(SampleInterval);

            double t = sw.Elapsed.TotalSeconds;
            double pos = output.GetPlaybackPosition().TotalSeconds;
            double sub = Interlocked.Read(ref submittedFrames) / (double)sampleRate;
            if (pos > maxPos) maxPos = pos;

            // 停顿判据：位置在一个采样窗口内完全没推进，且还没提交完 → 真卡住
            string note = "";
            bool advanced = pos > prevPos + 0.05;
            if (!advanced && sub < totalSec - 0.1)
            {
                note = "⚠ 位置未推进";
                if (stallStartSec < 0) stallStartSec = t;
            }
            prevPos = pos;

            Console.WriteLine($"  {t,4:F1}  {pos,9:F2}  {sub,12:F2}  {sub - pos,7:F2}  " +
                              $"{output.Latency.TotalMilliseconds,11:F1}  {note}");

            if (submitTask.IsCompleted && pos >= totalSec - 0.15) break;
        }

        await submitTask;
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine("=== 汇总 ===");
        Console.WriteLine($"  墙钟总耗时      : {sw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  PCM 总时长      : {totalSec:F2}s");
        Console.WriteLine($"  实际提交        : {Interlocked.Read(ref submittedFrames) / (double)sampleRate:F2}s");
        Console.WriteLine($"  最大播放位置    : {maxPos:F2}s");
        if (stallStartSec > 0)
            Console.WriteLine($"  ⚠ 首个停顿起点  : 墙钟 {stallStartSec:F2}s");
        if (submitError is not null)
            Console.WriteLine($"  ⚠ 提交异常      : {submitError.GetType().Name}: {submitError.Message}");

        Console.WriteLine();
        Console.WriteLine("=== 判定 ===");
        // 期望：播放位置推进到 PCM 总时长的 80% 以上（留出尾部缓冲与时钟粒度余量）
        double expect = totalSec * 0.8;
        bool pass = maxPos >= expect && submitError is null;
        if (pass)
        {
            Console.WriteLine($"  ✓ 播放位置推进 {maxPos:F2}s ≥ 期望 {expect:F2}s（设备枚举/格式协商/Start/背压/时钟均正常）");
            Console.WriteLine("  ✓ WASAPI 渲染链路本身健康 ⇒ 后续「听不到」不应再归咎于 WASAPI 基础设施。");
        }
        else
        {
            Console.WriteLine($"  ✗ 播放位置只推进 {maxPos:F2}s，低于期望 {expect:F2}s。");
            Console.WriteLine("  ✗ 本探针不含任何解码 ⇒ 责任 100% 在 WASAPI 渲染侧或本机 driver。");
        }
        Console.WriteLine();
        Console.WriteLine("  🔴 人耳判定同样重要：声音若在中途停顿/变哑，即便上面数字达标也应记录停顿时刻。");

        output.Dispose();          // 停设备 + 释放 COM（渲染线程 Shutdown），触发生产代码的 [WASAPI-DIAG]
        await Task.Delay(500);     // 让 SimpleConsole 后台线程把 [WASAPI-DIAG] 落屏

        Console.WriteLine();
        Console.WriteLine("=== b1 完成。把以上输出整段贴回即可。 ===");
        return pass ? 0 : 1;
    }

    /// <summary>
    /// 生成测试音，切成 <see cref="FramesPerSubmit"/> 大小的 <see cref="AudioFrame"/> 列表。
    /// 相位<b>连续累加</b>（而非按绝对时间重算），保证扫频时不产生咔哒声。
    /// </summary>
    private static List<AudioFrame> BuildTone(
        int seconds, int sampleRate, int channels, SampleFormat format,
        double freq, double amplitude, bool sweep)
    {
        int totalFrames = sampleRate * seconds;
        int bps = format == SampleFormat.S16 ? 2 : 4;
        var list = new List<AudioFrame>((totalFrames / FramesPerSubmit) + 1);

        double phase = 0;                    // 累积相位（弧度），保证跨帧连续
        const double sweepLow = 200.0, sweepHigh = 2000.0, sweepPeriod = 2.0;  // 2 秒扫一轮

        for (int off = 0; off < totalFrames; off += FramesPerSubmit)
        {
            int n = Math.Min(FramesPerSubmit, totalFrames - off);
            var data = new byte[n * channels * bps];
            var span = data.AsSpan();

            for (int i = 0; i < n; i++)
            {
                double t = (off + i) / (double)sampleRate;
                // 扫频：在 [low, high] 间做指数上滑，听感更均匀
                double f = sweep
                    ? sweepLow * Math.Pow(sweepHigh / sweepLow, t % sweepPeriod / sweepPeriod)
                    : freq;
                phase += 2 * Math.PI * f / sampleRate;
                if (phase > 2 * Math.PI) phase -= 2 * Math.PI;

                double v = Math.Sin(phase) * amplitude;
                for (int c = 0; c < channels; c++)
                {
                    int o = (i * channels + c) * bps;
                    if (format == SampleFormat.S16)
                        BitConverter.TryWriteBytes(span[o..], (short)(v * 32767));
                    else
                        BitConverter.TryWriteBytes(span[o..], (float)v);
                }
            }

            list.Add(new AudioFrame(
                data, sampleRate, channels, format,
                TimeSpan.FromSeconds(off / (double)sampleRate),
                TimeSpan.FromSeconds(n / (double)sampleRate), n));
        }
        return list;
    }

    private static string? ParseOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int ParseInt(string[] args, string name, int fallback) =>
        int.TryParse(ParseOption(args, name), out var v) ? v : fallback;

    private static double ParseDouble(string[] args, string name, double fallback) =>
        double.TryParse(ParseOption(args, name), out var v) ? v : fallback;
}

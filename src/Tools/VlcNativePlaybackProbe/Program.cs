using System.Diagnostics;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.VLCNative;
using LingFan.Media.Backends.VLCNative.Interop;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ─────────────────────────────────────────────────────────────────────────────
// VLCNative 集成验证探针
//
// 分六步递进，任一步失败都能直接定位环节：
//   ① 原生模块定位与加载（NativeLibrary.Load + SetDllImportResolver）
//   ② P/Invoke 解析与 Cdecl 调用约定（libvlc_get_version，无需引擎实例）
//   ③ libvlc_new(0, NULL)（引擎起来 = plugins 目录被 libvlc 自身推导成功）
//   ④ libvlc_new(argc, argv)（UTF-8 argv 手工封送的 ABI 正确性）
//   ⑤ VLCNativeBackend 构造（真实 VLCOptions args 路径 + 单例化生命周期）
//   ⑥ VLCNativeDemuxer 播放验证（地址式打开本地 m1.mp4，读帧计数）
//
// 要求：六步均成功，且 ⑥ 须取出视频帧≥1 或音频包≥1。
// ─────────────────────────────────────────────────────────────────────────────

var step = 0;

try
{
    step = 1;
    Console.WriteLine("[1/6] 定位并加载原生 libvlc ...");
    var version = LibVlcInstance.NativeVersion; // 触发 EnsureNativeLoaded
    Console.WriteLine($"      模块路径 = {LibVlcInstance.NativeModulePath}");
    Console.WriteLine("      OK");

    step = 2;
    Console.WriteLine("[2/6] P/Invoke 解析 + Cdecl 调用约定（libvlc_get_version）...");
    if (string.IsNullOrWhiteSpace(version))
        throw new InvalidOperationException("libvlc_get_version 返回空字符串。");
    Console.WriteLine($"      version = {version}");
    Console.WriteLine("      OK");

    step = 3;
    Console.WriteLine("[3/6] libvlc_new(0, NULL) ...");
    var sw = Stopwatch.StartNew();
    using (var plain = new LibVlcInstance())
    {
        sw.Stop();
        Console.WriteLine($"      handle = 0x{plain.Handle:X}  耗时 {sw.ElapsedMilliseconds} ms");
    }

    Console.WriteLine("      OK（引擎创建并释放成功 = plugins 目录已被 libvlc 自身推导到）");

    step = 4;
    // 与旧 VLCBackend 一致的启动选项，验证 const char* const* 手工封送
    string[] options =
    [
        "--vout=dummy",
        "--no-video-title-show",
        "--no-snapshot-preview",
        "--avcodec-hw=any"
    ];
    Console.WriteLine($"[4/6] libvlc_new(argc={options.Length}, argv) ...");
    sw.Restart();
    using (var withArgs = new LibVlcInstance(options))
    {
        sw.Stop();
        Console.WriteLine($"      handle = 0x{withArgs.Handle:X}  耗时 {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"      options = {string.Join(' ', options)}");
    }

    Console.WriteLine("      OK（UTF-8 argv 封送 ABI 正确）");

    step = 5;
    // 构造 VLCNativeBackend，确认真实 VLCOptions → args 注入路径与单例化生命周期。
    // 默认 VLCOptions：Headless=true → --vout=dummy，EnableHardwareDecoding=true → --avcodec-hw=any。
    Console.WriteLine("[5/6] VLCNativeBackend 构造（真实 VLCOptions args 路径）...");
    var backendOptions = new VLCOptions();
    using (var backend = new VLCNativeBackend(new NullLogger<VLCNativeBackend>(), backendOptions))
    {
        Console.WriteLine($"      backend 引擎版本 = {backend.Version}");
        Console.WriteLine($"      Headless={backendOptions.Headless} EnableHw={backendOptions.EnableHardwareDecoding}");
    }
    Console.WriteLine("      OK（backend 单例化 + args 注入成功）");

    step = 6;
    // 经 DI 构造 VLCNativeDemuxer，地址式打开本地样本，确认真实取帧（视频帧 + 音频包）。
    Console.WriteLine("[6/6] VLCNativeDemuxer 播放验证（地址式打开本地 m1.mp4，读帧计数）...");
    var mediaFile = Path.Combine(AppContext.BaseDirectory, "m1.mp4");
    if (!File.Exists(mediaFile))
        throw new FileNotFoundException("样本视频未复制到输出目录（检查 csproj 的 Content 包含）", mediaFile);

    var services = new ServiceCollection();
    // 日志基础设施由宿主注册，库扩展（AddVLCNative）不自行 AddLogging()
    //    （与 AddVLC / AddFFmpeg / AddMediaFoundation 同构，见 MediaOptions.cs:10）。
    //    探针是诊断型 exe（宿主），此处挂 Console provider 把库内 ILogger 诊断（含 codec 原始值）打到控制台。
    services.AddLogging(builder => builder.AddConsole());
    services.AddVLCNative();
    var provider = services.BuildServiceProvider();
    var demuxerFactory = provider.GetRequiredService<IMediaDemuxerFactory>();

    var fileStream = new FileMediaStream(new FileMediaSource(mediaFile));
    using (var demuxer = demuxerFactory.Create(fileStream))
    {
        await demuxer.OpenAsync(fileStream, CancellationToken.None);

        Console.WriteLine($"      轨道数 = {demuxer.Tracks.Count}；时长 = {demuxer.Metadata.Duration:g}");
        foreach (var t in demuxer.Tracks)
        {
            var codec = t.Type switch
            {
                TrackType.Video => t.VideoCodec.ToString(),
                TrackType.Audio => t.AudioCodec.ToString(),
                TrackType.Subtitle => t.SubtitleCodec.ToString(),
                _ => "?"
            };
            Console.WriteLine($"        - #{t.Index} {t.Type} codec={codec}");
        }

        // 在 6 秒窗口内读取 VLC 实时交付的帧（VLC 按媒体时钟限流，非灌帧）
        int videoFrames = 0, audioPackets = 0, other = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            while (true)
            {
                var p = await demuxer.ReadPacketAsync(cts.Token);
                if (p == null) break;
                var track = demuxer.Tracks.FirstOrDefault(t => t.Index == p.TrackIndex);
                if (track?.Type == TrackType.Video) videoFrames++;
                else if (track?.Type == TrackType.Audio) audioPackets++;
                else other++;
                p.Dispose();
            }
        }
        catch (OperationCanceledException) { /* 6s 窗口到，停止计数 */ }

        Console.WriteLine($"      6s 窗口内：视频帧 {videoFrames}，音频包 {audioPackets}，其它 {other}");
        demuxer.Close();

        // 本地样本 m1.mp4 为「视频+音频」文件，须同时取出视频帧≥1 与音频包≥1。
        // 视频帧为 0 须核查播放前 tracks_get 是否漏列视频轨（须播放后重取）与视频回调注册；
        // 音频包为 0 须核查 :amem-format=s16l 与音频回调注册。
        if (videoFrames == 0)
            throw new InvalidOperationException(
                $"demuxer 未取出视频帧（视频帧 0，音频包 {audioPackets}）；轨道数={demuxer.Tracks.Count}。" +
                "视频回调未触发：检查播放前 tracks_get 是否漏列视频轨（须播放后重取）与视频回调注册。");
        if (audioPackets == 0)
            throw new InvalidOperationException(
                $"demuxer 未取出音频包（视频帧 {videoFrames}，音频包 0）；轨道数={demuxer.Tracks.Count}。" +
                "音频回调未触发：检查 :amem-format=s16l 与音频回调注册。");

        Console.WriteLine($"      OK（demuxer 经自写 P/Invoke 取出 视频帧 {videoFrames} + 音频包 {audioPackets}）");
    }

    Console.WriteLine();
    Console.WriteLine("VLCNative 集成验证全部通过：自写 Apache-2.0 P/Invoke 可独立驱动 libvlc，VLCNativeBackend + VLCNativeDemuxer 均可用。");
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"第 {step} 步失败：{ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(ex.ToString());
    Environment.Exit(1);
}

// 探针自带的极简 ILogger<T> 实现（避免依赖框架 NullLogger 的包解析细节）。
file sealed class NullLogger<T> : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

file sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();
    public void Dispose() { }
}

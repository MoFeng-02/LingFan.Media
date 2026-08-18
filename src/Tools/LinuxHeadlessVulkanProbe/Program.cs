using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Renderers.Shared;
using LingFan.Media.Renderers.Vulkan;
using LingFan.Media.Platforms.Linux;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinuxHeadlessVulkanProbe;

/// <summary>
/// Linux/WSL2 无头 Vulkan 渲染探针（Phase 2b）：在 Xvfb 虚拟显示提供的 X11 窗口上，
/// 用 VulkanRenderer(X11 Surface) 真上屏 present，统计 vulkanPresentCount / GPU 纹理帧 / CPU 帧 / 丢帧。
/// 证明「ffmpeg 解码 → Vulkan 渲染 present」在 Linux 无头环境（无物理显示器、非 WSLg GUI）可闭环。
/// 注意：渲染器 Linux Attach 要求 X11WindowHandle（窗口表面）；Xvfb 提供虚拟 X 显示，不是 WSLg。
/// </summary>
internal static class Program
{
    /// <summary>包 X11WindowHandle 的最小 IRenderTarget（HandleType=Pointer，NativeHandle=X11WindowHandle）。</summary>
    private sealed class X11RenderTarget : IRenderTarget
    {
        private readonly X11WindowHandle _handle;
        private readonly int _w, _h;
        public X11RenderTarget(X11WindowHandle handle, int w, int h) { _handle = handle; _w = w; _h = h; }
        public RenderTargetType Type => RenderTargetType.Window;
        public RenderHandleType HandleType => RenderHandleType.Pointer;
        public object NativeHandle => _handle;
        public int Width => _w;
        public int Height => _h;
        public float Scale => 1.0f;
    }

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("本探针仅适用于 Linux/WSL2（依赖 libX11 + Vulkan X11 Surface）。");
            return 1;
        }

        string? file = ParseOption(args, "--file") ?? "Resources/Video/m1.mp4";
        bool verbose = args.Contains("-v") || args.Contains("--verbose");
        bool useHw = args.Contains("--hw");
        double seconds = ParseDouble(args, "--seconds") ?? 10.0;
        int winW = (int)(ParseDouble(args, "--window-w") ?? 1280);
        int winH = (int)(ParseDouble(args, "--window-h") ?? 720);
        int sample = (int)(ParseDouble(args, "--sample") ?? 25);
        // ffmpeg 原生库目录：优先 env LF_FFMPEG_LIB，否则应用目录（配合 LD_LIBRARY_PATH）。
        string ffmpegLib = Environment.GetEnvironmentVariable("LF_FFMPEG_LIB") ?? AppContext.BaseDirectory;

        Console.WriteLine("=== LingFan.Media Linux 无头 Vulkan 渲染探针（ffmpeg 解码 + Vulkan X11 present + 静音）===");
        if (!File.Exists(file))
        {
            Console.WriteLine($"找不到媒体文件：{file}（用 --file 指定绝对路径）");
            return 2;
        }
        Console.WriteLine($"媒体文件      : {file}");
        Console.WriteLine($"ffmpeg 库目录 : {ffmpegLib}");
        Console.WriteLine($"硬解/零拷贝   : {(useHw ? "请求(--hw)；VAAPI 真实零拷贝已启用（VA Surface → dma_buf → Vulkan 多平面），失败回落软解" : "关（软解软渲）")}");
        Console.WriteLine($"窗口尺寸      : {winW}x{winH}（Xvfb 虚拟显示，无物理输出）");
        Console.WriteLine($"显示 DISPLAY  : {Environment.GetEnvironmentVariable("DISPLAY") ?? "(未设，依赖 XOpenDisplay 默认)"}");
        Console.WriteLine();

        var services = new ServiceCollection();
        services.AddLogging(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        // 跨平台：ffmpeg 解码 + Vulkan 渲染器(X11 Surface) + 静音输出。
        services.AddLingFanMedia()
                .AddFFmpeg(o =>
                {
                    o.FFmpegLibraryPath = ffmpegLib;
                    o.HardwareAcceleration = useHw;
                })
                .AddVulkanRenderer()
                .AddSilentAudioOutput();
        // Linux 显式注册 VAAPI 零拷贝导出（IVaApiExport → VaApiInterop）；--hw 时启用真实零拷贝硬解。
        services.AddVaApi();

        await using var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        // —— 建立 X11 窗口（Xvfb 提供 Display） ——
        X11Native.XInitThreads();
        nint display = X11Native.XOpenDisplay(IntPtr.Zero);
        if (display == nint.Zero)
        {
            Console.WriteLine("[失败] XOpenDisplay 返回 NULL：请先启动 Xvfb（例如 `Xvfb :99 -screen 0 1280x720x24 &`）并设 DISPLAY=:99");
            return 4;
        }
        nint root = X11Native.XDefaultRootWindow(display);
        nint window = X11Native.XCreateSimpleWindow(display, root, 0, 0, (uint)winW, (uint)winH, 0, 0, 0);
        X11Native.XMapWindow(display, window);
        X11Native.XSync(display, 0);

        var factory = sp.GetRequiredService<VulkanRendererFactory>();
        IVideoRenderer renderer = factory.Create();
        renderer.Attach(new X11RenderTarget(new X11WindowHandle(display, window), winW, winH));

        long presentCount = 0, gpuServed = 0, cpuServed = 0;
        int sampleRate = 0; long audioCallbacks = 0, audioSamples = 0;

        player.VideoFrameAvailable += f =>
        {
            renderer.Present(f);
            Interlocked.Increment(ref presentCount);
            if (f.Resource is IGpuTextureResource) Interlocked.Increment(ref gpuServed);
            else Interlocked.Increment(ref cpuServed);
            if (presentCount % sample == 0)
            {
                string res = f.Resource is IGpuTextureResource ? "GPU" : "CPU";
                string path = f.Resource is IGpuTextureResource ? "零拷贝(硬渲)" : "CPU上传(软渲)";
                Console.WriteLine($"  [抽样#{presentCount}] t={f.Timestamp:g} {f.Width}x{f.Height} fmt={f.Format} 资源={res} 渲染路径={path}");
            }
        };
        player.AudioDataAvailable += f =>
        {
            Interlocked.Increment(ref audioCallbacks);
            Interlocked.Add(ref audioSamples, f.FrameCount);
            if (sampleRate == 0) sampleRate = f.SampleRate;
        };
        player.ErrorOccurred += (_, e) => Console.WriteLine($"[错误] {e.Message}");

        try
        {
            await player.OpenAsync(new FileMediaSource(file));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[失败] OpenAsync 异常：{ex.Message}");
            CleanupX11(display, window);
            return 3;
        }

        Console.WriteLine($"时长={player.Duration:g} 状态={player.State}");
        await player.PlayAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            while (!cts.IsCancellationRequested && player.State == MediaState.Playing)
            {
                await Task.Delay(500, cts.Token);
                Console.WriteLine($"  t={player.Position:g} present={presentCount} gpu={gpuServed} cpu={cpuServed} 音频回调={audioCallbacks}");
            }
        }
        catch (OperationCanceledException) { /* 时间到 */ }

        await player.StopAsync();
        await renderer.DisposeAsync();
        CleanupX11(display, window);

        Console.WriteLine();
        Console.WriteLine("=== 汇总 ===");
        Console.WriteLine($"Vulkan present 次数 : {presentCount}");
        Console.WriteLine($"GPU 纹理帧      : {gpuServed}   CPU 内存帧: {cpuServed}");
        Console.WriteLine($"判读            : 资源=GPU + 渲染路径=零拷贝 ⇒ 硬解GPU纹理直投Vulkan(零拷贝)；资源=CPU ⇒ 软解软渲；每 {sample} 帧抽一帧打印");
        Console.WriteLine($"音频回调        : {audioCallbacks}  采样数: {audioSamples}  采样率: {sampleRate}");
        Console.WriteLine($"丢帧            : {player.VideoDroppedFrames}");
        if (presentCount > 0)
            Console.WriteLine("判定            : Vulkan 在 Linux 无头(X11 Surface)成功 present——渲染闭环通");
        else
            Console.WriteLine("判定            : 异常——未见 Vulkan present，请检查上方 Vulkan/X11 日志与 Xvfb 是否运行");
        return 0;
    }

    private static void CleanupX11(nint display, nint window)
    {
        if (window != nint.Zero) X11Native.XDestroyWindow(display, window);
        if (display != nint.Zero) X11Native.XCloseDisplay(display);
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

/// <summary>X11 客户端 P/Invoke（仅 Linux 调用，Windows 上绝不执行）。顶级 partial 类以满足 [LibraryImport] 源生成。</summary>
internal static unsafe partial class X11Native
{
    private const string Lib = "libX11.so.6";

    [LibraryImport(Lib)]
    public static partial int XInitThreads();

    [LibraryImport(Lib)]
    public static partial nint XOpenDisplay(nint displayName); // displayName=IntPtr.Zero 取 $DISPLAY 默认

    [LibraryImport(Lib)]
    public static partial int XCloseDisplay(nint display);

    [LibraryImport(Lib)]
    public static partial nint XDefaultRootWindow(nint display);

    [LibraryImport(Lib)]
    public static partial nint XCreateSimpleWindow(
        nint display, nint parent, int x, int y,
        uint width, uint height, uint borderWidth, nuint border, nuint background);

    [LibraryImport(Lib)]
    public static partial int XMapWindow(nint display, nint window);

    [LibraryImport(Lib)]
    public static partial int XDestroyWindow(nint display, nint window);

    [LibraryImport(Lib)]
    public static partial int XSync(nint display, int discard);
}

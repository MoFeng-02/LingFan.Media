using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LingFan.Media.Abstractions;
using LingFan.Media.Avalonia;
using LingFan.Media.AvaloniaTools.ViewModels;
using LingFan.Media.AvaloniaTools.Views;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Backends.VLCNative;
using LingFan.Media.Backends.MediaCodec;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Outputs.OpenSLES;
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Renderers.Vulkan;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.AvaloniaTools;

public partial class App : Application
{
    /// <summary>全局 DI 容器。VideoView.Services 与 ViewModel 均从此解析。</summary>
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
#if DEBUG
        // 诊断期：WinExe 默认无控制台窗口，分配一个以便实时看到 DI/回退链日志。
        if (OperatingSystem.IsWindows())
            AttachDebugConsole();
#endif

        // ── 构建 DI：三后端（FFmpeg/VLC/MF）+ D3D11 渲染器 + WASAPI 音频 + Avalonia 控件 ──
        // Windows 专属扩展用 OperatingSystem.IsWindows 守卫，保证同一份代码在 Android/iOS 也能编译/运行。
        // VideoView 通过已注册的 IVideoRendererFactory 集合自动回退：
        //   D3D11RendererFactory（GPU 原生 SwapChain，Avalonia 控件内因需 Pointer/HWND 而失败）
        //   → SkiaVideoRendererFactory（末级兜底，软渲染，解码仍走 GPU）。
        var services = new ServiceCollection();
        var builder = services
            .AddLingFanMedia()
            .AddVulkanRenderer()
            .AddAvaloniaControls()
            .AddSkiaPresenter();

        if (OperatingSystem.IsWindows())
        {
            // MF（同步 MFT）+ D3D11 共享设备（FFmpeg D3D11VA 零拷贝 / MF DXVA）+ D3D11 渲染器 + WASAPI 音频。
            // AddD3D11Renderer 同时注册共享表面源工厂（ISharedGpuSurfaceSourceFactory，供无空域合成上屏使用）。
            // AddCompositionRenderer 注册 CompositionVideoRendererFactory：解码侧 GPU 纹理经共享 D3D11 纹理
            // 交 Avalonia 合成器直接导入、作为控件子视觉无空域上屏（不走 Skia CPU 回读），Skia 仍作末级兜底。
            builder
                //.AddMediaFoundation()
                .AddD3D11Renderer()
                // 无空域 GPU 合成上屏（CompositionVideoRenderer）：解码侧 GPU 纹理经共享 D3D11 纹理交 Avalonia
                // 合成器直接导入、作为控件子视觉无空域零拷贝上屏（不走 Skia CPU 回读）。
                // 已加固：① 挂载期导入自检（合成器无法跨设备导入即抛异常 → 回退 Skia）；② 运行期连续失败健康计数
                // （Unhealthy → VideoView 拉黑本工厂并重建回退链 → Skia 末级兜底）；③ 子视觉 Size=0 兜底。
                // 三项保障确保 Composition 永不静默空白：任何失败都干净落到 Skia。
                .AddCompositionRenderer()
                .AddWasapiOutput();
        }
        // 后端注册：Android 真机阶段暂只启用 D1（MediaCodec 平台原生后端）。
        // FFmpeg / VLC 在 Android 上无原生运行时打包（DllNotFoundException: avformat / libvlc），
        // 注册会白白消耗回退时间；桌面端如需再按需放开（与 MF 同模式注释）。
        //builder.AddFFmpeg(options => options.FFmpegLibraryPath = AppContext.BaseDirectory)
        //        .AddVLCNative();
        builder.AddMediaCodec();
        // composer 工厂需要 ILoggerFactory；独立运行的 App 手动 AddLogging 提供。

        // Android 真机：注册原生 OpenSL ES 音频输出（O4）。非 Android 调用会抛 PlatformNotSupportedException，
        // 故用 OperatingSystem.IsAndroid 守卫（与上方 Windows 守卫同构）。不注册则回落 NoOp 静音。
        if (OperatingSystem.IsAndroid())
            builder.AddOpenSlesOutput();
        services.AddLogging(options =>
        {
            options.AddConsole();
            options.SetMinimumLevel(LogLevel.Information);
        });


        // ViewModel 经 DI 解析，构造函数注入 IServiceProvider。
        services.AddTransient<MainViewModel>();

        // 内置样例提供者：跨平台通用（AvaloniaResource 嵌入 Assets/sample.mp4，各平台共用同一实现）。
        services.AddSingleton<IBundledSampleProvider, BundledSampleProvider>();

        Services = builder.Services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>()
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = Services.GetRequiredService<MainViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();

        // 启动后预热：吸收 WASAPI audiodg 冷启动，避免首次播放卡顿。
        // 纯优化，失败一律降级为未预热，绝不影响启动与播放。
        _ = Task.Run(() => PreheatAsync(Services!));
    }

#if DEBUG
    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    /// <summary>诊断期（DEBUG）：为 WinExe 分配一个控制台窗口，使 AddConsole 日志可见。失败静默忽略。</summary>
    private static void AttachDebugConsole()
    {
        try
        {
            _ = AllocConsole();
        }
        catch
        {
            // 诊断辅助：分配失败不影响正常运行。
        }
    }
#endif

    /// <summary>
    /// 启动预热。跨平台安全：非 Windows 主机不注册 WASAPI，解析到 null 直接跳过。
    /// 仅预热音频引擎（无需样例文件）；MF 解码器预热需样例媒体，留待后续按需接入。
    /// </summary>
    private static async Task PreheatAsync(IServiceProvider sp)
    {
        try
        {
            var audio = sp.GetService<IAudioEngine>();
            if (audio is not null)
            {
                await audio.WarmupAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // 预热失败：降级为未预热，不影响播放。
        }
    }
}

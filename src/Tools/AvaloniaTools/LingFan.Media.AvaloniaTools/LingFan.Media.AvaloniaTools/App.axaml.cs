using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LingFan.Media.Abstractions;
using LingFan.Media.Avalonia;
using LingFan.Media.AvaloniaTools.ViewModels;
using LingFan.Media.AvaloniaTools.Views;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Backends.VLC;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.D3D11;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

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
        // ── 构建 DI：三后端（FFmpeg/VLC/MF）+ D3D11 渲染器 + WASAPI 音频 + Avalonia 控件 ──
        // Windows 专属扩展用 OperatingSystem.IsWindows 守卫，保证同一份代码在 Android/iOS 也能编译/运行。
        // VideoView 通过已注册的 IVideoRendererFactory 集合自动回退：
        //   D3D11RendererFactory（GPU 原生 SwapChain，Avalonia 控件内因需 Pointer/HWND 而失败）
        //   → SkiaVideoRendererFactory（末级兜底，软渲染，解码仍走 GPU）。
        var services = new ServiceCollection();
        var builder = services
            .AddLingFanMedia()
            .AddFFmpeg(options => options.FFmpegLibraryPath = AppContext.BaseDirectory)
            .AddVLC()
            .AddAvaloniaControls()
            .AddSkiaPresenter();

        if (OperatingSystem.IsWindows())
        {
            // MF（同步 MFT）+ D3D11 共享设备（FFmpeg D3D11VA 零拷贝 / MF DXVA）+ D3D11 渲染器 + WASAPI 音频。
            // AddD3D11Renderer 同时注册共享表面源工厂（ISharedGpuSurfaceSourceFactory，供无空域合成上屏使用）。
            // AddCompositionRenderer 注册 CompositionVideoRendererFactory：解码侧 GPU 纹理经共享 D3D11 纹理
            // 交 Avalonia 合成器直接导入、作为控件子视觉无空域上屏（不走 Skia CPU 回读），Skia 仍作末级兜底。
            builder
                .AddMediaFoundation()
                .AddD3D11Renderer()
                // 无空域 GPU 合成上屏暂未启用：先以 Skia 软渲染（解码仍走 GPU）验证「帧→图片→每帧上屏」基础链路。
                // Composition 渲染器在控件内会被优先选中，但其子视觉尺寸/跨设备纹理导入存在盲区，会留下空白画面；
                // 待其修复后再取消下行注释即可启用，Skia 仍作末级兜底。
                // .AddCompositionRenderer()
                .AddWasapiOutput();
        }

        // composer 工厂需要 ILoggerFactory；独立运行的 App 手动 AddLogging 提供。
        services.AddLogging();
        // ViewModel 经 DI 解析，构造函数注入 IServiceProvider。
        services.AddTransient<MainViewModel>();

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

        // 启动后预热：吸收 WASAPI audiodg 冷启动（~2.5s），避免首次播放卡顿。
        // 纯优化，失败一律降级为未预热，绝不影响启动与播放。
        _ = Task.Run(() => PreheatAsync(Services!));
    }

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

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
        // DevTools 仅桌面可达：移动端真机（Android/iOS）USB 调试无 DevTools 服务器，
        // AttachDeveloperTools 启动的核心 RPC 轮询会抛 DevToolsUnreachableException，跨 JNI 边界
        // 升级为 JavaProxyThrowable 直接杀进程（见 2.txt FATAL EXCEPTION: main）。
        // 移动端不挂载即可消除该崩溃；包仍保留，桌面行为不变。
        //if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
        //    this.AttachDeveloperTools();
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

        // 无空域 GPU 合成上屏（CompositionVideoRenderer）：跨平台零拷贝首选路径，Skia 末级兜底。
        // 须先存在 ISharedGpuSurfaceSourceFactory——Windows=AddD3D11Renderer 注册 D3D11 源；
        // Android/iOS=AddVulkanRenderer 已注册 VulkanSharedSurfaceSourceFactory（承载 AHB→GPU 导入）。
        // 此前此注册被误锁在 IsWindows() 内，导致 Android 的零拷贝渲染器工厂根本未进入 DI 集合，
        // EnsurePresenter 仅尝试 Vulkan 直连（控件内无 Pointer 句柄必抛）→ 直接落到 Skia（治根F）。
        // 合成器不支持或导入自检失败时，VideoView 经异常驱动回退链干净落到 Skia——本注册不依赖任何平台专属 API。
        //if (OperatingSystem.IsWindows() || OperatingSystem.IsAndroid())
        //{
        builder.AddCompositionRenderer();
        //}

        if (OperatingSystem.IsWindows())
        {
            // MF（同步 MFT）+ D3D11 共享设备（FFmpeg D3D11VA 零拷贝 / MF DXVA）+ D3D11 渲染器 + WASAPI 音频。
            // AddD3D11Renderer 同时注册共享表面源工厂（ISharedGpuSurfaceSourceFactory，供无空域合成上屏使用）。
            builder
                //.AddMediaFoundation()
                .AddD3D11Renderer()
                .AddWasapiOutput();
        }
        // 平台后端注册钩子：仅平台可用（如 Android 的 MediaCodec）的后端由平台入口直接引用并在此注入，
        // 共享层不引用这些后端，避免跨 TFM 传递解析落到桩实现。桌面/iOS/浏览器端未设置则为无操作。
        // 平台入口（Android）经 MediaBuilderPlatformRegistrar.PlatformRegistrar 在此应用其平台后端。
        builder.ApplyPlatformRegistrar();

        // Android 无空域 GPU 合成上屏（CompositionVideoRenderer）：Vulkan 离屏图像按合成器要求导出为
        // opaque fd（VulkanOpaquePosixFileDescriptor = VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_FD_BIT，即 dma_buf），
        // 交 Avalonia 合成器直接导入、作为控件子视觉无空域零拷贝上屏（不走 Skia CPU 回读）。
        // 该句柄类型与 Linux 完全一致（2.txt 实测 Android Vulkan 合成器支持 [VulkanOpaquePosixFileDescriptor]）；
        // 解码侧 MediaCodec 帧经 AHB 导入本设备（VulkanGpuFrameProducer）与此正交。源工厂按 ExternalSharingEnabled
        // （VK_KHR_external_memory_fd）把关，能力不满足时 Attach 经导入自检失败干净回退 Skia。
        // 注意：CompositionVideoRenderer.Attach 仅做轻量挂载并 Post 一个 UI 线程 ResolveAsync 异步解析
        // TryGetCompositionGpuInterop（不阻塞 UI 线程），避免了此前在 Android UI 线程死锁（卡 logo）的根因，
        // 故此注册在 Android 安全启用。
        if (OperatingSystem.IsAndroid())
            builder.AddCompositionRenderer();

        // Android 真机：注册原生 OpenSL ES 音频输出（O4）。非 Android 调用会抛 PlatformNotSupportedException，
        // 故用 OperatingSystem.IsAndroid 守卫（与上方 Windows 守卫同构）。不注册则回落 NoOp 静音。
        if (OperatingSystem.IsAndroid())
            builder.AddOpenSlesOutput();
        services.AddLogging(options =>
        {
            options.AddConsole();
            // Debug provider 走 System.Diagnostics.Debug → .NET Android 转发 logcat DOTNET tag，
            // 不经 stdout——Fast Deployment（xamarin.sync）模式下 Console 的 stdout 重定向失效，
            // Android 设备上的托管日志只有这条通道可见。
            options.AddDebug();
            options.SetMinimumLevel(LogLevel.Information);
        });


        // ViewModel 经 DI 解析，构造函数注入 IServiceProvider。
        services.AddTransient<MainViewModel>();

        // 内置样例提供者：跨平台通用（AvaloniaResource 嵌入 Assets/sample.mp4，各平台共用同一实现）。
        services.AddSingleton<IBundledSampleProvider, BundledSampleProvider>();

        Services = builder.Services.BuildServiceProvider();

        // ── 共享 Vulkan device 注入（Android GPU 路径前提）──
        // Android 平台模块（LingFan.Media.Platforms.Android）自建 Vulkan device 并注册
        // IVulkanSharedDeviceProvider 到 DI，此处把同一 device 注入 VulkanRendererFactory：
        // 共享表面源的 dma_buf fd 导入从「跨实例」变为「同 device」，规避 Adreno 跨实例导入缺陷
        // （vkAllocateMemory 返回 ErrorInitializationFailed）。注入须在渲染器首次使用之前——
        // 此处 DI 刚构建完成、尚未触碰渲染器，时机正确。
        // 探测式设计原因：共享工程单目标 net10.0，#if ANDROID 永不成立，改用运行时接口探测，
        // 共享工程零平台依赖、AOT 零反射（桌面端无 provider 注册，条件不成立即跳过）。
        {
            var provider = Services.GetService<LingFan.Media.GPUShare.Vulkan.IVulkanSharedDeviceProvider>();
            var vulkanFactory = Services.GetService<LingFan.Media.Renderers.Vulkan.VulkanRendererFactory>();
            if (provider is not null && vulkanFactory is not null)
            {
                var d = provider.GetSharedDevice();
                vulkanFactory.UseExternalDevice(d.InstanceHandle, d.PhysicalDeviceHandle, d.DeviceHandle, d.GraphicsQueueFamilyIndex);
                Console.WriteLine("[ANDROID-VULKAN] 已向 VulkanRendererFactory 注入共享 device（Avalonia 与视频管线同 device）。");
            }
            else if (OperatingSystem.IsAndroid())
            {
                // 显式失败而非静默降级：provider 缺失 = 视频管线与 Avalonia 各自建 device，
                // Adreno 上 AHB/dma_buf 导入失败，GPU 零拷贝整链不可用（退化为 CPU 软渲）。
                Console.WriteLine("[ANDROID-VULKAN][ERROR] GPU 零拷贝不可用：IVulkanSharedDeviceProvider 或 VulkanRendererFactory 缺失"
                    + $"（provider={provider is not null}，factory={vulkanFactory is not null}）。"
                    + "请确认入口调用了 UseLingFanMediaAndroidVulkan()，并在平台注册钩子中注册 IVulkanSharedDeviceProvider。");
            }
        }

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

using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using LingFan.Media.Backends.MediaCodec;
using LingFan.Media.Extensions;
using LingFan.Media.Avalonia.Android;
using LingFan.Media.Abstractions;
using LingFan.Media.GPUShare.Vulkan;
using LingFan.Media.Renderers.OpenGL;
using Microsoft.Extensions.DependencyInjection;

namespace LingFan.Media.AvaloniaTools.Android
{
    [Application]
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        private static void Marker(string name)
        {
            try
            {
                var dir = global::Android.App.Application.Context?.FilesDir?.AbsolutePath ?? "/data/data/com.CompanyName.LingFan.Media.AvaloniaTools/files";
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, name), DateTime.Now.ToString("HH:mm:ss.fff"));
            }
            catch { }
        }

        public override void OnCreate()
        {
            Marker("mark_oncreate.txt");
            base.OnCreate();
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            // Release AOT 下 Console 可能不经 stdout→logcat，启动期锚点一律用 Android.Util.Log 直写。
            Marker("mark_customize.txt");
            global::Android.Util.Log.WriteLine(global::Android.Util.LogPriority.Info, "DOTNET",
                "[ANDROID-VULKAN] Application.CustomizeAppBuilder 进入");
            try
            {
                // Release（无调试器）下 Mono 不再把 stdout 重定向到 logcat，Console 诊断锚点会静默；
                // 启动期把两个标准流接回 logcat（tag=DOTNET），恢复 Release 构建的观测能力。
                Console.SetOut(new LogCatTextWriter(global::Android.Util.LogPriority.Info));
                Console.SetError(new LogCatTextWriter(global::Android.Util.LogPriority.Error));

                // 一步式引导：自建 Vulkan device 并注入 Avalonia（CustomSharedDevice），
                // 使 Avalonia 与视频管线共用同一 VkDevice；同时设置 RenderingMode [Vulkan, Egl, Software]。
                // 内部细节见 LingFan.Media.Platforms.Android.AndroidVulkanAppBuilderExtensions。
                builder = builder.UseLingFanMediaAndroidVulkan();

                // 选型策略：回 Vulkan 后端基线（EGL 后端的合成循环在此设备不驱动上屏，GL 路线挂起）——
                // 声明优先 VulkanNativeImage 形态。GL 导入链路（AHB→EGLImage→GL 纹理，已全通验证）
                // 作为可移植资产保留：PreferredKind 改 GlTexture 即可重新插拔启用。
                SharedGpuSourcePolicy.PreferredKind = SharedGpuHandleKind.VulkanNativeImage;

                // GPU 零拷贝出帧：经 AddMediaCodec 的 Options 配置（后端内部收敛到解码策略）。
                // Android 平台后端（MediaCodec）经共享层平台注册钩子注入，共享层不引用平台后端。
                MediaBuilderPlatformRegistrar.PlatformRegistrar =
                    b =>
                    {
                        b.AddMediaCodec(o => o.EnableHardwareZeroCopy = true);
                        // 共享 App 构建完成后按此契约把自建 device 注入 VulkanRendererFactory
                        //（同 device 化 dma_buf 导入）。
                        b.Services.AddSingleton<IVulkanSharedDeviceProvider>(VulkanSharedDeviceBootstrap.Instance);
                        // GL 共享表面源（GlTexture 句柄）：EGL 后端下由 GL 宿主呈现适配器直采。
                        b.AddOpenGLSharedSurfaceSource();
                        // 直写 Android.Util.Log 的日志通道：Console/Debug provider 在 Fast Deployment
                        // 下均不进 logcat，此 provider 是 Android 设备上托管日志的唯一可靠出口。
                        b.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider, LogCatLoggerProvider>();
                    };

            }
            catch (Exception ex)
            {
                var a = ex;
                throw;
            }
            return base.CustomizeAppBuilder(builder).WithInterFont();
        }
    }
}

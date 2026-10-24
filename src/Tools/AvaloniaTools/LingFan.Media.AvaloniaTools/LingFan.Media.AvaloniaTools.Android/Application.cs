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
                //
                // 引导失败时必须继续启动：UseLingFanMediaAndroidVulkan 的契约是「设备不支持 Vulkan 时
                // 自动回落 EGL/软件」，其内部 RenderingMode 也正是 [Vulkan, Egl, Software] 的优先级列表；
                // 但自建 device 由 VulkanDeviceFactory 急切创建，驱动无 Vulkan（如模拟器的占位
                // vulkan.default.so）时会直接抛 InvalidOperationException。不捕获就等于让整条回落链
                // 失效——进程在 Application 绑定阶段即崩，连 EGL 都轮不到。此处捕获并跳过注入：
                // 没有 CustomSharedDevice 时 Avalonia 自行按 RenderingMode 选 EGL 后端，
                // 视频管线退回 CPU 帧路径（无 GPU 零拷贝，但播放与音频链路照常）。
                bool sharedVulkanReady = true;
                try
                {
                    builder = builder.UseLingFanMediaAndroidVulkan();
                }
                catch (Exception ex)
                {
                    sharedVulkanReady = false;
                    global::Android.Util.Log.WriteLine(global::Android.Util.LogPriority.Warn, "DOTNET",
                        "[ANDROID-VULKAN] 自建 Vulkan device 失败，回落到 EGL/软件渲染：" + ex.Message);
                }

                // 选型策略：回 Vulkan 后端基线（EGL 后端的合成循环在此设备不驱动上屏，GL 路线挂起）——
                // 声明优先 VulkanNativeImage 形态。GL 导入链路（AHB→EGLImage→GL 纹理，已全通验证）
                // 作为可移植资产保留：PreferredKind 改 GlTexture 即可重新插拔启用。
                //
                // 引导失败时改选 GL 纹理形态：没有共享 VkDevice 就没有 dma_buf 导入通道，
                // 继续声明 VulkanNativeImage 只会让共享表面源工厂找不到可用的句柄类型。
                SharedGpuSourcePolicy.PreferredKind = sharedVulkanReady
                    ? SharedGpuHandleKind.VulkanNativeImage
                    : SharedGpuHandleKind.GlTexture;

                // GPU 零拷贝出帧：经 AddMediaCodec 的 Options 配置（后端内部收敛到解码策略）。
                // Android 平台后端（MediaCodec）经共享层平台注册钩子注入，共享层不引用平台后端。
                MediaBuilderPlatformRegistrar.PlatformRegistrar =
                    b =>
                    {
                        // 硬件零拷贝只在共享 Vulkan device 可用时开启：AHB 帧须经 dma_buf 导入
                        // 同一 VkDevice 才能上屏，无 device 时开它只会让解码器产出无法消费的帧。
                        b.AddMediaCodec(o => o.EnableHardwareZeroCopy = sharedVulkanReady);
                        // 共享 App 构建完成后按此契约把自建 device 注入 VulkanRendererFactory
                        //（同 device 化 dma_buf 导入）。引导已失败时不得注册：共享 App 会在
                        // OnFrameworkInitializationCompleted 里调用 GetSharedDevice()，其内部幂等懒
                        // 初始化会再试一次建 device 并再次抛异常，把已捕获的失败重新抛回启动路径。
                        if (sharedVulkanReady)
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

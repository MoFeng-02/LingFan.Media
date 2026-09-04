using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using LingFan.Media.Backends.MediaCodec;
using LingFan.Media.Extensions;
using LingFan.Media.Avalonia.Android;
using LingFan.Media.GPUShare.Vulkan;
using Microsoft.Extensions.DependencyInjection;

namespace LingFan.Media.AvaloniaTools.Android
{
    [Application]
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            // 一步式引导：自建 Vulkan device 并注入 Avalonia（CustomSharedDevice），
            // 使 Avalonia 与视频管线共用同一 VkDevice；同时设置 RenderingMode [Vulkan, Egl, Software]。
            // 内部细节见 LingFan.Media.Platforms.Android.AndroidVulkanAppBuilderExtensions。
            builder = builder.UseLingFanMediaAndroidVulkan();

            // GPU 零拷贝出帧：经 AddMediaCodec 的 Options 配置（后端内部收敛到解码策略）。
            // Android 平台后端（MediaCodec）经共享层平台注册钩子注入，共享层不引用平台后端。
            MediaBuilderPlatformRegistrar.PlatformRegistrar =
                b =>
                {
                    b.AddMediaCodec(o => o.EnableHardwareZeroCopy = true);
                    // 共享 App 构建完成后按此契约把自建 device 注入 VulkanRendererFactory
                    //（同 device 化 dma_buf 导入）。
                    b.Services.AddSingleton<IVulkanSharedDeviceProvider>(VulkanSharedDeviceBootstrap.Instance);
                };

            return base.CustomizeAppBuilder(builder).WithInterFont();
        }
    }
}

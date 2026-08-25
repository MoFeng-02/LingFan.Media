using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using LingFan.Media.Backends.MediaCodec;
using LingFan.Media.Extensions;

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
            // Android 平台后端（MediaCodec）经共享层的平台注册钩子注入，与本工程的共享层互不冲突。
            // 输出模式：MediaCodec 配置到 ImageReader Surface（硬解直出），帧经 Image.Plane 在 CPU 侧
            // 提取标准 I420；ImageReader 创建失败自动回落 ByteBuffer 软解兜底。两种模式均输出 CPU 帧，
            // 无 GPU 零拷贝依赖、无手写 P/Invoke（符合 2026-08-22 架构裁定；解码器侧 AHB→GPU 仍走
            // 现有 SoftwareFrameResource 输出，显示侧零拷贝由下方 Vulkan 合成路由承载）。
            MediaBuilderPlatformRegistrar.PlatformRegistrar =
                b => b.AddMediaCodec();

            // 启用 Vulkan 渲染后端（AndroidRenderingMode 首选项）：Android 默认仅 [Egl, Software]，
            // 而 EGL 后端不暴露 VK_ANDROID_external_memory_android_hardware_buffer 的 AHB 合成互操作，
            // 导致 ICompositionGpuInterop 拿不到 AHB 句柄、零拷贝无法激活。Vulkan 后端使合成器经
            // AHB 暴露 ICompositionGpuInterop，VideoView 的 CompositionVideoRenderer 方可走 AndroidHardwareBuffer
            // 零拷贝上屏。EGL / Software 保留为降级兜底（API 24+ 设备 Vulkan 为推荐路径）。
            return base.CustomizeAppBuilder(builder)
                .With(new AndroidPlatformOptions
                {
                    RenderingMode = new[]
                    {
                        AndroidRenderingMode.Vulkan,
                        AndroidRenderingMode.Egl,
                        AndroidRenderingMode.Software,
                    },
                })
                .WithInterFont();
        }
    }
}

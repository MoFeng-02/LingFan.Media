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

            // 渲染后端：Android 默认 [Egl, Software]。此前为「无空域零拷贝」opt-in 了 Vulkan
            // （EGL 后端不暴露 ICompositionGpuInterop 的 Vulkan 外部内存互操作），但 Vulkan AHB 采样
            // 在 Adreno 上触发驱动 SIGSEGV（vkFormat=R8G8B8A8Unorm 规范正确仍崩，纯驱动 bug，已联网核实
            // Chromium/Flutter 同列为 Adreno workaround 重灾区）。故默认回落 EGL + 软件（能播档），
            // 零拷贝经 GL 纹理路线（Phase 2，Flutter/TextureView 同款）重做，不再硬走 Vulkan AHB。
            return base.CustomizeAppBuilder(builder)
                .With(new AndroidPlatformOptions
                {
                    RenderingMode = 
                    [
                        AndroidRenderingMode.Egl,
                        AndroidRenderingMode.Software,
                    ],
                })
                .WithInterFont();
        }
    }
}

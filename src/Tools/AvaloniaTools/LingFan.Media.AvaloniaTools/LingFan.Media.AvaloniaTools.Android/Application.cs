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

            // 渲染后端：Android 默认 [Egl, Software]，必须显式把 Vulkan 提到首位。
            //
            // 【为什么这是治本的一下】Avalonia 的 EGL/GL 后端**不实现外部图像导入**，
            // 因此 ICompositionGpuInterop.SupportedImageHandleTypes 恒为空集 —— 任何共享表面源工厂
            // 都不可能命中，CompositionVideoRenderer 必然回退 Skia，整条链路退化成 CPU 软渲
            // （实测 1080x1920 上屏仅 12~18fps，是「开播期糊」的底色）。
            // 官方讨论 AvaloniaUI/Avalonia#20970 实测四平台取值可佐证：
            //   Windows → [D3D11TextureGlobalSharedHandle, D3D11TextureNtHandle]
            //   Android → []            （contextSharingFeature=Avalonia.Skia.GlSkiaGpu，即跑在 GL 上）
            //   macOS   → [IOSurfaceRef]
            // 切到 Vulkan 后合成器上报 [VulkanOpaquePosixFileDescriptor]，与 Linux 完全一致。
            //
            // 【旧障碍已失效】此前回落 EGL 的原因是「Vulkan AHB 采样在 Adreno 触发驱动 SIGSEGV」。
            // 但治根M（2026-08-27）已**删除整条 AHB 自分配代码**：改走普通 R8G8B8A8 图像 +
            // ExportMemoryAllocateInfo(OPAQUE_FD) + vkGetMemoryFdKHR 导出 dma_buf fd，
            // 与 Linux 同路径，全程不做 AHB 采样 —— 当年崩的那条路已经不存在了。
            // Vulkan 放在首位、EGL/软件紧随其后：设备不支持 Vulkan 时自动回落，不影响能播档。
            return base.CustomizeAppBuilder(builder)
                .With(new AndroidPlatformOptions
                {
                    RenderingMode =
                    [
                        AndroidRenderingMode.Vulkan,
                        AndroidRenderingMode.Egl,
                        AndroidRenderingMode.Software,
                    ],
                })
                .WithInterFont();
        }
    }
}

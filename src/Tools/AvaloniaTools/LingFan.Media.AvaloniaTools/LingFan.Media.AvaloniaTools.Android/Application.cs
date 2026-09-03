using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using LingFan.Media.Backends.MediaCodec;
using Microsoft.Extensions.DependencyInjection;
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
            // ── R2 里程碑 1（2026-09-02）：自建 Vulkan device 并注入 Avalonia（CustomSharedDevice）──
            // 让 Avalonia 与视频管线共用同一 VkDevice —— 同 device 建视频纹理 + 渲染流程内直绘的前提
            // （无空域、零拷贝、无 ByteBuffer 提取，坏帧根除）。注入路径已经 TryCreate 的 IL 反汇编确认：
            //   get_CustomSharedDevice 判空 → 非 null 直接用 → null 才走 Avalonia 自己的 Instance/Device.Create。
            LingFanVulkanBootstrap.Initialize();
            var vulkanOptions = new global::Avalonia.Vulkan.VulkanOptions();
            vulkanOptions.CustomSharedDevice = LingFanVulkanBootstrap.DeviceAdapter;

            // ── M4 验证（2026-09-02）：解码侧切换 AHB 零拷贝出帧 ──
            // 打开 GLES 桥接零拷贝：解码器渲入桥接 SurfaceTexture，GPU 内 YUV→RGBA 落 AHardwareBuffer，
            // 帧以 AndroidHardwareBufferFrameResource 交付（无 ByteBuffer CPU 提取 = 坏帧根因根除）；
            // 显示侧由 VulkanSharedSurfaceSource（VulkanNativeImage）→ Skia GPU 直绘全链路承接。
            // 失败自动回退 ByteBuffer CPU 档（能播档），不影响播放。
            global::LingFan.Media.Backends.MediaCodec.Decoders.AndroidVideoDecodePolicy.EnableHardwareZeroCopy = true;

            // Android 平台后端（MediaCodec）经共享层的平台注册钩子注入，与本工程的共享层互不冲突。
            MediaBuilderPlatformRegistrar.PlatformRegistrar =
                b =>
                {
                    b.AddMediaCodec();
                    // 治根BA：Bootstrap 在本方法开头已 Initialize ⇒ provider 可注册进 DI
                    b.Services.AddSingleton<global::LingFan.Media.GPUShare.Vulkan.IVulkanSharedDeviceProvider>(
                        LingFanVulkanBootstrap.Instance);
                };

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
                .With(vulkanOptions)
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

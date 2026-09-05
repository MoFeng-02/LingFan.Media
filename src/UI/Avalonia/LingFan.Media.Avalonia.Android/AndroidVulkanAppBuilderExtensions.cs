#if ANDROID23_0_OR_GREATER
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Android;
using Avalonia.Logging;

namespace LingFan.Media.Avalonia.Android;

/// <summary>Android 宿主的一步式 Vulkan 共享 device 引导扩展。</summary>
[SupportedOSPlatform("android23.0")]
public static class AndroidVulkanAppBuilderExtensions
{
    /// <summary>
    /// 启用本库的 Android GPU 路径：自建 Vulkan device 并注入 Avalonia
    /// （<c>VulkanOptions.CustomSharedDevice</c>），使 Avalonia 合成器与视频管线共用同一 VkDevice
    /// ——GPU 零拷贝上屏（Composition 渲染器 + AHB 导入）的前提。
    /// </summary>
    /// <remarks>
    /// <para>内部同时设置 <c>AndroidRenderingMode = [Vulkan, Egl, Software]</c>：Vulkan 必须在首位——
    /// Avalonia 的 EGL/GL 后端不实现外部图像导入，<c>ICompositionGpuInterop.SupportedImageHandleTypes</c>
    /// 恒为空集，共享表面源工厂不可能命中；切到 Vulkan 后合成器上报
    /// <c>VulkanOpaquePosixFileDescriptor</c>（dma_buf），与 Linux 一致。
    /// 设备不支持 Vulkan 时自动回落 EGL/软件，不影响能播档。</para>
    /// <para>同时开启 Avalonia 内部日志直灌 logcat（tag AVALONIA，Verbose 级）：Avalonia 平台渲染层
    /// （后端初始化/合成器 render target/swapchain——黑屏问题的所在层）不走 MEL 且默认全静默，
    /// LogToTrace→调试器通道、LogToConsole→stdout 在 Fast Deployment 下均不可见，
    /// LogToDelegate→Android.Util.Log 是设备上唯一可靠出口。</para>
    /// <para>配套要求：DI 侧注册 <c>AddSingleton&lt;IVulkanSharedDeviceProvider&gt;(VulkanSharedDeviceBootstrap.Instance)</c>
    /// 并在 App 构建后向 <c>VulkanRendererFactory</c> 注入外部 device（共享 App 已内置该逻辑）。</para>
    /// </remarks>
    public static AppBuilder UseLingFanMediaAndroidVulkan(this AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        VulkanSharedDeviceBootstrap.Initialize();
        var vulkanOptions = new global::Avalonia.Vulkan.VulkanOptions
        {
            CustomSharedDevice = VulkanSharedDeviceBootstrap.DeviceAdapter,
        };

        return builder
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
            // Avalonia 内部日志直灌 logcat（tag AVALONIA，全量 Verbose）：平台渲染层（后端初始化/
            // 合成器 render target/swapchain——黑屏问题所在层）不走 MEL 且默认全静默；LogToTrace→
            // 调试器通道、LogToConsole→stdout 在 Fast Deployment 下均不可见，LogToDelegate→
            // Android.Util.Log 是设备上唯一可靠出口。回调为 Action&lt;string&gt;（官方 API，无 level 参数）。
            .LogToDelegate(
                message => global::Android.Util.Log.WriteLine(
                    global::Android.Util.LogPriority.Info, "AVALONIA", message),
                LogEventLevel.Verbose);
    }
}
#endif

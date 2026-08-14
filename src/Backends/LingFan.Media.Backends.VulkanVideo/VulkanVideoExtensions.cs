using LingFan.Media.Backends.VulkanVideo.Decoders;
using LingFan.Media.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LingFan.Media.Backends.VulkanVideo;

/// <summary>
/// Vulkan 硬解后端（VK_KHR_video_decode_h264）DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddVulkanVideo(options =&gt; { ... })</code></para>
/// <para>注册的是工厂（Singleton），不是实例！Decoder 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>VulkanVideoBackend 作为 Singleton 是安全的——只持有选项与平台能力标记（无原生全局状态需要释放）。</para>
/// <para>依赖倒置：本后端只依赖 Abstractions 契约（IVideoDecoderFactory / IGpuDeviceContext）、GPUShare.Vulkan 绑定与
/// 渲染器共享的 VkDevice，绝不引用任何 Renderers 程序集。零拷贝经共享 VkDevice 由渲染器侧直接消费
/// （解码产出的 NV12 VkImage 与渲染器同设备，无需经 IGpuFrameProducer 导入）。</para>
/// </remarks>
public static class VulkanVideoExtensions
{
    /// <summary>
    /// 注册 Vulkan 硬解后端（IVideoDecoderFactory）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">Vulkan 后端配置回调（可选）。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddVulkanVideo(
        this MediaBuilder builder,
        Action<VulkanVideoOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new VulkanVideoOptions();
        configure?.Invoke(options);

        // 注册 Vulkan 后端入口（Singleton，持有选项与平台能力标记，无原生全局状态）
        builder.Services.AddSingleton<VulkanVideoBackend>();
        builder.Services.AddSingleton(options);

        // 注册工厂（集合注册 TryAddEnumerable：支持多后端并存、按 DI 注册顺序参与运行时回退）
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IVideoDecoderFactory, VulkanVideoDecoderFactory>());

        return builder;
    }
}

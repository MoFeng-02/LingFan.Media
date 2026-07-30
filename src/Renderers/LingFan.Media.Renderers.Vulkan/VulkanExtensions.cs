using LingFan.Media.Renderers.Shared;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan 渲染器 DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddVulkanRenderer()</code></para>
/// <para>注册 <see cref="VulkanRendererFactory"/> 为 Singleton。
/// 调用 <c>Create()</c> 返回缓存的 <see cref="VulkanRenderer"/> 单例（共享 GPU Device）。</para>
/// <para>同时注册 <see cref="IGpuDeviceContext"/>（由工厂持有的 <see cref="RenderContext"/> 实现），
/// 供 Avalonia / FFmpeg 硬解等层查询 GPU 能力（依赖倒置严守）。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class VulkanExtensions
{
    /// <summary>
    /// 注册 Vulkan 渲染器（跨平台 GPU 渲染：Windows / Linux / Android；macOS/MoltenVK 待开发）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddVulkanRenderer(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<VulkanRendererFactory>();
        builder.Services.AddSingleton<IVideoRendererFactory>(sp => sp.GetRequiredService<VulkanRendererFactory>());

        // 中立 GPU 设备上下文（Abstractions 契约），由 VulkanRendererFactory 注入能力。
        // RenderContext 实现 IGpuDeviceContext，Avalonia / Outputs 等层可查询 GPU 能力
        // 而无需引用具体渲染器模块（依赖倒置严守）。
        builder.Services.AddSingleton<IGpuDeviceContext>(sp =>
        {
            var factory = sp.GetRequiredService<VulkanRendererFactory>();
            return factory.Context;
        });

        return builder;
    }
}

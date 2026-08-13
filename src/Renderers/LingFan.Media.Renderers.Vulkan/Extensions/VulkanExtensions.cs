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
    /// 注册 Vulkan 渲染器（跨平台 GPU 渲染：Windows / Linux / Android；macOS/iOS 经 MoltenVK 覆盖——
    /// 仅引入 MoltenVK 让 Vulkan 后端在 Apple 平台初始化/跑 SwapChain 的有头路径；无空域零拷贝上屏属第二类，待 Apple 合成栈落地）。
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

        // 无空域合成桥（ISharedGpuSurfaceSource）注册 Vulkan 实现——「Vulkan 渲染 Vulkan 的」架构原则落地：
        // 本源产出<b>自身</b>的 Vulkan 外部内存/信号量句柄（VulkanOpaqueNtHandle / VulkanOpaquePosixFileDescriptor），
        // 由原生支持导入该句柄的宿主组合器（Vulkan 后端合成器）消费，不跨界喂 D3D11 组合器、
        // 不伪造 D3D11 句柄类型。仅在本 Vulkan 模块内注册，严守模块化与依赖倒置。
        // UI 层遍历 IEnumerable<ISharedGpuSurfaceSourceFactory> 选中首个 IsAvailable 且句柄被支持的工厂，
        // 故不在此硬编码任何「优先 D3D11 / 其次 Vulkan」分支。
        builder.Services.AddSingleton<VulkanSharedSurfaceSourceFactory>();
        builder.Services.AddSingleton<ISharedGpuSurfaceSourceFactory>(sp =>
            sp.GetRequiredService<VulkanSharedSurfaceSourceFactory>());

        // E3 后端自动选择：启用且未显式指定时，Vulkan 作为候选默认 GPU 后端（与 D3D11 同构守卫）。
        // 默认仍面向无空域合成；独立 Win32 HWND 路径为 opt-in（由消费方显式 Attach HWND 启用）。
        if (builder.Options.EnableAutoBackendSelection && builder.Options.DefaultVideoRenderer is null)
        {
            builder.Options.DefaultVideoRenderer = typeof(VulkanRendererFactory);
        }

        return builder;
    }
}

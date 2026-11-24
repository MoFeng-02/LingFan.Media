using LingFan.Media.Abstractions;
using LingFan.Media.Composition;
using LingFan.Media.Presenters.Metal;
using LingFan.Media.Renderers.Metal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Presenters;

/// <summary>
/// Metal GPU 呈现器的 DI 注册扩展。位于中立的 LingFan.Media.GpuPresenter 项目（无 UI 依赖）。
/// </summary>
/// <remarks>
/// <para><b>落点说明</b>：扩展方法放在 GpuPresenter 项目而非 Extensions 项目——
/// GpuPresenter 引 Extensions（拿 MediaBuilder）但不反向，避免环。宿主需
/// <c>using LingFan.Media.Presenters;</c> 调用。</para>
/// <para><b>注册内容</b>：MetalRendererFactory（具体类型单一真源）→ IVideoRendererFactory
/// （与 AddMetalRenderer 同构，确保自包含；若宿主已调 AddMetalRenderer 则重复注册无害）
/// + IGpuPresenterFactory → MetalGpuPresenterFactory。</para>
/// <para><b>平台边界</b>：注册本身跨平台无害（纯 DI）；运行时 Attach 仅 Apple 成立，
/// 其余平台由渲染器抛 PlatformNotSupportedException。<b>运行时验证待 Apple 真机</b>。</para>
/// <para><b>AOT 兼容</b>：工厂委托显式构造，无反射。</para>
/// <para><b>异步策略</b>：同步配置——纯 DI 注册，无 I/O。</para>
/// </remarks>
public static class MetalPresenterExtensions
{
    /// <summary>
    /// 注册 Metal GPU 视频呈现器工厂，使 VideoView 在 RendererType = typeof(MetalGpuPresenter) 时
    /// 可通过 DI 解析出原生 GPU Presenter。
    /// </summary>
    /// <param name="builder">媒体服务构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddMetalPresenter(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // 与 AddMetalRenderer 同一落法：具体类型作单一真源，契约服务从它派生。
        services.AddSingleton<MetalRendererFactory>();
        services.AddSingleton<IVideoRendererFactory>(sp => sp.GetRequiredService<MetalRendererFactory>());

        services.AddSingleton<IGpuPresenterFactory>(sp =>
            new MetalGpuPresenterFactory(
                sp.GetRequiredService<IVideoRendererFactory>(),
                sp.GetRequiredService<ILoggerFactory>()));

        return builder;
    }
}
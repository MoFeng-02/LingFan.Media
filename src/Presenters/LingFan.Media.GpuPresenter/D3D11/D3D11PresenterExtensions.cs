using LingFan.Media.Abstractions;
using LingFan.Media.Extensions;
using LingFan.Media.Presenters;
using LingFan.Media.Presenters.D3D11;
using LingFan.Media.Renderers.D3D11;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace LingFan.Media.Presenters;

/// <summary>
/// GPU 呈现器的 DI 注册扩展。位于中立的 LingFan.Media.GpuPresenter 项目（无 UI 依赖）。
/// </summary>
/// <remarks>
/// <para><b>落点说明</b>：扩展方法放在 GpuPresenter 项目而非 Extensions 项目——
/// GpuPresenter 引 Extensions（拿 MediaBuilder）但不反向，避免 Extensions 引用 GpuPresenter 形成环。
/// 宿主需 <c>using LingFan.Media.Presenters;</c> 调用。</para>
/// <para><b>注册内容</b>：</para>
/// <list type="bullet">
/// <item><see cref="IVideoRendererFactory"/> → <see cref="D3D11RendererFactory"/>
/// （来自 Renderers.D3D11，确保自包含；若宿主已调 AddD3D11Renderer 则重复注册无害）。</item>
/// <item><see cref="IGpuPresenterFactory"/> → <see cref="D3D11GpuPresenterFactory"/>。</item>
/// </list>
/// <para><b>AOT 兼容</b>：使用工厂委托（非泛型 AddSingleton&lt;TService,TImplementation&gt;）显式构造，
/// 避免跨程序集类型在 trimming 分析时触发 IL2066。</para>
/// <para><b>异步策略</b>：config 分类——纯 DI 注册，无 I/O。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class GpuPresenterExtensions
{
    /// <summary>
    /// 注册 D3D11 GPU 视频呈现器工厂，使 VideoView 在 RendererType = typeof(D3D11GpuPresenter) 时
    /// 可通过 DI 解析出原生 GPU Presenter。
    /// </summary>
    /// <param name="builder">媒体服务构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddD3D11Presenter(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // 🔴 与 AddD3D11Renderer 同一落法：具体类型作单一真源，两个契约服务都从它派生。
        // 不得写 `(D3D11RendererFactory)sp.GetRequiredService<IVideoRendererFactory>()`——
        // 后注册的渲染器会胜出，强制转换直接 InvalidCastException。
        services.AddSingleton<D3D11RendererFactory>(sp =>
            new D3D11RendererFactory(sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IVideoRendererFactory>(sp => sp.GetRequiredService<D3D11RendererFactory>());

        // 第二轮审计修复：必须注册 IGpuDeviceContext，否则 FFmpegVideoDecoderFactory
        // 获取 null → D3D11VA 硬件解码静默禁用（与 AddD3D11Renderer 行为一致）
        services.AddSingleton<IGpuDeviceContext>(sp => sp.GetRequiredService<D3D11RendererFactory>().Context);

        services.AddSingleton<IGpuPresenterFactory>(sp =>
            new D3D11GpuPresenterFactory(
                sp.GetRequiredService<IVideoRendererFactory>(),
                sp.GetRequiredService<ILoggerFactory>()));

        return builder;
    }

    /// <summary>
    /// 注册 D3D11 GPU 视频呈现器工厂（基于 <see cref="IServiceCollection"/>，供不使用 MediaBuilder 的宿主）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>服务集合（链式调用）。</returns>
    public static IServiceCollection AddGpuPresenter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<D3D11RendererFactory>(sp =>
            new D3D11RendererFactory(sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IVideoRendererFactory>(sp => sp.GetRequiredService<D3D11RendererFactory>());

        // 第二轮审计修复：同步注册 IGpuDeviceContext（与 AddD3D11Presenter 一致）
        services.AddSingleton<IGpuDeviceContext>(sp => sp.GetRequiredService<D3D11RendererFactory>().Context);

        services.AddSingleton<IGpuPresenterFactory>(sp =>
            new D3D11GpuPresenterFactory(
                sp.GetRequiredService<IVideoRendererFactory>(),
                sp.GetRequiredService<ILoggerFactory>()));

        return services;
    }
}

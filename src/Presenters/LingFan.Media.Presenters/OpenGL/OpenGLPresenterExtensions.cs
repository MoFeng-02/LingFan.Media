using LingFan.Media.Abstractions;
using LingFan.Media.Composition;
using LingFan.Media.Presenters.OpenGL;
using LingFan.Media.Renderers.OpenGL;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Presenters;

/// <summary>
/// OpenGL GPU 呈现器的 DI 注册扩展。位于中立的 LingFan.Media.Presenters 项目（无 UI 依赖）。
/// </summary>
/// <remarks>
/// <para><b>落点说明</b>：扩展方法放在 GpuPresenter 项目而非 Extensions 项目——
/// GpuPresenter 引 Extensions（拿 MediaBuilder）但不反向，避免环。宿主需
/// <c>using LingFan.Media.Presenters;</c> 调用。</para>
/// <para><b>注册内容</b>：OpenGLRendererFactory（具体类型单一真源）→ IVideoRendererFactory /
/// IGpuDeviceContext / IGpuFrameProducer（与 AddOpenGLRenderer 同构，确保自包含；
/// 若宿主已调 AddOpenGLRenderer 则重复注册无害）+ IGpuPresenterFactory → OpenGLGpuPresenterFactory。</para>
/// <para><b>AOT 兼容</b>：工厂委托显式构造，无反射。</para>
/// <para><b>异步策略</b>：同步配置——纯 DI 注册，无 I/O。</para>
/// </remarks>
public static class OpenGLPresenterExtensions
{
    /// <summary>
    /// 注册 OpenGL GPU 视频呈现器工厂，使 VideoView 在 RendererType = typeof(OpenGLGpuPresenter) 时
    /// 可通过 DI 解析出原生 GPU Presenter。
    /// </summary>
    /// <param name="builder">媒体服务构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddOpenGLPresenter(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // 与 AddOpenGLRenderer 同一落法：具体类型作单一真源，契约服务都从它派生。
        services.AddSingleton<OpenGLRendererFactory>();
        services.AddSingleton<IVideoRendererFactory>(sp => sp.GetRequiredService<OpenGLRendererFactory>());
        services.AddSingleton<IGpuDeviceContext>(sp => sp.GetRequiredService<OpenGLRendererFactory>().DeviceContext);
        services.AddSingleton<IGpuFrameProducer>(sp => sp.GetRequiredService<OpenGLRendererFactory>().CreateFrameProducer());

        services.AddSingleton<IGpuPresenterFactory>(sp =>
            new OpenGLGpuPresenterFactory(
                sp.GetRequiredService<IVideoRendererFactory>(),
                sp.GetRequiredService<ILoggerFactory>()));

        return builder;
    }
}
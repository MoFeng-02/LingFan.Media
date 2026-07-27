using System;
using LingFan.Media.Abstractions;
using LingFan.Media.Extensions;
using LingFan.Media.Renderers.D3D11;
using Microsoft.Extensions.DependencyInjection;

namespace LingFan.Media.Avalonia.D3D11;

/// <summary>
/// D3D11 GPU Presenter 的 DI 注册扩展。
/// </summary>
/// <remarks>
/// <para><b>落点在 Avalonia.D3D11 桥接项目（而非 Extensions 项目）</b>：
/// Avalonia 项目已引用 Extensions，若 Extensions 再引用 Avalonia.D3D11 会形成项目引用环
/// （Avalonia.D3D11 → Avalonia → Extensions → Avalonia.D3D11）。接线就近后端，符合依赖倒置——
/// 宿主需 <c>using LingFan.Media.Avalonia.D3D11;</c> 调用本方法。</para>
/// <para><b>注册内容</b>：</para>
/// <list type="bullet">
/// <item><see cref="IVideoRendererFactory"/> → <see cref="D3D11RendererFactory"/>
/// （来自 Renderers.D3D11，确保自包含；若宿主已调 AddD3D11Renderer 则重复注册无害）。</item>
/// <item><see cref="IVideoPresenterFactory"/> → <see cref="D3D11PresenterFactory"/>。</item>
/// </list>
/// <para><b>异步策略</b>：config 分类——纯 DI 注册，无 I/O。</para>
/// </remarks>
public static class D3D11PresenterExtensions
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

        builder.Services.AddSingleton<IVideoRendererFactory, D3D11RendererFactory>();
        builder.Services.AddSingleton<IVideoPresenterFactory, D3D11PresenterFactory>();

        return builder;
    }
}

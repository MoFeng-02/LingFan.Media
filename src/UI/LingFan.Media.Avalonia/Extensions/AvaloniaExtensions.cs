using System;
using Microsoft.Extensions.DependencyInjection;
using MediaBuilder = LingFan.Media.Extensions.MediaBuilder;

namespace LingFan.Media.Avalonia;

/// <summary>
/// Avalonia DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：config 分类——纯 DI 注册，无 I/O。</para>
/// <para><b>使用模式</b>：<code>services.AddLingFanMedia().AddAvaloniaControls();</code></para>
/// <para>V1 可能为空注册或仅注册样式资源。未来可注册默认 Presenter 工厂等服务。</para>
/// <para><b>AOT 兼容</b>：static 类，无反射。</para>
/// </remarks>
public static class AvaloniaExtensions
{
    /// <summary>
    /// 注册 Avalonia 控件相关服务。
    /// </summary>
    /// <param name="builder">媒体服务构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddAvaloniaControls(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // V1: 空注册。
        // 控件本身不需要 DI 注册（VideoView/MediaControl/AudioVisualizer 是直接 new 的 Avalonia 控件）。
        // 样式资源由消费方 App.axaml 引用，或通过 AddAvaloniaControls() 合并到应用 Styles。

        return builder;
    }

    /// <summary>
    /// 注册默认 Skia 视频呈现器工厂（<see cref="IVideoPresenterFactory"/> → <see cref="SkiaPresenterFactory"/>，Singleton）。
    /// </summary>
    /// <param name="builder">媒体服务构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    /// <remarks>
    /// <para><b>U9 (V2-09)</b>：默认 Presenter 工厂以 Singleton 注册，供宿主 DI 解析。
    /// 控件 <see cref="VideoView"/> 自身仍通过 <see cref="VideoView.RendererType"/> 静态创建 Presenter
    /// （Avalonia 控件不走 DI 解析，避免引入控件工厂注入耦合），本工厂供需要编程式创建/替换 Presenter
    /// 的宿主或未来控件工厂注入使用。</para>
    /// <para><b>D3D11 GPU Presenter 工厂</b>（<c>AddD3D11Presenter</c>）暂未注册——依赖 D1 决策
    /// （GPU Presenter 适配器类是否创建）与 Renderers.D3D11 真实实现，属独立 PR 范畴。</para>
    /// <para><b>异步策略</b>：config 分类——纯 DI 注册，无 I/O。</para>
    /// <para><b>AOT 兼容</b>：static 方法，无反射。</para>
    /// </remarks>
    public static MediaBuilder AddSkiaPresenter(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IVideoPresenterFactory, SkiaPresenterFactory>();
        return builder;
    }
}

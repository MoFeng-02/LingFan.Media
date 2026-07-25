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
        // 未来可注册：
        //   - 默认 IVideoPresenter 工厂
        //   - 样式资源合并服务
        //   - 自定义控件主题

        return builder;
    }
}

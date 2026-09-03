using Microsoft.Extensions.DependencyInjection;
using MediaBuilder = LingFan.Media.Extensions.MediaBuilder;

namespace LingFan.Media.Avalonia;

/// <summary>
/// Avalonia DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：config 分类——纯 DI 注册，无 I/O。</para>
/// <para><b>使用模式</b>：<code>services.AddLingFanMedia().AddAvaloniaControls();</code></para>
/// <para>可能为空注册或仅注册样式资源。未来可注册默认 Presenter 工厂等服务。</para>
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

        // 空注册。
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
    /// <para><b>U9</b>：默认 Presenter 工厂以 Singleton 注册，供宿主 DI 解析。
    /// 控件 <see cref="VideoView"/> 自身仍通过 <see cref="VideoView.RendererType"/> 静态创建 Presenter
    /// （Avalonia 控件不走 DI 解析，避免引入控件工厂注入耦合），本工厂供需要编程式创建/替换 Presenter
    /// 的宿主或未来控件工厂注入使用。</para>
    /// <para><b>D3D11 GPU Presenter 工厂</b>（<c>AddD3D11Presenter</c>）暂未注册——依赖尚未落地的 Presenter 注册决策
    /// （GPU Presenter 适配器类是否创建）与 Renderers.D3D11 真实实现，属独立 PR 范畴。</para>
    /// <para><b>异步策略</b>：config 分类——纯 DI 注册，无 I/O。</para>
    /// <para><b>AOT 兼容</b>：static 方法，无反射。</para>
    /// </remarks>
    public static MediaBuilder AddSkiaPresenter(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // IVideoPresenterFactory：供需要编程式创建/替换 Presenter 的宿主或控件工厂注入使用。
        builder.Services.AddSingleton<IVideoPresenterFactory, SkiaPresenterFactory>();
        // IVideoRendererFactory：VideoView 回退链路的末级兜底——所有 GPU 渲染器在 Avalonia 控件内
        // 无法合成（Attach 需 Pointer/HWND → 抛 NotSupportedException）时回退到 Skia 软渲染。
        // 注册顺序：VideoView 会强制将其置于 IVideoRendererFactory 列表末位，保证它始终是最终兜底。
        builder.Services.AddSingleton<IVideoRendererFactory, SkiaVideoRendererFactory>();
        return builder;
    }

    /// <summary>
    /// 注册无空域 GPU 上屏渲染器工厂（<see cref="IVideoRendererFactory"/> → <see cref="CompositionVideoRendererFactory"/>，Singleton）。
    /// </summary>
    /// <param name="builder">媒体服务构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    /// <remarks>
    /// <para>无空域 GPU 合成上屏（Avalonia <see cref="Avalonia.Rendering.Composition.ICompositionGpuInterop"/>）：
    /// 解码侧 GPU 硬解纹理经 <see cref="ISharedGpuSurfaceSource"/>（D3D11 适配器等，由 <c>AddD3D11Renderer</c> 注册）
    /// 写入跨设备共享纹理，由宿主合成器直接导入并作为控件子视觉合成——无独占 HWND、无空域、Skia 兜底。</para>
    /// <para><b>依赖</b>：须先调用 <c>AddD3D11Renderer</c>（或将来其他 GPU 后端）注册
    /// <see cref="ISharedGpuSurfaceSourceFactory"/>，否则本工厂在 <see cref="CompositionVideoRenderer.Attach"/> 时
    /// 因无可用共享表面源而抛 <see cref="NotSupportedException"/>，由 VideoView 自动回退 Skia。</para>
    /// <para><b>回退链位置</b>：VideoView 按 DI 顺序尝试——D3D11 SwapChain（控件内因需 Pointer/HWND 抛异常）
    /// → <b>本渲染器</b>（无空域合成，优先于 Skia）→ Skia（末级兜底）。也可用
    /// <c>VideoView.RendererType = typeof(CompositionVideoRenderer)</c> 强制前置。</para>
    /// <para><b>异步策略</b>：config 分类——纯 DI 注册，无 I/O。</para>
    /// <para><b>AOT 兼容</b>：static 方法，无反射。</para>
    /// </remarks>
    public static MediaBuilder AddCompositionRenderer(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // 共享表面源工厂选择记忆（进程级单例）：首次成功选定后缓存胜出厂，后续挂载优先命中，
        // 不再每次从注册序头部逐个探测（Vulkan→D3D11→…→软渲）。对标后端 Lazy<*Backend> 记忆模式。
        builder.Services.AddSingleton<SharedGpuSurfaceSourceSelector>();
        // 类名须含 CompositionVideoRenderer 以支持 RendererType 前置匹配。
        builder.Services.AddSingleton<IVideoRendererFactory, CompositionVideoRendererFactory>();
        // 同 device Skia GPU 直绘（VulkanNativeImage）：注册于 Composition 之后、CPU Skia 之前。
        // Android：Composition 因合成器不支持 VulkanNativeImage 句柄而让位，本渲染器接手
        // 「同 device 直接采样」直绘（零拷贝、无 ByteBuffer）；Windows/Linux/Apple：无
        // VulkanNativeImage 源，Attach 即抛 NotSupportedException，VideoView 自动跳过，
        // 既有 Composition 路径不受影响。
        builder.Services.AddSingleton<IVideoRendererFactory, SkiaGpuVideoRendererFactory>();
        return builder;
    }
}

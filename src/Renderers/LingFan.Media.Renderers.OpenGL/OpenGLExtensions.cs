using System;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL 渲染器 DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddOpenGLRenderer()</code></para>
/// <para>注册 <see cref="OpenGLRendererFactory"/> 为 Singleton。
/// 工厂 <c>Create()</c> 返回的 <see cref="OpenGLRenderer"/> 为桌面 GL 3.3 渲染器（Windows WGL / Linux EGL X11），
/// 已实现 YUV→RGB / RGB 直通 / 软帧 GPU 缩放，调用方按契约使用。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// <para><b>DI 模式（对齐 D3D11 / Vulkan）</b>：先以具体类型 <see cref="OpenGLRendererFactory"/> 为单一真源注册 Singleton，
/// 再由它派生契约服务 <see cref="IVideoRendererFactory"/>。绝不直接
/// <c>AddSingleton&lt;IVideoRendererFactory, OpenGLRendererFactory&gt;()</c>——
/// 那样一旦宿主之后注册别的渲染器（如无头探针、装饰器包裹、或显式指定另一默认后端），
/// 后注册者胜出，任何 <c>(OpenGLRendererFactory)sp.GetRequiredService&lt;IVideoRendererFactory&gt;()</c>
/// 的强制转换都会抛 <see cref="InvalidCastException"/>。具体类型为单一真源后，跨渲染器组合天然成立。</para>
/// <para><b>为何注册 IGpuDeviceContext（与 D3D11 / Vulkan 同源，非"有意省略"）</b>：
/// <list type="bullet">
/// <item>零拷贝与后端无关：解码后端（MF / FFmpeg / VLC）只经中立桥 <see cref="IGpuDeviceContext"/> / <see cref="IGpuTextureResource"/>
/// 产出<b>当前启用渲染器</b>的 API 纹理；三个渲染后端各自为政，只处理自己 API 形态的纹理数据。这才是依赖倒置，而非强依赖某 GPU API。</item>
/// <item>OpenGL 之前"不注册"的判断是<b>错的</b>：GL 上下文虽仍由渲染器实例在 <see cref="OpenGLRenderer.Attach"/> 按窗口建立 on-screen 上下文，
/// 但工厂层面现已维护一个<b>离屏 GL 上下文单例</b>（<see cref="OpenGLRendererFactory.DeviceContext"/>，实现 <see cref="IGpuDeviceContext"/>）作为共享组所有者。
/// 解码后端在 decode-init 阶段即可经此接口获取 OpenGL 设备句柄，on-screen 上下文以共享组接入 → 解码侧 GL 纹理对渲染器可见，零拷贝链路与 D3D11/Vulkan 完全同源。</item>
/// <item>不注册 <see cref="IGpuDeviceContext"/> 才会发出<b>虚假未就绪信号</b>：解码后端拿不到设备句柄 → 只能软解回退（宪法禁止的"假绿，S_OK≠被接受"）。
/// 故此处与 D3D11/Vulkan 一致，以工厂级离屏设备上下文派生注册。</item>
/// <item>注：<see cref="ISharedGpuSurfaceSourceFactory"/>（Avalonia 无空域合成导入路径）属 UI 合成层范围，待 OpenGL 有头合成接入时再按 D3D11/Vulkan 同构补充，
/// 不在此过早挂空能力。</item>
/// </list></para>
/// </remarks>
public static class OpenGLExtensions
{
    /// <summary>
    /// 注册 OpenGL 渲染器（桌面 GL 3.3：Windows WGL / Linux EGL X11）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddOpenGLRenderer(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // 先以具体类型作为单一真源注册 Singleton，再由它派生两个契约服务。
        // 不得写 `(OpenGLRendererFactory)sp.GetRequiredService<IVideoRendererFactory>()`：
        // 后注册的渲染器会胜出，该强制转换直接 InvalidCastException。
        // 工厂 ctor 为 public（DI + AOT 源生成器只解析 public ctor），MS DI 自动解析 ILogger<OpenGLRenderer>。
        builder.Services.AddSingleton<OpenGLRendererFactory>();
        builder.Services.AddSingleton<IVideoRendererFactory>(sp => sp.GetRequiredService<OpenGLRendererFactory>());

        // 中立 GPU 设备上下文（Abstractions 契约），由工厂级离屏 GL 上下文单例注入能力。
        // OpenGLOffscreenDeviceContext 实现 IGpuDeviceContext，Avalonia / 解码后端等层可经此查询 OpenGL 能力并获取
        // 共享组所有者句柄以启用零拷贝，而无需引用具体渲染器模块（依赖倒置严守，与 D3D11/Vulkan 同构）。
        // 不注册则解码后端拿不到设备句柄、只能软解回退（S_OK≠被接受的假绿）。
        builder.Services.AddSingleton<IGpuDeviceContext>(sp => sp.GetRequiredService<OpenGLRendererFactory>().DeviceContext);

        // 中立 GPU 帧生产者桥（Abstractions 契约）：解码后端经此把原生解码输出导入为 OpenGL 纹理（零拷贝上屏），
        // 依赖倒置严守——后端仅依赖 IGpuFrameProducer 抽象，不感知 OpenGL 绑定细节。
        // 解析延迟到消费方（解码器）真正请求时才发生，且仅在 ApiType==OpenGL 时被解码器选用（与 Vulkan 同源守卫）。
        builder.Services.AddSingleton<IGpuFrameProducer>(sp =>
            sp.GetRequiredService<OpenGLRendererFactory>().CreateFrameProducer());

        // E3 后端自动选择：启用且未显式指定时，OpenGL 作为候选默认 GPU 后端（与 D3D11 / Vulkan 同构守卫）。
        // 覆盖 D3D11 不存在的桌面平台（Linux EGL X11）；Windows 上若 D3D11 已先行注册则由其胜出，符合 V1 桌面默认意图。
        if (builder.Options.EnableAutoBackendSelection && builder.Options.DefaultVideoRenderer is null)
        {
            builder.Options.DefaultVideoRenderer = typeof(OpenGLRendererFactory);
        }

        return builder;
    }
}

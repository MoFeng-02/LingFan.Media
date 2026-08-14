using LingFan.Media.Abstractions;

namespace LingFan.Media.Renderers.OpenGLES;

/// <summary>
/// OpenGL ES 渲染器 DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddOpenGLESRenderer()</code></para>
/// <para>注册 <see cref="OpenGLESRendererFactory"/> 为 Singleton。
/// 工厂 <c>Create()</c> 返回的 <see cref="OpenGLESRenderer"/> 为 Android OpenGL ES 3.0 渲染器（GLES 上屏兜底后端），
/// 已实现 YUV→RGB / RGB 直通 / 软帧 GPU 缩放，调用方按契约使用。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// <para><b>DI 模式（对齐 D3D11 / Vulkan / 桌面 OpenGL）</b>：先以具体类型 <see cref="OpenGLESRendererFactory"/> 为单一真源注册 Singleton，
/// 再由它派生契约服务 <see cref="IVideoRendererFactory"/>。绝不直接
/// <c>AddSingleton&lt;IVideoRendererFactory, OpenGLESRendererFactory&gt;()</c>——
/// 那样一旦宿主之后注册别的渲染器（如无头探针、装饰器包裹、或显式指定另一默认后端），
/// 后注册者胜出，任何 <c>(OpenGLESRendererFactory)sp.GetRequiredService&lt;IVideoRendererFactory&gt;()</c>
/// 的强制转换都会抛 <see cref="InvalidCastException"/>。具体类型为单一真源后，跨渲染器组合天然成立。</para>
/// <para><b>何时调用</b>：仅 Android 宿主注册流程中应调用本方法，作为 Vulkan 之后的<b>兜底</b>上屏后端。
/// 非 Android 宿主（Windows 桌面 GL / Linux / Apple）不应调用——本渲染器 <see cref="OpenGLESRenderer.Attach"/> 对非 Android 抛 PNS。</para>
/// <para><b>自动选择守卫（先到先得）</b>：与 D3D11/Vulkan/桌面 OpenGL 同构——仅当 <c>DefaultVideoRenderer</c> 尚为空时设为 GLES 工厂类型。
/// 故 Android 宿主若先 <c>AddVulkanRenderer()</c>（Vulkan 抢占默认），再 <c>AddOpenGLESRenderer()</c>，
/// GLES 不抢占、作为兜底；若仅注册 GLES（无 Vulkan），则 GLES 成为默认。Windows/Linux 上由对应桌面后端先行抢占默认。</para>
/// <para><b>为何不注册 IGpuDeviceContext</b>：GLES 当前无离屏设备上下文（零拷贝共享组属 C 线未来增强），
/// 注册反而会发出"GLES 零拷贝就绪"的虚假信号；解码后端拿不到设备句柄时按契约走软解回退（明确行为，非 S_OK≠被接受的假绿）。</para>
/// </remarks>
public static class OpenGLESExtensions
{
    /// <summary>
    /// 注册 OpenGL ES 渲染器（Android：OpenGL ES 3.0 上屏兜底后端）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddOpenGLESRenderer(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // 先以具体类型作为单一真源注册 Singleton，再由它派生契约服务。
        // 不得写 `(OpenGLESRendererFactory)sp.GetRequiredService<IVideoRendererFactory>()`：
        // 后注册的渲染器会胜出，该强制转换直接 InvalidCastException。
        // 工厂 ctor 为 public（DI + AOT 源生成器只解析 public ctor），MS DI 自动解析 ILogger<OpenGLESRenderer>。
        builder.Services.AddSingleton<OpenGLESRendererFactory>();
        builder.Services.AddSingleton<IVideoRendererFactory>(sp => sp.GetRequiredService<OpenGLESRendererFactory>());

        // 自动选择：仅当 DefaultVideoRenderer 尚为空时设 GLES 为默认（先到先得）。
        // Android 宿主先 AddVulkanRenderer 则 Vulkan 抢默认、GLES 兜底；非 Android 桌面后端先抢默认。
        if (builder.Options.EnableAutoBackendSelection && builder.Options.DefaultVideoRenderer is null)
        {
            builder.Options.DefaultVideoRenderer = typeof(OpenGLESRendererFactory);
        }

        return builder;
    }
}

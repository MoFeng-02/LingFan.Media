namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL 渲染器 DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddOpenGLRenderer()</code></para>
/// <para>注册 <see cref="OpenGLRendererFactory"/> 为 Singleton。
/// 工厂 <c>Create()</c> 返回的 <see cref="OpenGLRenderer"/> 为桌面 GL 3.3 渲染器（Windows WGL / Linux EGL），
/// 已实现 YUV→RGB / RGB 直通 / 软帧 GPU 缩放，调用方按契约使用。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class OpenGLExtensions
{
    /// <summary>
    /// 注册 OpenGL 渲染器（桌面 GL 3.3：Windows WGL / Linux EGL）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddOpenGLRenderer(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IVideoRendererFactory, OpenGLRendererFactory>();

        return builder;
    }
}

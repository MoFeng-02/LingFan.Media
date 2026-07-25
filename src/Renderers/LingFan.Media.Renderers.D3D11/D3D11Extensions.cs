namespace LingFan.Media.Renderers.D3D11;

/// <summary>
/// D3D11 渲染器 DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddD3D11Renderer()</code></para>
/// <para>注册的是工厂（Singleton），不是实例！D3D11Renderer 是 Session 级对象，由工厂 Create() 每次新建。
/// 工厂持有共享 ID3D11Device（Singleton），SwapChain 在 Create() 中独立创建。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class D3D11Extensions
{
    /// <summary>
    /// 注册 D3D11 渲染器（Windows 高性能桌面）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddD3D11Renderer(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IVideoRendererFactory, D3D11RendererFactory>();

        return builder;
    }
}

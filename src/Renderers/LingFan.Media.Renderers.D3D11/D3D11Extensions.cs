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

        // 🔴 先以具体类型注册共享工厂，再由它派生两个契约服务。
        // 绝不要写 `(D3D11RendererFactory)sp.GetRequiredService<IVideoRendererFactory>()`：
        // 那样 IGpuDeviceContext 就被绑死在「D3D11 必须是最后注册的渲染器」这个隐含前提上——
        // 一旦宿主之后再注册别的渲染器（如无头探针 AddHeadlessRenderer()，或装饰器包裹），
        // 后注册者胜出，该强制转换就抛 InvalidCastException。
        // 以具体类型为单一真源后，「用无头渲染器 + D3D11 硬解设备」这类组合天然成立。
        builder.Services.AddSingleton<D3D11RendererFactory>(sp =>
            new D3D11RendererFactory(sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton<IVideoRendererFactory>(sp => sp.GetRequiredService<D3D11RendererFactory>());

        // 中立 GPU 设备上下文（Abstractions 契约），由 D3D11RendererFactory 注入能力。
        // 注册为 Singleton；首次解析时工厂确保设备创建（lazy，同步 native）。
        // RenderContext 实现 IGpuDeviceContext，Avalonia / Outputs 等层可查询 GPU 能力
        // 而无需引用具体渲染器模块（依赖倒置严守）。
        builder.Services.AddSingleton<IGpuDeviceContext>(sp => sp.GetRequiredService<D3D11RendererFactory>().Context);

        // 中立共享表面源工厂（GPU 适配层 ↔ UI 渲染器层之间唯一桥的 D3D11 实现）。
        // 产出 D3D11TextureGlobalSharedHandle 句柄，供 Avalonia ICompositionGpuInterop 无空域导入。
        // 与 IGpuDeviceContext 同源：依赖具体 D3D11RendererFactory 取共享设备，注册为 Singleton。
        builder.Services.AddSingleton<ISharedGpuSurfaceSourceFactory, D3D11SharedSurfaceSourceFactory>();

        // E3 后端自动选择：启用且未显式指定时，D3D11 作为 Windows 默认 GPU 后端。
        if (builder.Options.EnableAutoBackendSelection && builder.Options.DefaultVideoRenderer is null)
        {
            builder.Options.DefaultVideoRenderer = typeof(D3D11RendererFactory);
        }

        return builder;
    }
}

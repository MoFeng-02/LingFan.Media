namespace LingFan.Media.Extensions;

/// <summary>
/// 媒体服务构建器。提供 fluent API 链式注册后端、渲染器、输出等组件。
/// </summary>
/// <remarks>
/// <para>由 <c>AddLingFanMedia()</c> 创建，各 <c>AddXxx()</c> 扩展方法（在各后端/渲染器/输出项目中）
/// 通过 <see cref="Services"/> 注册具体实现，通过 <see cref="Options"/> 读取全局配置。</para>
/// <para>使用模式：<code>services.AddLingFanMedia().AddFFmpeg().AddD3D11Renderer().AddWasapiOutput();</code></para>
/// <para>构造函数为 <see langword="internal"/>，确保 <see cref="MediaBuilder"/> 只能通过
/// <c>AddLingFanMedia()</c> 创建，不可直接 new。</para>
/// <para>各 <c>AddXxx()</c> 链式扩展方法均为同步配置（config 分类），无 I/O、无异步。</para>
/// </remarks>
public sealed class MediaBuilder
{
    /// <summary>DI 服务集合。各扩展方法通过此属性注册具体实现。</summary>
    public IServiceCollection Services { get; }

    /// <summary>全局媒体配置。各扩展方法可读取配置调整注册行为。</summary>
    public MediaOptions Options { get; }

    /// <summary>
    /// 初始化 <see cref="MediaBuilder"/> 的新实例。
    /// </summary>
    /// <param name="services">DI 服务集合。</param>
    /// <param name="options">全局媒体配置。</param>
    /// <remarks>
    /// 构造函数为 <see langword="internal"/>，仅供
    /// <see cref="ServiceCollectionExtensions.AddLingFanMedia"/> 调用。
    /// </remarks>
    internal MediaBuilder(IServiceCollection services, MediaOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        Services = services;
        Options = options;
    }
}

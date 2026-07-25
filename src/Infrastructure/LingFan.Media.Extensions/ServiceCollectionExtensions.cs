using LingFan.Media.Core;
using LingFan.Media.Sources;
using LingFan.Media.Formats;

namespace LingFan.Media.Extensions;

/// <summary>
/// DI 注册主入口。提供 <see cref="AddLingFanMedia"/> 扩展方法注册核心媒体服务。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddFFmpeg().AddD3D11Renderer().AddWasapiOutput();</code></para>
/// <para><b>生命周期模型</b>：</para>
/// <para>Infrastructure（Singleton）：无状态工厂 / 共享资源。</para>
/// <para>- <see cref="IMediaStreamFactory"/> → <see cref="MediaStreamFactory"/>（持有 <c>IHttpClientFactory</c>）</para>
/// <para>- <see cref="IMediaDemuxerFactory"/> → <see cref="Formats.DemuxerFactory"/>（被 <c>AddFFmpeg()</c> 覆盖）</para>
/// <para>- <see cref="IMediaPlayerFactory"/> → <see cref="MediaPlayerFactory"/></para>
/// <para>Session（Transient）：仅注册 <see cref="IMediaPlayer"/>，内部组件由 Factory 手动 new 不走 DI。</para>
/// <para><b>以下工厂由各子模块扩展方法注册（不在 AddLingFanMedia 中）</b>：</para>
/// <para>- <c>AddFFmpeg()</c> → <see cref="IVideoDecoderFactory"/> / <see cref="IAudioDecoderFactory"/> /
/// <see cref="ISubtitleDecoderFactory"/> / <see cref="IMediaDemuxerFactory"/>（覆盖默认）</para>
/// <para>- <c>AddD3D11Renderer()</c> → <see cref="IVideoRendererFactory"/></para>
/// <para>- <c>AddWasapiOutput()</c> → <see cref="IAudioOutputFactory"/></para>
/// <para>此方法为同步配置（config 分类），无 I/O、无异步。</para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 LingFan.Media 核心服务（工厂 + 播放器）并返回 <see cref="MediaBuilder"/> 供链式注册。
    /// </summary>
    /// <param name="services">DI 服务集合。</param>
    /// <param name="configure">全局媒体配置回调（可选）。</param>
    /// <returns>媒体构建器，用于链式调用 <c>AddFFmpeg()</c> / <c>AddD3D11Renderer()</c> 等。</returns>
    /// <remarks>
    /// <para>注册 Infrastructure Lifetime（Singleton）工厂：</para>
    /// <para>- <see cref="IMediaStreamFactory"/> → <see cref="MediaStreamFactory"/></para>
    /// <para>- <see cref="IMediaDemuxerFactory"/> → <see cref="Formats.DemuxerFactory"/></para>
    /// <para>- <see cref="IMediaPlayerFactory"/> → <see cref="MediaPlayerFactory"/></para>
    /// <para>注册 Session Lifetime（Transient）播放器：</para>
    /// <para>- <see cref="IMediaPlayer"/>（每次解析通过 Factory.Create() 新建）</para>
    /// <para>注册 <c>IHttpClientFactory</c>（<c>AddHttpClient()</c>）供 <see cref="MediaStreamFactory"/> 网络流连接池管理。</para>
    /// <para>注册 <c>IOptions&lt;MediaOptions&gt;</c> 供后续读取全局配置。</para>
    /// <para>日志配置由消费方应用的 Host 负责（Extensions 层只依赖 <c>Logging.Abstractions</c>）。</para>
    /// </remarks>
    public static MediaBuilder AddLingFanMedia(
        this IServiceCollection services,
        Action<MediaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MediaOptions();
        configure?.Invoke(options);

        // ── Infrastructure Lifetime（Singleton：无状态工厂 / 共享资源）──

        // IHttpClientFactory：供 MediaStreamFactory 网络流连接池管理（防套接字耗尽）
        services.AddHttpClient();

        // 媒体流工厂（Singleton，持有 IHttpClientFactory 引用）
        services.AddSingleton<IMediaStreamFactory, MediaStreamFactory>();

        // 解封装工厂（Singleton，被 AddFFmpeg() 覆盖为 FFmpegDemuxerFactory）
        services.AddSingleton<IMediaDemuxerFactory, DemuxerFactory>();

        // 播放器工厂（Singleton，无状态，只负责 new）
        services.AddSingleton<IMediaPlayerFactory, MediaPlayerFactory>();

        // 未来：services.AddSingleton<ICodecRegistry, CodecRegistry>();
        // 未来：services.AddSingleton<IGpuDeviceContext, GpuDeviceContext>();

        // ── 配置 ──

        // 注册 IOptions 服务（AddOptions 来自 Microsoft.Extensions.Options）
        services.AddOptions();

        // 将 MediaOptions 绑定到 IOptions<MediaOptions>，供后续服务读取
        services.Configure<MediaOptions>(o => options.CopyTo(o));

        // ── Session Lifetime（Transient：仅注册 IMediaPlayer，内部组件由 Factory 手动 new）──
        services.AddTransient<IMediaPlayer>(sp =>
        {
            var factory = sp.GetRequiredService<IMediaPlayerFactory>();
            return factory.Create();
        });

        return new MediaBuilder(services, options);
    }

    /// <summary>
    /// 注册 LingFan.Media 核心服务并返回 <see cref="MediaBuilder"/> 供链式注册。
    /// </summary>
    /// <param name="services">DI 服务集合。</param>
    /// <param name="options">全局媒体配置。</param>
    /// <returns>媒体构建器，用于链式调用 <c>AddFFmpeg()</c> / <c>AddD3D11Renderer()</c> 等。</returns>
    public static MediaBuilder AddLingFanMedia(
        this IServiceCollection services,
        MediaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services.AddLingFanMedia(o => options.CopyTo(o));
    }
}

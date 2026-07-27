using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using LingFan.Media.Audio;
using LingFan.Media.Core;
using LingFan.Media.Formats;
using LingFan.Media.Sources;
using LingFan.Media.Video;
using Microsoft.Extensions.Options;

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

        // SSL 证书绕过专用命名 client：仅 AllowInsecureHttps 的网络源经
        // IHttpClientFactory.CreateClient("LingFanMedia_Insecure") 获取（S2 统一入口）。
        // 自定义 SocketsHttpHandler 由工厂管理生命周期，避免套接字耗尽。
        services.AddHttpClient("LingFanMedia_Insecure")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
                SslOptions = new SslClientAuthenticationOptions
                {
                    // 绕过证书校验（仅用于显式 AllowInsecureHttps 场景）
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                },
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            });

        // 媒体流工厂（Singleton，持有 IHttpClientFactory 引用）
        services.AddSingleton<IMediaStreamFactory, MediaStreamFactory>();

        // 解封装工厂（Singleton，被 AddFFmpeg() 覆盖为 FFmpegDemuxerFactory）
        services.AddSingleton<IMediaDemuxerFactory, DemuxerFactory>();

        // 播放器工厂注册见下方（在构建器创建后，透传宿主配置的后处理链与重置钩子）。

        // 编解码器注册表（E1）：静态映射表，纯内存，Singleton。
        services.AddSingleton<ICodecRegistry, CodecRegistry>();
        // IGpuDeviceContext 的注册位于 Renderers.D3D11 的 AddD3D11Renderer
        // （具体工厂依赖 Vortice，Extensions 层不引用渲染器模块，严守分层）。

        // ── 配置 ──

        // 注册 IOptions 服务（AddOptions 来自 Microsoft.Extensions.Options）
        services.AddOptions();

        // 将 MediaOptions 绑定到 IOptions<MediaOptions>，供后续服务读取
        services.Configure<MediaOptions>(o => options.CopyTo(o));

        // 播放器默认配置（契约层 MediaPlayerOptions）经 IOptions 绑定，使宿主配置的 DefaultVolume 传播到 Core 工厂。
        // 此前 MediaPlayerOptions 仅在 Core 定义且从未注册，导致工厂始终走默认 1.0 —— 此处闭合该缺口（V2-06 接线期遗漏）。
        services.Configure<MediaPlayerOptions>(o => o.DefaultVolume = options.DefaultVolume);

        // ── Session Lifetime（Transient：仅注册 IMediaPlayer，内部组件由 Factory 手动 new）──
        services.AddTransient<IMediaPlayer>(sp =>
        {
            var factory = sp.GetRequiredService<IMediaPlayerFactory>();
            return factory.Create();
        });

        var builder = new MediaBuilder(services, options);

        // 播放器工厂（Singleton）：延迟构造，透传宿主经 WithAudioPipeline/WithVideoPipeline 配置的后处理链与重置钩子
        // （V2-06 C5/C6 / V2-07 / V2-08.1）。未配置时 transforms/reset 为 null → V1 完全兼容。
        // 工厂委托在首次解析 IMediaPlayerFactory 时执行，此时所有 AddXxx 链式调用（含 WithAudioPipeline/WithVideoPipeline）已完成，
        // 故可安全读取 builder 中的宿主配置。
        services.AddSingleton<IMediaPlayerFactory>(sp =>
        {
            var streamFactory = sp.GetRequiredService<IMediaStreamFactory>();
            var demuxerFactory = sp.GetRequiredService<IMediaDemuxerFactory>();
            var videoDecoderFactory = sp.GetRequiredService<IVideoDecoderFactory>();
            var audioDecoderFactory = sp.GetRequiredService<IAudioDecoderFactory>();
            var subtitleDecoderFactory = sp.GetService<ISubtitleDecoderFactory>();
            var videoRendererFactory = sp.GetRequiredService<IVideoRendererFactory>();
            var audioOutputFactory = sp.GetRequiredService<IAudioOutputFactory>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var playerOptions = sp.GetService<IOptions<MediaPlayerOptions>>();

            // 中立 BCL 委托桥：宿主配置经 Audio/Video 模块收敛为 Func<,>/Action，Core 不依赖具体模块（依赖倒置严守）。
            // 以下委托已由 WithAudioPipeline/WithVideoPipeline/WithAudioTransforms/WithVideoTransforms 在配置阶段解析完成。
            var videoTransforms = builder.VideoTransforms;
            var audioTransforms = builder.AudioTransforms;
            var videoTransformsReset = builder.VideoTransformsReset;
            var audioTransformsReset = builder.AudioTransformsReset;

            return new MediaPlayerFactory(
                streamFactory, demuxerFactory, videoDecoderFactory, audioDecoderFactory,
                subtitleDecoderFactory, videoRendererFactory, audioOutputFactory, loggerFactory,
                playerOptions,
                videoTransforms, audioTransforms, videoTransformsReset, audioTransformsReset);
        });

        return builder;
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

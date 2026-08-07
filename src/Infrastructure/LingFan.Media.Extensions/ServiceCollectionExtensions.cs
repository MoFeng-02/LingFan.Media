using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using LingFan.Media.Audio;
using LingFan.Media.Core;
using LingFan.Media.Formats;
using LingFan.Media.Formats.Detection;
using LingFan.Media.Sources;
using LingFan.Media.Sources.Security;
using LingFan.Media.Video;
using LingFan.Media.Playback;
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
/// <para>- <see cref="IMediaDemuxerFactory"/> → 由各后端 <c>AddXxx()</c> 经 TryAddEnumerable 集合注册（不再默认注册，支持多后端并存回退）</para>
/// <para>- <see cref="IMediaPlayerFactory"/> → <see cref="MediaPlayerFactory"/></para>
/// <para>Session（Transient）：仅注册 <see cref="IMediaPlayer"/>，内部组件由 Factory 手动 new 不走 DI。</para>
/// <para><b>以下工厂由各子模块扩展方法注册（不在 AddLingFanMedia 中）</b>：</para>
/// <para>- <c>AddFFmpeg()</c> / <c>AddMediaFoundation()</c> / <c>AddVLC()</c> → 各以 TryAddEnumerable 集合注册
/// <see cref="IMediaDemuxerFactory"/> / <see cref="IVideoDecoderFactory"/> / <see cref="IAudioDecoderFactory"/> / <see cref="ISubtitleDecoderFactory"/>（可多后端并存，按注册顺序回退）</para>
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

        // 通用 Lazy<T> 解析支持：MS DI 默认不自动解析 Lazy<T>（仅集合类型）。
        // 注册后 Lazy<T>.Value 才延迟解析 T，用于把后端原生初始化延迟到 Open（宪法纪律）。
        services.AddLazySupport();

        var options = new MediaOptions();
        configure?.Invoke(options);

        // ── Infrastructure Lifetime（Singleton：无状态工厂 / 共享资源）──

        // IHttpClientFactory：供 MediaStreamFactory 网络流连接池管理（防套接字耗尽）
        services.AddHttpClient();

        // B-DNS: 网络流默认命名 client——SocketsHttpHandler.ConnectCallback 挂载
        // SsrfConnectGuard（DNS pinning：只连 NetworkMediaStream 校验过的 IP，
        // 闭合「SsrfGuard.Validate 后 SendAsync 二次 DNS 解析」的重绑定 TOCTOU 窗口；
        // 重定向到新主机时回调内现场重解析 + 重校验）。
        services.AddHttpClient("LingFanMedia")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = SsrfConnectGuard.ConnectAsync,
            });

        // SSL 证书绕过专用命名 client：仅 AllowInsecureHttps 的网络源经
        // IHttpClientFactory.CreateClient("LingFanMedia_Insecure") 获取（S2 统一入口）。
        // 自定义 SocketsHttpHandler 由工厂管理生命周期，避免套接字耗尽。
        // B-DNS: 同样挂载 SsrfConnectGuard（不安全 SSL 场景同样需要 SSRF 防护）。
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
                ConnectCallback = SsrfConnectGuard.ConnectAsync,
            });

        // 媒体流工厂（Singleton，持有 IHttpClientFactory 引用）
        services.AddSingleton<IMediaStreamFactory, MediaStreamFactory>();

        // 格式探测器（契约 IFormatDetector → 具体 FormatDetector，位于 LingFan.Media.Formats）：
        // 供回退调度器在 Open 前轻量探测 (容器, 视频编码) 以提前命中「格式级记忆」、跳过已知坏后端。
        // 高层中间件（LingFan.Media.Playback）仅依赖 IFormatDetector 契约，不引用具体实现，严守依赖倒置。
        services.AddSingleton<IFormatDetector, FormatDetector>();

        // 解封装工厂不再在此默认注册：由各后端 AddXxx() 经 TryAddEnumerable 集合注册，
        // 中间件按 DI 注册顺序做运行时回退。未注册任何后端时 Create 会在 Open 时抛 MediaBackendUnsupportedException。

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
        // 核心 composer（keyed "composer"）：保留原构造逻辑，供回退中间件在选定后端组后调用其 Create(...) 重载建 Session。
        services.AddKeyedSingleton<IMediaPlayerFactory>("composer", (sp, key) =>
        {
            var streamFactory = sp.GetRequiredService<IMediaStreamFactory>();
            var demuxerFactories = sp.GetServices<IMediaDemuxerFactory>();
            var videoDecoderFactories = sp.GetServices<IVideoDecoderFactory>();
            var audioDecoderFactories = sp.GetServices<IAudioDecoderFactory>();
            var subtitleDecoderFactories = sp.GetServices<ISubtitleDecoderFactory>();
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
                streamFactory, demuxerFactories, videoDecoderFactories, audioDecoderFactories,
                subtitleDecoderFactories, videoRendererFactory, audioOutputFactory, loggerFactory,
                playerOptions,
                videoTransforms, audioTransforms, videoTransformsReset, audioTransformsReset);
        });

        // 回退中间件（契约纯净，仅依赖 Abstractions + DI.Abstractions）：同时以 IMediaPlayerFactory（对外调度）
        // 与 IBackendRegistry（只读检视后端组）两个契约对外。两者必须指向同一 Singleton 实例——
        // 否则会解析出两个对象图、各持一份回退 Cache，导致「命中缓存」语义失效。
        services.AddSingleton<BackendFallbackMediaPlayerFactory>();
        services.AddSingleton<IMediaPlayerFactory>(sp => sp.GetRequiredService<BackendFallbackMediaPlayerFactory>());
        services.AddSingleton<IBackendRegistry>(sp => sp.GetRequiredService<BackendFallbackMediaPlayerFactory>());

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

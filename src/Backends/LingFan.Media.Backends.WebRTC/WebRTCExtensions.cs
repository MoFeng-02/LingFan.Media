using LingFan.Media.Backends.WebRTC.Demuxer;
using LingFan.Media.Backends.WebRTC.Decoders;
using LingFan.Media.Extensions;

namespace LingFan.Media.Backends.WebRTC;

/// <summary>
/// WebRTC 后端 DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddWebRTC(options => { ... })</code></para>
/// <para>注册的是工厂（Singleton），不是实例。</para>
/// <para><b>注意</b>：WebRTC 后端需要原生 WebRTC 库，当前未集成。
/// DI 注册成功，但运行时操作抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// <para>从 BackendStubs.cs 迁移真实实现（Task-V2-14 B4）。</para>
/// </remarks>
public static class WebRTCExtensions
{
    /// <summary>
    /// 注册 WebRTC 后端（Demuxer + VideoDecoder + AudioDecoder）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">WebRTC 配置回调（可选）。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddWebRTC(
        this MediaBuilder builder,
        Action<WebRTCOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new WebRTCOptions();
        configure?.Invoke(options);

        builder.Services.AddSingleton<WebRTCBackend>();
        builder.Services.AddSingleton(options);

        builder.Services.AddSingleton<IMediaDemuxerFactory, WebRTCDemuxerFactory>();
        builder.Services.AddSingleton<IVideoDecoderFactory, WebRTCVideoDecoderFactory>();
        builder.Services.AddSingleton<IAudioDecoderFactory, WebRTCAudioDecoderFactory>();

        return builder;
    }
}

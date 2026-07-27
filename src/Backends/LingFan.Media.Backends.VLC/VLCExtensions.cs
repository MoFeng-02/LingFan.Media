using LingFan.Media.Backends.VLC.Decoders;
using LingFan.Media.Backends.VLC.Demuxer;
using LingFan.Media.Extensions;

namespace LingFan.Media.Backends.VLC;

/// <summary>
/// VLC 后端 DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddVLC(options => { ... })</code></para>
/// <para>注册的是工厂（Singleton），不是实例！Demuxer/Decoder 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>VLCBackend 作为 Singleton 是安全的——只持有 LibVLC 引擎实例。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// <para>从 BackendStubs.cs 迁移真实实现（Task-V2-14 B1）。</para>
/// </remarks>
public static class VLCExtensions
{
    /// <summary>
    /// 注册 VLC 后端（Demuxer + VideoDecoder + AudioDecoder）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">VLC 配置回调（可选）。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddVLC(
        this MediaBuilder builder,
        Action<VLCOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new VLCOptions();
        configure?.Invoke(options);

        // 注册 VLC 后端入口（Singleton，持有 LibVLC 引擎实例）
        builder.Services.AddSingleton<VLCBackend>();
        builder.Services.AddSingleton(options);

        // 注册工厂（Singleton，无状态）
        builder.Services.AddSingleton<IMediaDemuxerFactory, VLCDemuxerFactory>();
        builder.Services.AddSingleton<IVideoDecoderFactory, VLCVideoDecoderFactory>();
        builder.Services.AddSingleton<IAudioDecoderFactory, VLCAudioDecoderFactory>();

        return builder;
    }
}

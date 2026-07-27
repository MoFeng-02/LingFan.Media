using LingFan.Media.Backends.MediaFoundation.Decoders;
using LingFan.Media.Backends.MediaFoundation.Demuxer;
using LingFan.Media.Extensions;

namespace LingFan.Media.Backends.MediaFoundation;

/// <summary>
/// MediaFoundation 后端 DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddMediaFoundation(options => { ... })</code></para>
/// <para>注册的是工厂（Singleton），不是实例！Demuxer/Decoder 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>MFBackend 作为 Singleton 是安全的——只持有 MFStartup 全局状态。</para>
/// <para><b>仅 Windows 可用</b>：MFBackend 构造时检测平台，非 Windows 抛 PlatformNotSupportedException。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// <para>从 BackendStubs.cs 迁移真实实现（Task-V2-14 B2）。</para>
/// </remarks>
public static class MFExtensions
{
    /// <summary>
    /// 注册 MediaFoundation 后端（Demuxer + VideoDecoder + AudioDecoder）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">MediaFoundation 配置回调（可选）。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddMediaFoundation(
        this MediaBuilder builder,
        Action<MediaFoundationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new MediaFoundationOptions();
        configure?.Invoke(options);

        // 注册 MF 后端入口（Singleton，持有 MFStartup 全局状态）
        builder.Services.AddSingleton<MFBackend>();
        builder.Services.AddSingleton(options);

        // 注册工厂（Singleton，无状态）
        builder.Services.AddSingleton<IMediaDemuxerFactory, MFDemuxerFactory>();
        builder.Services.AddSingleton<IVideoDecoderFactory, MFVideoDecoderFactory>();
        builder.Services.AddSingleton<IAudioDecoderFactory, MFAudioDecoderFactory>();

        return builder;
    }
}

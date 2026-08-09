using LingFan.Media.Backends.FFmpeg.Decoders;
using LingFan.Media.Backends.FFmpeg.Demuxer;
using LingFan.Media.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LingFan.Media.Backends.FFmpeg;

/// <summary>
/// FFmpeg 后端 DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddFFmpeg(options => { ... })</code></para>
/// <para>注册的是工厂（Singleton），不是实例！Demuxer/Decoder 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>FFmpegBackend 作为 Singleton 是安全的——只持有全局初始化状态。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class FFmpegExtensions
{
    /// <summary>
    /// 注册 FFmpeg 后端（Demuxer + VideoDecoder + AudioDecoder + SubtitleDecoder）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">FFmpeg 配置回调（可选）。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddFFmpeg(
        this MediaBuilder builder,
        Action<FFmpegOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new FFmpegOptions();
        configure?.Invoke(options);

        // 注册 FFmpegOptions（Singleton）：宿主可在运行时解析并设置 MediaCodecSurface（注入点）
        builder.Services.AddSingleton(options);

        // 注册 FFmpeg 后端入口（Singleton，持有全局初始化状态）。
        // 🔴 此处【不】调用任何 ffmpeg.* 原生 API——原生初始化延迟到 FFmpegBackend 首次构造时执行，
        // 以保持 AddFFmpeg() 注册阶段是纯 DI、不要求 ffmpeg 原生 DLL 在注册期就位。
        // 只有真正回退用到 FFmpeg 后端（首次解析 FFmpegBackend）时才需要原生 DLL 在场，
        // 符合“开箱即用 + 不侵入”：注册一个后端 ≠ 马上要它的 native 库（MF 直接支持的源绝不触碰 ffmpeg）。
        builder.Services.AddSingleton<FFmpegBackend>();

        // 注册工厂（集合注册 TryAddEnumerable：支持多后端并存、按 DI 注册顺序参与运行时回退）
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMediaDemuxerFactory, FFmpegDemuxerFactory>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IVideoDecoderFactory, FFmpegVideoDecoderFactory>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAudioDecoderFactory, FFmpegAudioDecoderFactory>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ISubtitleDecoderFactory, FFmpegSubtitleDecoderFactory>());

        return builder;
    }
}

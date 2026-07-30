using LingFan.Media.Backends.FFmpeg.Decoders;
using LingFan.Media.Backends.FFmpeg.Demuxer;
using LingFan.Media.Extensions;

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

        // 设置原生库路径（如果指定）
        if (!string.IsNullOrEmpty(options.FFmpegLibraryPath))
        {
            FFmpegBackend.SetLibraryPath(options.FFmpegLibraryPath!);
        }

        // 设置 FFmpeg 日志级别
        unsafe
        {
            ffmpeg.av_log_set_level(options.LogLevel);
        }

        // 注册 FFmpegOptions（Singleton）：宿主可在运行时解析并设置 MediaCodecSurface（V2-17 B9 注入点）
        builder.Services.AddSingleton(options);

        // 注册 FFmpeg 后端入口（Singleton，持有全局初始化状态）
        builder.Services.AddSingleton<FFmpegBackend>();

        // 注册工厂（Singleton，无状态）
        builder.Services.AddSingleton<IMediaDemuxerFactory, FFmpegDemuxerFactory>();
        builder.Services.AddSingleton<IVideoDecoderFactory, FFmpegVideoDecoderFactory>();
        builder.Services.AddSingleton<IAudioDecoderFactory, FFmpegAudioDecoderFactory>();
        builder.Services.AddSingleton<ISubtitleDecoderFactory, FFmpegSubtitleDecoderFactory>();

        return builder;
    }
}

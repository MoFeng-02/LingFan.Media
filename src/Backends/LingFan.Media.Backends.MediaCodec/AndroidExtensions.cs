using LingFan.Media.Backends.MediaCodec.Decoders;
using LingFan.Media.Backends.MediaCodec.Demuxer;
using LingFan.Media.Extensions;

namespace LingFan.Media.Backends.MediaCodec;

/// <summary>
/// Android 后端（MediaExtractor + MediaCodec）DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddMediaCodec(options =&gt; { ... })</code></para>
/// <para>注册的是工厂（Singleton），不是实例！Demuxer/Decoder 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>AndroidBackend 作为 Singleton 是安全的——只持有选项与平台能力标记（无 NDK 全局状态需要释放）。</para>
/// <para><b>仅 Android 可用</b>：AndroidBackend 构造时不做平台检查（允许 DI 注册），
/// 实际平台检查在 demuxer.OpenAsync / decoder.Initialize 内执行（<see cref="OperatingSystem.IsAndroid"/>）。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// <para>依赖倒置：本后端只依赖 Abstractions 契约（IMediaDemuxerFactory / IVideoDecoderFactory /
/// IAudioDecoderFactory），绝不引用任何 Renderers 程序集，也不依赖 <see cref="IGpuFrameProducer"/>。
/// 解码输出为 CPU 侧 <c>Image.Plane</c> 提取的标准 I420 帧（GPU 零拷贝暂缓，见设计文档 §5.2），
/// 后端与渲染器互不感知。</para>
/// </remarks>
public static class AndroidExtensions
{
    /// <summary>
    /// 注册 Android 后端（Demuxer + VideoDecoder + AudioDecoder）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">Android 后端配置回调（可选）。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddMediaCodec(
        this MediaBuilder builder,
        Action<AndroidOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AndroidOptions();
        configure?.Invoke(options);

        // 注册 Android 后端入口（Singleton，持有选项与平台能力标记，无原生全局状态）
        builder.Services.AddSingleton<AndroidBackend>();
        builder.Services.AddSingleton(options);

        // 注册工厂（集合注册 TryAddEnumerable：支持多后端并存、按 DI 注册顺序参与运行时回退）
        // 【必须类型化注册】工厂委托形式 Singleton<IVideoDecoderFactory>(sp => ...) 会被 TryAddEnumerable
        // 拒绝：委托类型 Func<IServiceProvider, IVideoDecoderFactory> 的返回类型与服务类型相同 →
        // 「indistinguishable」ArgumentException，启动即崩（真机 crash.txt 实证）。
        // 类型化注册同样与注册顺序无关：构造解析发生在播放期（GetServices 物化），那时各后端已注册。
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMediaDemuxerFactory, AndroidDemuxerFactory>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IVideoDecoderFactory, AndroidVideoDecoderFactory>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAudioDecoderFactory, AndroidAudioDecoderFactory>());

        return builder;
    }
}

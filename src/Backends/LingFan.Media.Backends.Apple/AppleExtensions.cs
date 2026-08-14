using LingFan.Media.Backends.Apple.Decoders;
using LingFan.Media.Backends.Apple.Demuxer;
using LingFan.Media.Extensions;

namespace LingFan.Media.Backends.Apple;

/// <summary>
/// Apple 后端（AVAssetReader passthrough + VideoToolbox 解码）DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddApple(options =&gt; { ... })</code></para>
/// <para>注册的是工厂（Singleton），不是实例！Demuxer/Decoder 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>AppleBackend 作为 Singleton 是安全的——只持有选项与平台能力标记（无 Apple 原生全局状态需要释放）。</para>
/// <para><b>仅 Apple 可用</b>：AppleBackend 构造时不做平台检查（允许 DI 注册），
/// 实际平台检查在 demuxer.OpenAsync / decoder.Initialize 内执行（<see cref="OperatingSystem.IsMacOS"/> / <see cref="OperatingSystem.IsIOS"/>）。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// <para>依赖倒置：本后端只依赖 Abstractions 契约（IMediaDemuxerFactory / IVideoDecoderFactory /
/// IAudioDecoderFactory / IGpuFrameProducer），绝不引用任何 Renderers 程序集。零拷贝经
/// <see cref="IGpuFrameProducer"/> 抽象由渲染器侧生产者消费，后端与渲染器互不感知。</para>
/// </remarks>
public static class AppleExtensions
{
    /// <summary>
    /// 注册 Apple 后端（Demuxer + VideoDecoder + AudioDecoder）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">Apple 后端配置回调（可选）。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddApple(
        this MediaBuilder builder,
        Action<AppleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AppleOptions();
        configure?.Invoke(options);

        // 注册 Apple 后端入口（Singleton，持有选项与平台能力标记，无原生全局状态）
        builder.Services.AddSingleton<AppleBackend>();
        builder.Services.AddSingleton(options);

        // 注册工厂（集合注册 TryAddEnumerable：支持多后端并存、按 DI 注册顺序参与运行时回退）
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMediaDemuxerFactory, AppleDemuxerFactory>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IVideoDecoderFactory, AppleVideoDecoderFactory>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAudioDecoderFactory, AppleAudioDecoderFactory>());

        return builder;
    }
}

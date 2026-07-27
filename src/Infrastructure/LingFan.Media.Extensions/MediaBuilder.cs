using System;
using LingFan.Media.Audio;
using LingFan.Media.Video;

namespace LingFan.Media.Extensions;

/// <summary>
/// 媒体服务构建器。提供 fluent API 链式注册后端、渲染器、输出等组件。
/// </summary>
/// <remarks>
/// <para>由 <c>AddLingFanMedia()</c> 创建，各 <c>AddXxx()</c> 扩展方法（在各后端/渲染器/输出项目中）
/// 通过 <see cref="Services"/> 注册具体实现，通过 <see cref="Options"/> 读取全局配置。</para>
/// <para>使用模式：<code>services.AddLingFanMedia().AddFFmpeg().AddD3D11Renderer().AddWasapiOutput();</code></para>
/// <para>构造函数为 <see langword="internal"/>，确保 <see cref="MediaBuilder"/> 只能通过
/// <c>AddLingFanMedia()</c> 创建，不可直接 new。</para>
/// <para>各 <c>AddXxx()</c> 链式扩展方法均为同步配置（config 分类），无 I/O、无异步。</para>
/// </remarks>
public sealed class MediaBuilder
{
    /// <summary>DI 服务集合。各扩展方法通过此属性注册具体实现。</summary>
    public IServiceCollection Services { get; }

    /// <summary>全局媒体配置。各扩展方法可读取配置调整注册行为。</summary>
    public MediaOptions Options { get; }

    /// <summary>
    /// 音频后处理变换链（中立 BCL 委托），由 <see cref="WithAudioPipeline"/> / <see cref="WithAudioTransforms"/> 注入，
    /// 透传至 Core <c>AudioPipeline</c>（V2-06 C6 / V2-08.1）。null = 不注入 → V1 兼容。
    /// </summary>
    internal IReadOnlyList<Func<AudioFrame, AudioFrame>>? AudioTransforms { get; set; }

    /// <summary>
    /// 音频效果状态重置钩子（中立 BCL 委托），由 <see cref="WithAudioPipeline"/> / <see cref="WithAudioTransforms"/> 注入，
    /// 供 Core <c>AudioPipeline</c> 在 Seek/Flush 解码锁内调用（V2-08.1）。null = 不注入。
    /// </summary>
    internal Action? AudioTransformsReset { get; set; }

    /// <summary>
    /// 视频后处理变换链（中立 BCL 委托），由 <see cref="WithVideoPipeline"/> / <see cref="WithVideoTransforms"/> 注入，
    /// 透传至 Core <c>VideoPipeline</c>（V2-06 C5 / V2-07）。null = 不注入 → V1 兼容。
    /// </summary>
    internal IReadOnlyList<Func<VideoFrame, VideoFrame?>>? VideoTransforms { get; set; }

    /// <summary>
    /// 视频后处理状态重置钩子（中立 BCL 委托），由 <see cref="WithVideoPipeline"/> / <see cref="WithVideoTransforms"/> 注入，
    /// 供 Core <c>VideoPipeline</c> 在 Seek/Flush 解码锁内调用（V2-07）。null = 不注入。
    /// </summary>
    internal Action? VideoTransformsReset { get; set; }

    /// <summary>
    /// 初始化 <see cref="MediaBuilder"/> 的新实例。
    /// </summary>
    /// <param name="services">DI 服务集合。</param>
    /// <param name="options">全局媒体配置。</param>
    /// <remarks>
    /// 构造函数为 <see langword="internal"/>，仅供
    /// <see cref="ServiceCollectionExtensions.AddLingFanMedia"/> 调用。
    /// </remarks>
    internal MediaBuilder(IServiceCollection services, MediaOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        Services = services;
        Options = options;
    }

    /// <summary>
    /// 注入音频管线配置（V2-06 C6 / V2-08.1），将效果链与重置钩子透传至 Core 音频管线。
    /// </summary>
    /// <param name="config">音频管线配置（含效果链及混音设置）。</param>
    /// <returns>同一构建器，便于链式调用。</returns>
    /// <remarks>
    /// 宿主可保留 <paramref name="config"/> 引用，在运行时通过 <see cref="IAudioEffect.IsEnabled"/> 调整单个效果启停；
    /// <c>ToTransforms()</c> / <see cref="AudioPipelineConfig.ResetEffects"/> 闭包捕获的效果实例与此引用相同，运行时切换即时生效。
    /// 不调用本方法则音频后处理与重置钩子为 null → V1 兼容。
    /// 如需同时接入音量控制 / 混音，请改用 <see cref="WithAudioTransforms"/> 自行组合（或在本方法之后另行调用以覆盖）。
    /// </remarks>
    public MediaBuilder WithAudioPipeline(AudioPipelineConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        AudioTransforms = config.ToTransforms();
        AudioTransformsReset = config.ResetEffects();
        return this;
    }

    /// <summary>
    /// 直接注入已组合好的音频后处理变换链与重置钩子（V2-06 C4/C6 / V2-08.1）。
    /// 适用于需将音量控制（<see cref="AudioPipelineTransforms.FromVolume"/>）、混音（<see cref="AudioPipelineTransforms.FromMixer"/>）
    /// 与效果链（<see cref="AudioPipelineConfig.ToTransforms"/>）组合后再注入的场景。
    /// </summary>
    /// <param name="transforms">组合后的音频变换链（非 null）。</param>
    /// <param name="reset">组合后的状态重置钩子（可为 null）。</param>
    /// <returns>同一构建器，便于链式调用。</returns>
    public MediaBuilder WithAudioTransforms(
        IReadOnlyList<Func<AudioFrame, AudioFrame>> transforms,
        Action? reset = null)
    {
        AudioTransforms = transforms ?? throw new ArgumentNullException(nameof(transforms));
        AudioTransformsReset = reset;
        return this;
    }

    /// <summary>
    /// 注入视频管线配置（V2-06 C5 / V2-07），将后处理链与重置钩子透传至 Core 视频管线。
    /// </summary>
    /// <param name="config">视频管线配置（含后处理链）。</param>
    /// <returns>同一构建器，便于链式调用。</returns>
    /// <remarks>
    /// 不调用本方法则视频后处理与重置钩子为 null → V1 兼容。
    /// 如需自行组合后处理链与重置钩子，请改用 <see cref="WithVideoTransforms"/>。
    /// </remarks>
    public MediaBuilder WithVideoPipeline(VideoPipelineConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        VideoTransforms = config.ToTransforms();
        VideoTransformsReset = config.Processors.Count > 0 ? new Action(config.ResetProcessors) : null;
        return this;
    }

    /// <summary>
    /// 直接注入已组合好的视频后处理变换链与重置钩子（V2-06 C5 / V2-07）。
    /// </summary>
    /// <param name="transforms">组合后的视频变换链（非 null）。</param>
    /// <param name="reset">组合后的状态重置钩子（可为 null）。</param>
    /// <returns>同一构建器，便于链式调用。</returns>
    public MediaBuilder WithVideoTransforms(
        IReadOnlyList<Func<VideoFrame, VideoFrame?>> transforms,
        Action? reset = null)
    {
        VideoTransforms = transforms ?? throw new ArgumentNullException(nameof(transforms));
        VideoTransformsReset = reset;
        return this;
    }
}

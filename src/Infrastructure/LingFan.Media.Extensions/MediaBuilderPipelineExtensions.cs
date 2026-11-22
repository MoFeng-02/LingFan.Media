using LingFan.Media.Audio;
using LingFan.Media.Video;

namespace LingFan.Media.Extensions;

/// <summary>
/// <see cref="MediaBuilder"/> 的管线后处理配置扩展（组合根侧）。
/// </summary>
/// <remarks>
/// <para>这些方法依赖 Audio/Video 模块的 <see cref="AudioPipelineConfig"/> / <see cref="VideoPipelineConfig"/>，
/// 故留在组合根 Extensions（唯一全知位置），不下沉到 Composition 叶子——
/// 插件（后端/渲染器/输出）只需 MediaBuilder 的 Services/Options 注册面，不感知管线配置类型。</para>
/// <para>调用语法与实例方法一致：<c>builder.WithAudioPipeline(config)</c>；各方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class MediaBuilderPipelineExtensions
{
    /// <summary>
    /// 注入音频管线配置，将效果链与重置钩子透传至 Core 音频管线。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="config">音频管线配置（含效果链及混音设置）。</param>
    /// <returns>同一构建器，便于链式调用。</returns>
    /// <remarks>
    /// 宿主可保留 <paramref name="config"/> 引用，在运行时通过 <see cref="IAudioEffect.IsEnabled"/> 调整单个效果启停；
    /// <c>ToTransforms()</c> / <see cref="AudioPipelineConfig.ResetEffects"/> 闭包捕获的效果实例与此引用相同，运行时切换即时生效。
    /// 不调用本方法则音频后处理与重置钩子为 null。
    /// 如需同时接入音量控制 / 混音，请改用 <see cref="WithAudioTransforms"/> 自行组合（或在本方法之后另行调用以覆盖）。
    /// </remarks>
    public static MediaBuilder WithAudioPipeline(this MediaBuilder builder, AudioPipelineConfig config)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(config);
        builder.AudioTransforms = config.ToTransforms();
        builder.AudioTransformsReset = config.ResetEffects();
        return builder;
    }

    /// <summary>
    /// 直接注入已组合好的音频后处理变换链与重置钩子。
    /// 适用于需将音量控制（<see cref="AudioPipelineTransforms.FromVolume"/>）、混音（<see cref="AudioPipelineTransforms.FromMixer"/>）
    /// 与效果链（<see cref="AudioPipelineConfig.ToTransforms"/>）组合后再注入的场景。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="transforms">组合后的音频变换链（非 null）。</param>
    /// <param name="reset">组合后的状态重置钩子（可为 null）。</param>
    /// <returns>同一构建器，便于链式调用。</returns>
    public static MediaBuilder WithAudioTransforms(
        this MediaBuilder builder,
        IReadOnlyList<Func<AudioFrame, AudioFrame>> transforms,
        Action? reset = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AudioTransforms = transforms ?? throw new ArgumentNullException(nameof(transforms));
        builder.AudioTransformsReset = reset;
        return builder;
    }

    /// <summary>
    /// 注入视频管线配置，将后处理链与重置钩子透传至 Core 视频管线。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="config">视频管线配置（含后处理链）。</param>
    /// <returns>同一构建器，便于链式调用。</returns>
    /// <remarks>
    /// 不调用本方法则视频后处理与重置钩子为 null。
    /// 如需自行组合后处理链与重置钩子，请改用 <see cref="WithVideoTransforms"/>。
    /// </remarks>
    public static MediaBuilder WithVideoPipeline(this MediaBuilder builder, VideoPipelineConfig config)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(config);
        builder.VideoTransforms = config.ToTransforms();
        builder.VideoTransformsReset = config.Processors.Count > 0 ? new Action(config.ResetProcessors) : null;
        return builder;
    }

    /// <summary>
    /// 直接注入已组合好的视频后处理变换链与重置钩子。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="transforms">组合后的视频变换链（非 null）。</param>
    /// <param name="reset">组合后的状态重置钩子（可为 null）。</param>
    /// <returns>同一构建器，便于链式调用。</returns>
    public static MediaBuilder WithVideoTransforms(
        this MediaBuilder builder,
        IReadOnlyList<Func<VideoFrame, VideoFrame?>> transforms,
        Action? reset = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.VideoTransforms = transforms ?? throw new ArgumentNullException(nameof(transforms));
        builder.VideoTransformsReset = reset;
        return builder;
    }
}
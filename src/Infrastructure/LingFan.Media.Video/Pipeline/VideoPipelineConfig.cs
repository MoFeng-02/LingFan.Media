namespace LingFan.Media.Video;

/// <summary>
/// 视频管线配置快照。由 <see cref="VideoPipeline.BuildConfig"/> 生成，
/// 供 MediaPlayer（Task-02-09）在创建 Core <c>VideoPipeline</c> 执行器时消费。
/// </summary>
/// <remarks>
/// <para>不可变快照，创建后不可修改。包含执行相关配置项：</para>
/// <list type="bullet">
/// <item>目标分辨率（<see cref="TargetWidth"/> / <see cref="TargetHeight"/>）</item>
/// <item>输出像素格式（<see cref="OutputPixelFormat"/>）</item>
/// <item>宽高比模式（<see cref="AspectRatio"/>）</item>
/// <item>后处理链（<see cref="Processors"/>）</item>
/// </list>
/// <para>
/// MediaPlayer 将配置中的执行相关项映射为 <c>Core.VideoPipeline</c> 的运行时参数。
/// Core.VideoPipeline 不直接依赖 <c>LingFan.Media.Video</c>，避免分层倒置。
/// </para>
/// </remarks>
public sealed class VideoPipelineConfig
{
    /// <summary>目标输出宽度（null 表示使用源宽度）。</summary>
    public int? TargetWidth { get; init; }

    /// <summary>目标输出高度（null 表示使用源高度）。</summary>
    public int? TargetHeight { get; init; }

    /// <summary>输出像素格式（null 表示使用源格式）。</summary>
    public PixelFormat? OutputPixelFormat { get; init; }

    /// <summary>宽高比缩放模式。默认 <see cref="AspectRatioMode.Uniform"/>（保持宽高比，留黑边）。</summary>
    public AspectRatioMode AspectRatio { get; init; } = AspectRatioMode.Uniform;

    /// <summary>后处理链（可能为空列表，不会为 null）。</summary>
    public IReadOnlyList<IVideoProcessor> Processors { get; init; } = Array.Empty<IVideoProcessor>();
}

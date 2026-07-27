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

    /// <summary>
    /// 将后处理链转换为 Core 管线可用的中立委托序列（V2-06 C5）。
    /// </summary>
    /// <remarks>
    /// <para>每个 <see cref="IVideoProcessor"/> 包装为一个 <c>Func&lt;VideoFrame, VideoFrame?&gt;</c> 闭包（null = 丢弃帧）。</para>
    /// <para>Core.VideoPipeline 仅消费中立 BCL 委托，不直接依赖 <c>LingFan.Media.Video</c>，保持分层倒置避免。</para>
    /// <para>所有权转移：<see cref="IVideoProcessor.Process"/> 内部 Dispose 输入帧并返回新帧；
    /// 禁用的处理器透传（闭包直接返回输入帧）。</para>
    /// </remarks>
    public IReadOnlyList<Func<VideoFrame, VideoFrame?>> ToTransforms()
    {
        if (Processors.Count == 0)
            return Array.Empty<Func<VideoFrame, VideoFrame?>>();

        var list = new List<Func<VideoFrame, VideoFrame?>>(Processors.Count);
        foreach (var processor in Processors)
        {
            if (processor is null)
                continue;
            // 闭包捕获具体处理器，Core 看不到 IVideoProcessor 类型
            list.Add(frame => processor.Process(frame));
        }
        return list;
    }

    /// <summary>
    /// 重置后处理链（Seek/Flush 后调用，V2-06 二次审计修复）。
    /// </summary>
    /// <remarks>
    /// <para>依次调用每个 <see cref="IVideoProcessor.Reset"/>，释放有状态处理器
    /// （如 <see cref="FrameRateConverter"/> 的上一帧副本 _held），
    /// 避免 Seek 后返回陈旧帧或跨会话滞留。</para>
    /// <para>宿主/Extensions（V2-07/08 端到端接入）可将本方法作为
    /// <c>Action</c>（BCL 中立类型）注入 Core <c>VideoPipeline</c> 的重置钩子，
    /// 保持分层倒置（Core 不依赖 Video 模块）。</para>
    /// </remarks>
    public void ResetProcessors()
    {
        foreach (var processor in Processors)
            processor?.Reset();
    }
}

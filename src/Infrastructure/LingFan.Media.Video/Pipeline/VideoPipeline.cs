namespace LingFan.Media.Video;

/// <summary>
/// 视频管线**配置**。管理后处理链、视频设置和输出参数。
/// </summary>
/// <remarks>
/// <para>本类仅为<b>配置</b>，不持有解码/渲染执行逻辑；实际执行由
/// <c>Core/Playback/VideoPipeline.cs</c> 完成。</para>
/// <para>使用 <see cref="BuildConfig"/> 生成不可变配置快照
/// (<see cref="VideoPipelineConfig"/>)，供 MediaPlayer 创建 Core 执行器时消费。</para>
/// <para>非线程安全（配置在播放启动前设置，运行时不可修改）。</para>
/// </remarks>
public sealed class VideoPipeline
{
    private readonly List<IVideoProcessor> _processors = [];
    private VideoSettings _settings = new();

    /// <summary>后处理链（只读视图）。</summary>
    public IReadOnlyList<IVideoProcessor> Processors => _processors;

    /// <summary>视频解码与渲染设置。</summary>
    public VideoSettings Settings => _settings;

    /// <summary>目标输出宽度（null 表示使用源宽度）。</summary>
    public int? TargetWidth { get; set; }

    /// <summary>目标输出高度（null 表示使用源高度）。</summary>
    public int? TargetHeight { get; set; }

    /// <summary>宽高比缩放模式。默认 <see cref="AspectRatioMode.Uniform"/>。</summary>
    public AspectRatioMode AspectRatio { get; set; } = AspectRatioMode.Uniform;

    /// <summary>
    /// 添加后处理器。
    /// </summary>
    /// <param name="processor">视频处理器。</param>
    /// <exception cref="ArgumentNullException">processor 为 null。</exception>
    public void AddProcessor(IVideoProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _processors.Add(processor);
    }

    /// <summary>
    /// 移除后处理器。
    /// </summary>
    /// <param name="processor">要移除的处理器。</param>
    public void RemoveProcessor(IVideoProcessor processor)
    {
        _processors.Remove(processor);
    }

    /// <summary>
    /// 应用管线配置。
    /// </summary>
    /// <param name="settings">视频设置。</param>
    /// <exception cref="ArgumentNullException">settings 为 null。</exception>
    public void Configure(VideoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>
    /// 生成供 Core <c>VideoPipeline</c> 执行器使用的不可变配置快照。
    /// </summary>
    /// <returns>配置快照。</returns>
    public VideoPipelineConfig BuildConfig()
    {
        return new VideoPipelineConfig
        {
            TargetWidth = TargetWidth,
            TargetHeight = TargetHeight,
            OutputPixelFormat = _settings.OutputPixelFormat,
            AspectRatio = AspectRatio,
            Processors = _processors.AsReadOnly(),
        };
    }
}

namespace LingFan.Media.Video.Processors;

/// <summary>
/// 去隔行处理器。将隔行扫描帧（如 1080i）转换为逐行扫描帧（如 1080p）。
/// </summary>
/// <remarks>
/// <para>支持 <see cref="DeinterlaceMode"/> 三种模式：Bob（帧率翻倍）、Blend（混合两场）、Yadif（自适应）。</para>
/// <para>V1 简化实现：透传处理器（不做实际去隔行，直接返回输入帧）。</para>
/// <para>V2 路径：可对接 FFmpeg libavfilter yadif/delogo 滤镜或 GPU 去隔行 Shader。</para>
/// </remarks>
public sealed class Deinterlacer : IVideoProcessor
{
    /// <inheritdoc/>
    public string Name => "Deinterlacer";

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>去隔行模式。</summary>
    public DeinterlaceMode Mode { get; set; } = DeinterlaceMode.Blend;

    /// <summary>
    /// 初始化 <see cref="Deinterlacer"/> 的新实例。
    /// </summary>
    /// <param name="mode">去隔行模式（默认 Blend）。</param>
    public Deinterlacer(DeinterlaceMode mode = DeinterlaceMode.Blend)
    {
        Mode = mode;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// V1 透传：直接返回输入帧，不做实际去隔行。
    /// V2 将根据 <see cref="Mode"/> 实现对应算法，Dispose 输入帧并返回新帧。
    /// </remarks>
    public VideoFrame Process(VideoFrame frame)
    {
        if (!IsEnabled)
            return frame;

        // V1: 透传——不做实际去隔行，直接返回输入帧
        // V2: 根据 Mode 实现 Bob/Blend/Yadif 算法 → 创建新 VideoFrame → Dispose 输入 frame → 返回新帧
        return frame;
    }
}

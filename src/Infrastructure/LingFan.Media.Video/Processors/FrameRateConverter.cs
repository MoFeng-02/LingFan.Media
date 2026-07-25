namespace LingFan.Media.Video.Processors;

/// <summary>
/// 帧率转换处理器。将输入帧率转换到目标帧率。
/// </summary>
/// <remarks>
/// <para>常见场景：24fps → 60fps（插帧）、60fps → 30fps（丢帧）。</para>
/// <para>V1 简化实现：透传处理器（不做实际帧率转换，直接返回输入帧）。</para>
/// <para>V2 路径：可对接 FFmpeg minterpolate 滤镜或 GPU 运动补偿插帧。</para>
/// </remarks>
public sealed class FrameRateConverter : IVideoProcessor
{
    /// <inheritdoc/>
    public string Name => "FrameRateConverter";

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>目标帧率（FPS，如 60）。</summary>
    public float TargetFrameRate { get; set; } = 60f;

    /// <summary>
    /// 初始化 <see cref="FrameRateConverter"/> 的新实例。
    /// </summary>
    /// <param name="targetFrameRate">目标帧率（默认 60 FPS）。</param>
    public FrameRateConverter(float targetFrameRate = 60f)
    {
        TargetFrameRate = targetFrameRate;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// V1 透传：直接返回输入帧，不做实际帧率转换。
    /// V2 将实现插帧/丢帧逻辑。插帧时 Dispose 输入帧并返回新帧；
    /// 丢帧时 Dispose 输入帧并返回一个标记帧（或重新设计接口支持 VideoFrame? 返回值）。
    /// </remarks>
    public VideoFrame Process(VideoFrame frame)
    {
        if (!IsEnabled)
            return frame;

        // V1: 透传——不做实际帧率转换，直接返回输入帧
        // V2: 实现插帧/丢帧逻辑 → 创建新 VideoFrame → Dispose 输入 frame → 返回新帧
        return frame;
    }
}

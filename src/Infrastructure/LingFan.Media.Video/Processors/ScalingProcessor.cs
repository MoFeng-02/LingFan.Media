namespace LingFan.Media.Video.Processors;

/// <summary>
/// 缩放处理器。将输入帧缩放到目标分辨率。
/// </summary>
/// <remarks>
/// <para>V1 简化实现：透传处理器（不做实际缩放，直接返回输入帧）。</para>
/// <para>V2 路径：可对接 FFmpeg libswscale 或 GPU 缩放（D3D11 Shader / Vulkan blit）。</para>
/// <para><b>所有权转移</b>：<see cref="IsEnabled"/> 为 true 且执行实际处理时，
/// 输入帧被 Dispose，返回新帧。V1 透传模式下不 Dispose、不创建新帧。</para>
/// </remarks>
public sealed class ScalingProcessor : IVideoProcessor
{
    /// <inheritdoc/>
    public string Name => "ScalingProcessor";

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>目标宽度（像素）。</summary>
    public int TargetWidth { get; set; }

    /// <summary>目标高度（像素）。</summary>
    public int TargetHeight { get; set; }

    /// <summary>
    /// 初始化 <see cref="ScalingProcessor"/> 的新实例。
    /// </summary>
    /// <param name="targetWidth">目标宽度。</param>
    /// <param name="targetHeight">目标高度。</param>
    public ScalingProcessor(int targetWidth = 0, int targetHeight = 0)
    {
        TargetWidth = targetWidth;
        TargetHeight = targetHeight;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// V1 透传：直接返回输入帧，不做实际缩放。
    /// V2 将实现双线性/Bicubic 缩放算法，Dispose 输入帧并返回新帧。
    /// </remarks>
    public VideoFrame Process(VideoFrame frame)
    {
        if (!IsEnabled)
            return frame;

        // V1: 透传——不做实际缩放，直接返回输入帧
        // V2: 实现缩放逻辑 → 创建新 VideoFrame → Dispose 输入 frame → 返回新帧
        return frame;
    }
}

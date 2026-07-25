namespace LingFan.Media.Video.Processors;

/// <summary>
/// 色彩空间转换处理器。将输入帧的像素格式转换到目标格式。
/// </summary>
/// <remarks>
/// <para>常见场景：YUV420P → BGRA32（供 Skia 渲染）、NV12 → RGBA32（供 GPU 纹理上传）。</para>
/// <para>V1 简化实现：透传处理器（不做实际转换，直接返回输入帧）。</para>
/// <para>V2 路径：可对接 FFmpeg libswscale 或 GPU 色彩空间转换 Shader。</para>
/// </remarks>
public sealed class ColorSpaceConverter : IVideoProcessor
{
    /// <inheritdoc/>
    public string Name => "ColorSpaceConverter";

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>目标像素格式。</summary>
    public PixelFormat TargetFormat { get; set; } = PixelFormat.BGRA32;

    /// <summary>
    /// 初始化 <see cref="ColorSpaceConverter"/> 的新实例。
    /// </summary>
    /// <param name="targetFormat">目标像素格式（默认 BGRA32）。</param>
    public ColorSpaceConverter(PixelFormat targetFormat = PixelFormat.BGRA32)
    {
        TargetFormat = targetFormat;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// V1 透传：直接返回输入帧，不做实际转换。
    /// V2 将实现色彩空间转换算法，Dispose 输入帧并返回新帧。
    /// </remarks>
    public VideoFrame Process(VideoFrame frame)
    {
        if (!IsEnabled)
            return frame;

        // V1: 透传——不做实际转换，直接返回输入帧
        // V2: 实现色彩空间转换 → 创建新 VideoFrame → Dispose 输入 frame → 返回新帧
        return frame;
    }
}

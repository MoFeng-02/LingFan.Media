namespace LingFan.Media.Video.Processors;

/// <summary>
/// 色彩空间转换处理器。将输入帧的像素格式转换到目标格式。
/// </summary>
/// <remarks>
/// <para>V2 实现：仅处理打包格式间的 R/B 通道交换（BGRA32 ↔ RGBA32）。
/// YUV/平面格式与 RGB24 的复杂转换留给 FFmpeg libswscale 或 GPU Shader 路径；不支持时透传。</para>
/// <para>仅处理打包软件帧；其余格式透传。同步热路径。</para>
/// </remarks>
public sealed class ColorSpaceConverter : IVideoProcessor
{
    /// <inheritdoc/>
    public string Name => "ColorSpaceConverter";

    /// <inheritdoc/>
    public void Reset() { } // 无状态

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>目标像素格式（默认 BGRA32）。</summary>
    public PixelFormat TargetFormat { get; set; } = PixelFormat.BGRA32;

    /// <summary>初始化 <see cref="ColorSpaceConverter"/> 的新实例。</summary>
    public ColorSpaceConverter(PixelFormat targetFormat = PixelFormat.BGRA32)
    {
        TargetFormat = targetFormat;
    }

    /// <inheritdoc/>
    public VideoFrame? Process(VideoFrame frame)
    {
        if (!IsEnabled)
            return frame;
        if (!FrameUtil.TryGetPackedSoftware(frame, out var src, out var bpp))
            return frame; // 非打包软件帧，透传
        if (src.Format == TargetFormat)
            return frame; // 已是目标格式
        if (!IsSupportedSwap(src.Format, TargetFormat))
            return frame; // 平面/YUV 等复杂转换 → 留给 FFmpeg/GPU，透传

        int w = src.Width, h = src.Height;
        int srcStride = src.Stride > 0 ? src.Stride : w * bpp;
        var srcSpan = src.Data.Span;
        var dstRes = new SoftwareFrameResource(w, h, TargetFormat, w * h * bpp);
        var dstSpan = dstRes.Data.Span;
        int dstStride = w * bpp;

        for (int y = 0; y < h; y++)
        {
            var sRow = srcSpan.Slice(y * srcStride, w * bpp);
            var dRow = dstSpan.Slice(y * dstStride, w * bpp);
            for (int x = 0; x < w; x++)
            {
                int si = x * bpp;
                int di = x * bpp;
                // 交换 R 与 B 通道（索引 0 与 2），其余通道原样
                dRow[di] = sRow[si + 2];
                dRow[di + 1] = sRow[si + 1];
                dRow[di + 2] = sRow[si];
                if (bpp == 4)
                    dRow[di + 3] = sRow[si + 3];
            }
        }

        var result = new VideoFrame(w, h, TargetFormat, dstRes, frame.Timestamp, frame.Duration, frame.KeyFrame);
        frame.Dispose(); // 所有权转移
        return result;
    }

    private static bool IsSupportedSwap(PixelFormat src, PixelFormat dst) => (src, dst) switch
    {
        (PixelFormat.BGRA32, PixelFormat.RGBA32) => true,
        (PixelFormat.RGBA32, PixelFormat.BGRA32) => true,
        _ => false,
    };
}

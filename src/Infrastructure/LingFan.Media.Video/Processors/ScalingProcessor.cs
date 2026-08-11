namespace LingFan.Media.Video.Processors;

/// <summary>
/// 缩放处理器。将输入帧缩放到目标分辨率。
/// </summary>
/// <remarks>
/// <para>仅处理打包（packed）CPU 帧（BGRA32/RGBA32/RGB24），
/// 平面/YUV 与 GPU 资源直接透传（不 Dispose，不创建新帧）。</para>
/// <para><b>所有权转移</b>：执行实际缩放时输入帧被 Dispose，返回新帧。
/// 透传路径不 Dispose、不创建新帧。</para>
/// <para>同步热路径，无 async。</para>
/// </remarks>
public sealed class ScalingProcessor : IVideoProcessor
{
    /// <inheritdoc/>
    public string Name => "ScalingProcessor";

    /// <inheritdoc/>
    public void Reset() { } // 无状态（每帧新建 + Dispose 输入）

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>目标宽度（像素，0 表示未配置 → 透传）。</summary>
    public int TargetWidth { get; set; }

    /// <summary>目标高度（像素，0 表示未配置 → 透传）。</summary>
    public int TargetHeight { get; set; }

    /// <summary>缩放算法（默认双线性，验收要求）。</summary>
    public ScalingAlgorithm Algorithm { get; set; } = ScalingAlgorithm.Bilinear;

    /// <summary>初始化 <see cref="ScalingProcessor"/> 的新实例。</summary>
    public ScalingProcessor(int targetWidth = 0, int targetHeight = 0)
    {
        TargetWidth = targetWidth;
        TargetHeight = targetHeight;
    }

    /// <inheritdoc/>
    public VideoFrame? Process(VideoFrame frame)
    {
        if (!IsEnabled)
            return frame;
        if (TargetWidth <= 0 || TargetHeight <= 0)
            return frame; // 未配置目标尺寸
        if (TargetWidth == frame.Width && TargetHeight == frame.Height)
            return frame; // 尺寸已匹配
        if (!FrameUtil.TryGetPackedSoftware(frame, out var src, out var bpp))
            return frame; // 非打包软件帧（GPU/平面），透传

        int sw = src.Width, sh = src.Height;
        int dw = TargetWidth, dh = TargetHeight;
        int srcStride = src.Stride > 0 ? src.Stride : sw * bpp;
        var dstRes = new SoftwareFrameResource(dw, dh, src.Format, dw * dh * bpp);
        var srcSpan = src.Data.Span;
        var dstSpan = dstRes.Data.Span;
        int dstStride = dw * bpp;

        if (Algorithm == ScalingAlgorithm.Nearest)
            NearestScale(srcSpan, srcStride, sw, sh, bpp, dstSpan, dstStride, dw, dh);
        else
            // Bilinear 为默认；Bicubic 当前复用双线性（后续增强）
            BilinearScale(srcSpan, srcStride, sw, sh, bpp, dstSpan, dstStride, dw, dh);

        var result = new VideoFrame(dw, dh, src.Format, dstRes, frame.Timestamp, frame.Duration, frame.KeyFrame);
        frame.Dispose(); // 所有权转移
        return result;
    }

    private static void BilinearScale(ReadOnlySpan<byte> src, int srcStride, int sw, int sh, int bpp,
        Span<byte> dst, int dstStride, int dw, int dh)
    {
        for (int dy = 0; dy < dh; dy++)
        {
            float sy = ((dy + 0.5f) * sh / dh) - 0.5f;
            if (sy < 0f) sy = 0f;
            int y0 = (int)sy;
            if (y0 > sh - 1) y0 = sh - 1;
            int y1 = y0 + 1 < sh ? y0 + 1 : y0;
            float fy = sy - y0;
            var dstRow = dst.Slice(dy * dstStride, dstStride);
            for (int dx = 0; dx < dw; dx++)
            {
                float sx = ((dx + 0.5f) * sw / dw) - 0.5f;
                if (sx < 0f) sx = 0f;
                int x0 = (int)sx;
                if (x0 > sw - 1) x0 = sw - 1;
                int x1 = x0 + 1 < sw ? x0 + 1 : x0;
                float fx = sx - x0;
                for (int c = 0; c < bpp; c++)
                {
                    int s00 = src[y0 * srcStride + x0 * bpp + c];
                    int s01 = src[y0 * srcStride + x1 * bpp + c];
                    int s10 = src[y1 * srcStride + x0 * bpp + c];
                    int s11 = src[y1 * srcStride + x1 * bpp + c];
                    int top = (int)(s00 * (1f - fx) + s01 * fx);
                    int bot = (int)(s10 * (1f - fx) + s11 * fx);
                    int val = (int)(top * (1f - fy) + bot * fy);
                    if (val < 0) val = 0;
                    else if (val > 255) val = 255;
                    dstRow[dx * bpp + c] = (byte)val;
                }
            }
        }
    }

    private static void NearestScale(ReadOnlySpan<byte> src, int srcStride, int sw, int sh, int bpp,
        Span<byte> dst, int dstStride, int dw, int dh)
    {
        for (int dy = 0; dy < dh; dy++)
        {
            int sy = (dy * sh) / dh;
            var dstRow = dst.Slice(dy * dstStride, dstStride);
            for (int dx = 0; dx < dw; dx++)
            {
                int sx = (dx * sw) / dw;
                var srcPix = src.Slice(sy * srcStride + sx * bpp, bpp);
                for (int c = 0; c < bpp; c++)
                    dstRow[dx * bpp + c] = srcPix[c];
            }
        }
    }
}

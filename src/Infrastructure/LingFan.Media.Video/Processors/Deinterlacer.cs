namespace LingFan.Media.Video.Processors;

/// <summary>
/// 去隔行处理器。将隔行扫描帧（如 1080i）转换为逐行扫描帧（如 1080p）。
/// </summary>
/// <remarks>
/// <para>支持 <see cref="DeinterlaceMode"/>：Bob（场复制）、Blend（场平均）。
/// Yadif 为可选增强，当前复用 Blend 以保证画质稳定。</para>
/// <para>仅处理打包软件帧；其余格式透传。同步热路径。</para>
/// </remarks>
public sealed class Deinterlacer : IVideoProcessor
{
    /// <inheritdoc/>
    public string Name => "Deinterlacer";

    /// <inheritdoc/>
    public void Reset() { } // 无状态

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>去隔行模式（默认 Blend）。</summary>
    public DeinterlaceMode Mode { get; set; } = DeinterlaceMode.Blend;

    /// <summary>初始化 <see cref="Deinterlacer"/> 的新实例。</summary>
    public Deinterlacer(DeinterlaceMode mode = DeinterlaceMode.Blend)
    {
        Mode = mode;
    }

    /// <inheritdoc/>
    public VideoFrame? Process(VideoFrame frame)
    {
        if (!IsEnabled)
            return frame;
        if (!FrameUtil.TryGetPackedSoftware(frame, out var src, out var bpp))
            return frame; // 非打包软件帧，透传

        int w = src.Width, h = src.Height;
        int srcStride = src.Stride > 0 ? src.Stride : w * bpp;
        var srcSpan = src.Data.Span;
        var dstRes = new SoftwareFrameResource(w, h, src.Format, w * h * bpp);
        var dstSpan = dstRes.Data.Span;
        int dstStride = w * bpp;

        if (Mode == DeinterlaceMode.Bob)
            BobDeinterlace(srcSpan, srcStride, w, h, bpp, dstSpan, dstStride);
        else
            // Blend 为默认；Yadif 复用 Blend（可选增强）
            BlendDeinterlace(srcSpan, srcStride, w, h, bpp, dstSpan, dstStride);

        var result = new VideoFrame(w, h, src.Format, dstRes, frame.Timestamp, frame.Duration, frame.KeyFrame);
        frame.Dispose(); // 所有权转移
        return result;
    }

    private static void BobDeinterlace(ReadOnlySpan<byte> src, int srcStride, int w, int h, int bpp,
        Span<byte> dst, int dstStride)
    {
        // 取顶场（偶数行）逐行复制到完整帧（垂直分辨率减半但无梳状伪影）
        for (int y = 0; y < h; y++)
        {
            int srcY = (y / 2) * 2;
            if (srcY > h - 1) srcY = h - 1;
            var srcRow = src.Slice(srcY * srcStride, w * bpp);
            srcRow.CopyTo(dst.Slice(y * dstStride, w * bpp));
        }
    }

    private static void BlendDeinterlace(ReadOnlySpan<byte> src, int srcStride, int w, int h, int bpp,
        Span<byte> dst, int dstStride)
    {
        for (int y = 0; y < h; y++)
        {
            int topY = (y / 2) * 2;
            int botY = topY + 1;
            if (topY > h - 1) topY = h - 1;
            if (botY > h - 1) botY = h - 1;
            var top = src.Slice(topY * srcStride, w * bpp);
            var bot = src.Slice(botY * srcStride, w * bpp);
            var row = dst.Slice(y * dstStride, w * bpp);
            for (int i = 0; i < w * bpp; i++)
            {
                int v = (top[i] + bot[i]) >> 1;
                row[i] = (byte)v;
            }
        }
    }
}

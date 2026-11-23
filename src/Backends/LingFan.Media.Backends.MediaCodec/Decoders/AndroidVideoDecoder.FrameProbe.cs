namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// 坏帧探针分部：帧间亮度采样 MAD 差分（识别参考帧损坏）与平面行拷贝/统计辅助。
/// </summary>
internal sealed partial class AndroidVideoDecoder
{
    // 帧间差分探测器（坏帧探针）
    // 正常相邻帧的亮度采样平均绝对差（MAD）平稳；参考帧损坏产生的宏块 garbage 会让 MAD 出现尖峰。
    // 用途：区分「帧丢了」（MAD 平稳，只是 pts 跳变）与「解码器输出了坏帧」（MAD 尖峰且持续若干帧）。
    // 分块覆盖全画面：12 行 × 8 段 × 8 字节 = 768 字节，共 96 个块位。
    // 早期版本只采 8 行 × 64 字节（占 Y 平面 0.025%），宏块 garbage 绝大多数落在采样点之外，
    // 产生「假阴性」（画面肉眼花屏但探针报告零突变）。必须按块覆盖整幅画面。
    private const int DeltaRows = 12;
    private const int DeltaCols = 8;
    private const int DeltaBytesPerBlock = 8;
    private const int DeltaSampleLen = DeltaRows * DeltaCols * DeltaBytesPerBlock;
    /// <summary>单块平均绝对差超过该值即计入「坏块」。</summary>
    private const double DeltaBlockBadThreshold = 25.0;
    private readonly byte[] _prevYSample = new byte[DeltaSampleLen];
    private bool _hasPrevSample;
    private double _madBase;
    private int _spikeFrames;

    /// <summary>计算本帧与上一帧的亮度采样平均绝对差（MAD），识别参考帧损坏导致的坏帧。</summary>
    private void UpdateFrameDelta(ReadOnlySpan<byte> data, int w, int h, int seq, TimeSpan pts)
    {
        Span<byte> cur = stackalloc byte[DeltaSampleLen];
        for (int r = 0; r < DeltaRows; r++)
        {
            int row = (h * (r * 2 + 1)) / (DeltaRows * 2);   // 纵向均匀
            for (int c = 0; c < DeltaCols; c++)
            {
                int src = row * w + (w * c) / DeltaCols;      // 横向均匀
                int dst = (r * DeltaCols + c) * DeltaBytesPerBlock;
                for (int i = 0; i < DeltaBytesPerBlock; i++)
                {
                    int pos = src + i;
                    cur[dst + i] = (pos >= 0 && pos < data.Length) ? data[pos] : (byte)0;
                }
            }
        }

        if (_hasPrevSample)
        {
            long sum = 0;
            int badBlocks = 0;
            for (int b = 0; b < DeltaRows * DeltaCols; b++)
            {
                int off = b * DeltaBytesPerBlock;
                int diff = 0;
                for (int i = 0; i < DeltaBytesPerBlock; i++)
                    diff += Math.Abs(cur[off + i] - _prevYSample[off + i]);
                sum += diff;
                if (diff / (double)DeltaBytesPerBlock >= DeltaBlockBadThreshold)
                    badBlocks++;
            }
            double mad = sum / (double)DeltaSampleLen;

            // 判据：整体突变 OR 局部坏块占比 > 5% —— 后者专抓「只有部分宏块损坏」的坏帧。
            bool spike = (_madBase > 1.0 && mad > _madBase * 3.0)
                         || badBlocks * 20 >= DeltaRows * DeltaCols;
            if (spike)
                _spikeFrames++;
            _madBase = _madBase > 0 ? (_madBase * 0.9 + mad * 0.1) : mad;

            if (spike || seq < 240 || (seq % 60) == 0)
                _logger.LogInformation(
                    "[FRAME-DELTA] seq={Seq} pts={Pts} mad={Mad:F1} 基线={Base:F1} 坏块={Bad}/{Total}{Spike} 累计突变={Spikes}",
                    seq, pts, mad, _madBase, badBlocks, DeltaRows * DeltaCols,
                    spike ? " ← 突变（疑似坏帧）" : string.Empty, _spikeFrames);
        }

        cur.CopyTo(_prevYSample);
        _hasPrevSample = true;
    }

    /// <summary>按源有效长度拷贝一行（越界时只拷可用部分，不整行丢弃，避免对齐填充误差导致整行变 0）。</summary>
    /// <param name="src">源缓冲（被 Y/U/V 三个平面轮流复用，故 <c>src.Length</c> 不等于本平面有效长度）。</param>
    /// <param name="srcOff">本行在源缓冲内的字节偏移。</param>
    /// <param name="count">期望拷贝字节数。</param>
    /// <param name="dst">目标紧凑平面。</param>
    /// <param name="dstOff">目标偏移。</param>
    /// <param name="srcValid">本平面实际装载的有效字节数（= plane.Buffer.Capacity()）。
    /// <b>必须</b>用它而非 <c>src.Length</c> 做上界：源缓冲为 Y 平面分配时远大于 UV 容量，
    /// 若按 <c>src.Length</c> 计算可用量，UV 行会越过有效数据边界读到残留的 Y 像素，
    /// 导致画面下半色度错乱（大面积块状破碎、色块错位、拖影）。</param>
    private static void CopyRow(byte[] src, int srcOff, int count, Span<byte> dst, int dstOff, int srcValid)
    {
        if (srcOff < 0 || count <= 0) return;
        int limit = Math.Min(src.Length, srcValid);
        int n = Math.Min(count, limit - srcOff);
        if (n <= 0) return;
        new ReadOnlySpan<byte>(src, srcOff, n).CopyTo(dst.Slice(dstOff, n));
    }

    /// <summary>累计一行的平面统计（均值/范围/非零占比），供首帧诊断使用。</summary>
    private static void AccumPlaneStats(Span<byte> data, int off, int count,
        ref long sum, ref byte min, ref byte max, ref int nonZero)
    {
        for (int i = 0; i < count; i++)
        {
            byte b = data[off + i];
            sum += b; if (b < min) min = b; if (b > max) max = b; if (b != 0) nonZero++;
        }
    }
}
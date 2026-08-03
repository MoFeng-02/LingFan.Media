using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Backends.MediaFoundation.Decoders;

/// <summary>
/// 诊断工具：把 MFT 输出的<b>裁剪之前</b>的完整 coded NV12 帧落盘，并做「边缘列/行填充检测」。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：显示孔径裁剪（coded 1920x1088 → display 1906x1080）一旦起点猜错，
/// 画面会整体平移、边缘吃进编码填充区，而这种错误对
/// 「行间错位检测（SKEW）」和「方向性频域检测（FREQ）」<b>都是不可见的</b>——
/// 前者只看相邻行相对位移（整体平移不改变），后者被 7/1906 = 0.37% 的占比平均抹平。
/// 唯一的破法是看裁剪之前的原图，让真实内容边界自己显形。</para>
///
/// <para><b>边缘填充检测原理</b>（眼睛无关）：视频编码器把画面补齐到宏块整数倍时，
/// 填充区一律由<b>最后一个有效像素沿边缘复制</b>而来，因此填充列之间的差异恒等于 0。
/// 于是逐列计算「与右邻列的平均绝对差」，从边缘向内扫描，
/// 差值持续为 0 的那一段就是填充区，第一个非 0 列即真实内容边界。
/// 该边界与 <c>MFVideoArea.OffsetX</c> 必须一致，否则裁剪起点有误。</para>
///
/// <para>环境变量门控，默认全关、零开销：
/// <list type="bullet">
/// <item><c>LINGFAN_MF_DUMP_CODED=1</c> —— 启用（落盘 + 边缘检测）</item>
/// <item><c>LINGFAN_MF_DUMP_CODED_DIR</c> —— 输出目录，默认 <c>%CD%/TestInfo/Diagnostics/coded</c></item>
/// </list></para>
/// </remarks>
internal static class MFCodedFrameDump
{
    /// <summary>从边缘向内最多扫描多少列/行寻找内容边界（编码填充不会超过一个宏块 16px，取 48 留足余量）。</summary>
    private const int ScanDepth = 48;

    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("LINGFAN_MF_DUMP_CODED") is "1" or "true" or "TRUE";

    private static readonly string OutDir =
        Environment.GetEnvironmentVariable("LINGFAN_MF_DUMP_CODED_DIR")
        ?? Path.Combine(Environment.CurrentDirectory, "TestInfo", "Diagnostics", "coded");

    private static bool _done;

    /// <summary>
    /// 落盘并分析一帧 coded NV12（仅首帧，之后自动短路）。
    /// </summary>
    /// <param name="src">MFT 输出的完整 NV12 源缓冲（长度应为 codedW*codedH*3/2）。</param>
    /// <param name="codedW">编码宽（NV12 平面 stride）。</param>
    /// <param name="codedH">编码高。</param>
    /// <param name="displayW">显示孔径宽。</param>
    /// <param name="displayH">显示孔径高。</param>
    /// <param name="offX">当前采用的孔径 X 偏移（待验证值）。</param>
    /// <param name="offY">当前采用的孔径 Y 偏移（待验证值）。</param>
    /// <param name="logger">日志。</param>
    public static void TryDump(ReadOnlySpan<byte> src, int codedW, int codedH,
        int displayW, int displayH, int offX, int offY, ILogger logger)
    {
        if (!Enabled || _done) return;
        if (codedW <= 0 || codedH <= 0) return;
        long need = (long)codedW * codedH * 3 / 2;
        if (src.Length < need) return;
        _done = true;

        try
        {
            Directory.CreateDirectory(OutDir);

            // ── 1. 落盘：coded 全帧（彩色 + 纯 Y 灰度），均为 1:1 不缩放 ──────────────
            string colorPath = Path.Combine(OutDir, $"coded_{codedW}x{codedH}_color.bmp");
            string yPath = Path.Combine(OutDir, $"coded_{codedW}x{codedH}_yplane.bmp");
            WriteNv12AsBmp(src, codedW, codedH, colorPath, grayscaleOnly: false);
            WriteNv12AsBmp(src, codedW, codedH, yPath, grayscaleOnly: true);

            // ── 2. 边缘填充检测（眼睛无关）───────────────────────────────────────
            var y = src[..(codedW * codedH)];
            int leftEdge = FindLeftContentEdge(y, codedW, codedH);
            int rightPad = FindRightPadWidth(y, codedW, codedH);
            int topEdge = FindTopContentEdge(y, codedW, codedH);
            int botPad = FindBottomPadHeight(y, codedW, codedH);

            int contentW = codedW - rightPad - leftEdge;
            int contentH = codedH - botPad - topEdge;

            // ── 3. 判定：实测内容边界 vs 当前采用的 offset ────────────────────────
            string verdictX = leftEdge == offX
                ? $"✓ 一致（起裁列 {offX} 正确）"
                : $"★不一致：实测内容自第 {leftEdge} 列起，当前从第 {offX} 列起裁 ⇒ 画面平移 {offX - leftEdge} 列★";
            string verdictY = topEdge == offY
                ? $"✓ 一致（起裁行 {offY} 正确）"
                : $"★不一致：实测内容自第 {topEdge} 行起，当前从第 {offY} 行起裁 ⇒ 画面平移 {offY - topEdge} 行★";
            string verdictSize = contentW == displayW && contentH == displayH
                ? $"✓ 实测内容尺寸 {contentW}x{contentH} == 显示孔径 {displayW}x{displayH}"
                : $"★实测内容尺寸 {contentW}x{contentH} ≠ 显示孔径 {displayW}x{displayH}★";

            logger.LogInformation(
                "[CODED-DUMP] {CW}x{CH} 已落盘 | 左填充={L} 右填充={R} 上填充={T} 下填充={B} | 实测内容={CoW}x{CoH}\n" +
                "             X: {VX}\n" +
                "             Y: {VY}\n" +
                "             尺寸: {VS}\n" +
                "             彩色: {P1}\n" +
                "             Y平面: {P2}",
                codedW, codedH, leftEdge, rightPad, topEdge, botPad, contentW, contentH,
                verdictX, verdictY, verdictSize, colorPath, yPath);

            // ── 4. 边缘列差值明细（供人工复核，防止"填充恰好差值为0"的误判）────────
            logger.LogInformation("[CODED-EDGE] {Detail}", BuildEdgeDetail(y, codedW, codedH));
        }
        catch (Exception ex)
        {
            logger.LogWarning("[CODED-DUMP] 落盘失败: {Msg}", ex.Message);
        }
    }

    // ────────────────────── 边缘填充检测 ──────────────────────

    /// <summary>列 x 与列 x+1 的平均绝对差（对全部行采样）。</summary>
    private static double ColumnDiff(ReadOnlySpan<byte> y, int w, int h, int x)
    {
        if (x < 0 || x + 1 >= w) return double.NaN;
        long sum = 0;
        for (int row = 0; row < h; row++)
        {
            int b = row * w + x;
            sum += Math.Abs(y[b] - y[b + 1]);
        }
        return (double)sum / h;
    }

    /// <summary>行 r 与行 r+1 的平均绝对差。</summary>
    private static double RowDiff(ReadOnlySpan<byte> y, int w, int h, int r)
    {
        if (r < 0 || r + 1 >= h) return double.NaN;
        long sum = 0;
        int b0 = r * w, b1 = (r + 1) * w;
        for (int x = 0; x < w; x++)
            sum += Math.Abs(y[b0 + x] - y[b1 + x]);
        return (double)sum / w;
    }

    /// <summary>从左向右扫描，返回第一个「与右邻列存在差异」的列号（= 内容起始列）。全 0 则返回 0。</summary>
    private static int FindLeftContentEdge(ReadOnlySpan<byte> y, int w, int h)
    {
        int limit = Math.Min(ScanDepth, w - 1);
        for (int x = 0; x < limit; x++)
            if (ColumnDiff(y, w, h, x) > 0.0)
                return x;
        return 0;
    }

    /// <summary>从右向左扫描，返回尾部连续「与左邻列完全相同」的列数（= 右填充宽度）。</summary>
    private static int FindRightPadWidth(ReadOnlySpan<byte> y, int w, int h)
    {
        int pad = 0;
        int limit = Math.Min(ScanDepth, w - 1);
        for (int i = 0; i < limit; i++)
        {
            int x = w - 2 - i;              // 比较 (x, x+1)
            if (x < 0) break;
            if (ColumnDiff(y, w, h, x) > 0.0) break;
            pad++;
        }
        return pad;
    }

    /// <summary>从上向下扫描，返回第一个「与下邻行存在差异」的行号（= 内容起始行）。</summary>
    private static int FindTopContentEdge(ReadOnlySpan<byte> y, int w, int h)
    {
        int limit = Math.Min(ScanDepth, h - 1);
        for (int r = 0; r < limit; r++)
            if (RowDiff(y, w, h, r) > 0.0)
                return r;
        return 0;
    }

    /// <summary>从下向上扫描，返回尾部连续「与上邻行完全相同」的行数（= 下填充高度）。</summary>
    private static int FindBottomPadHeight(ReadOnlySpan<byte> y, int w, int h)
    {
        int pad = 0;
        int limit = Math.Min(ScanDepth, h - 1);
        for (int i = 0; i < limit; i++)
        {
            int r = h - 2 - i;
            if (r < 0) break;
            if (RowDiff(y, w, h, r) > 0.0) break;
            pad++;
        }
        return pad;
    }

    /// <summary>输出四个边缘各 12 个差值，供人工复核自动判定。</summary>
    private static string BuildEdgeDetail(ReadOnlySpan<byte> y, int w, int h)
    {
        var sb = new StringBuilder();
        sb.Append("左缘列差 x=0..11: ");
        for (int x = 0; x < 12 && x + 1 < w; x++)
            sb.Append(ColumnDiff(y, w, h, x).ToString("F2", CultureInfo.InvariantCulture)).Append(' ');
        sb.Append("\n             右缘列差 x=").Append(w - 13).Append("..").Append(w - 2).Append(": ");
        for (int x = Math.Max(0, w - 13); x + 1 < w; x++)
            sb.Append(ColumnDiff(y, w, h, x).ToString("F2", CultureInfo.InvariantCulture)).Append(' ');
        sb.Append("\n             上缘行差 r=0..11: ");
        for (int r = 0; r < 12 && r + 1 < h; r++)
            sb.Append(RowDiff(y, w, h, r).ToString("F2", CultureInfo.InvariantCulture)).Append(' ');
        sb.Append("\n             下缘行差 r=").Append(h - 13).Append("..").Append(h - 2).Append(": ");
        for (int r = Math.Max(0, h - 13); r + 1 < h; r++)
            sb.Append(RowDiff(y, w, h, r).ToString("F2", CultureInfo.InvariantCulture)).Append(' ');
        sb.Append("\n             （差值恒 0 = 编码填充区；第一个非 0 处即真实内容边界）");
        return sb.ToString();
    }

    // ────────────────────── BMP 落盘 ──────────────────────

    /// <summary>把 NV12 写成 32bpp BGRA 的 BMP（自上而下，负高度）。</summary>
    private static void WriteNv12AsBmp(ReadOnlySpan<byte> nv12, int w, int h, string path, bool grayscaleOnly)
    {
        const int headerSize = 54;
        int stride = w * 4;
        var bmp = new byte[headerSize + stride * h];
        WriteBmpHeader(bmp, w, h);

        int uvBase = w * h;
        for (int row = 0; row < h; row++)
        {
            int dst = headerSize + row * stride;
            int yRow = row * w;
            int uvRow = uvBase + (row / 2) * w;
            for (int x = 0; x < w; x++)
            {
                byte yv = nv12[yRow + x];
                byte b, g, r;
                if (grayscaleOnly)
                {
                    b = g = r = yv;
                }
                else
                {
                    // BT.601 limited range（与 b2 ImageUtil.Nv12ToRgba 同口径，便于交叉对比）
                    int uvIdx = uvRow + (x & ~1);
                    int u = nv12[uvIdx] - 128;
                    int v = nv12[uvIdx + 1] - 128;
                    int c = yv - 16;
                    r = ClampByte((298 * c + 409 * v + 128) >> 8);
                    g = ClampByte((298 * c - 100 * u - 208 * v + 128) >> 8);
                    b = ClampByte((298 * c + 516 * u + 128) >> 8);
                }
                int o = dst + x * 4;
                bmp[o] = b;
                bmp[o + 1] = g;
                bmp[o + 2] = r;
                bmp[o + 3] = 255;
            }
        }
        File.WriteAllBytes(path, bmp);
    }

    private static byte ClampByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    private static void WriteBmpHeader(byte[] bmp, int w, int h)
    {
        int stride = w * 4;
        int imageSize = stride * h;
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        WriteI32(bmp, 2, 54 + imageSize);   // 文件大小
        WriteI32(bmp, 10, 54);              // 像素数据偏移
        WriteI32(bmp, 14, 40);              // BITMAPINFOHEADER 大小
        WriteI32(bmp, 18, w);
        WriteI32(bmp, 22, -h);              // 负数 = 自上而下
        bmp[26] = 1;                        // planes
        bmp[28] = 32;                       // bpp
        WriteI32(bmp, 34, imageSize);
    }

    private static void WriteI32(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }
}

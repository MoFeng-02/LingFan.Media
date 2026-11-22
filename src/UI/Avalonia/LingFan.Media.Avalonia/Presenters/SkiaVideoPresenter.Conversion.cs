using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

// SkiaVideoPresenter 的 Conversion 职责段（部分类，由 SkiaVideoPresenter.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/Present/Render/Clear/Resize/Dispose）在主文件；软帧 CPU 转换与布局计算。
public sealed partial class SkiaVideoPresenter
{
    /// <summary>在紧凑 BGRA 缓冲上填充矩形（staging 格式恒定 Bgra8888，步长 = 宽×4）。</summary>
    private static void FillRect(byte[] buf, int w, int h, int x, int y, int rw, int rh, byte b, byte g, byte r)
    {
        int x1 = Math.Max(0, x), y1 = Math.Max(0, y);
        int x2 = Math.Min(w, x + rw), y2 = Math.Min(h, y + rh);
        for (int yy = y1; yy < y2; yy++)
        {
            int off = (yy * w + x1) * 4;
            for (int xx = x1; xx < x2; xx++, off += 4)
            {
                buf[off] = b;
                buf[off + 1] = g;
                buf[off + 2] = r;
                buf[off + 3] = 255;
            }
        }
    }

    /// <summary>烧入帧水印：4 位十进制序号 + 五色标条 + 竖条纹（三者分别检验时序/色彩/行距）。</summary>
    private static void DrawFrameWatermark(byte[] buf, int w, int h, int index)
    {
        const int scale = 16;
        const int digits = 4;
        int dw = 5 * scale;          // 单字宽 80
        int dh = 7 * scale;          // 单字高 112
        int gap = scale;             // 字间距 16
        int textW = digits * dw + (digits - 1) * gap;  // 368
        int panelW = textW + 24;     // 392
        int panelH = 24 + dh + 12 + 48 + 12 + 64 + 12; // 284
        if (w < panelW + 24 || h < panelH + 24) return;

        // 不透明黑底板：任何画面内容下水印都清晰可读
        FillRect(buf, w, h, 12, 12, panelW, panelH, 0, 0, 0);

        // 1) 帧序号（白色数字；个位在最右，取模 10000 循环）
        int x0 = 24, y0 = 24;
        int v = index % 10000;
        for (int d = 0; d < digits; d++)
        {
            int digit = (v / Pow10[digits - 1 - d]) % 10;
            string glyph = DigitFont[digit];
            int dx = x0 + d * (dw + gap);
            for (int row = 0; row < 7; row++)
            {
                for (int col = 0; col < 5; col++)
                {
                    if (glyph[row * 5 + col] != '1') continue;
                    FillRect(buf, w, h, dx + col * scale, y0 + row * scale, scale, scale, 255, 255, 255);
                }
            }
        }

        // 2) 五色标条：蓝/绿/红/白/黑 —— 一眼看出通道是否互换或色彩矩阵错误
        int barY = y0 + dh + 12;
        int stripeW = textW / 5;
        FillRect(buf, w, h, x0 + stripeW * 0, barY, stripeW, 48, 255, 0, 0);    // B
        FillRect(buf, w, h, x0 + stripeW * 1, barY, stripeW, 48, 0, 255, 0);    // G
        FillRect(buf, w, h, x0 + stripeW * 2, barY, stripeW, 48, 0, 0, 255);    // R
        FillRect(buf, w, h, x0 + stripeW * 3, barY, stripeW, 48, 255, 255, 255);// W
        FillRect(buf, w, h, x0 + stripeW * 4, barY, textW - stripeW * 4, 48, 0, 0, 0); // K

        // 3) 竖条纹：若行距（stride）处理错误，竖条会呈现斜纹或整块错位
        int stripeY = barY + 48 + 12;
        for (int i = 0; i < 32; i++)
        {
            bool on = (i & 1) == 0;
            FillRect(buf, w, h, x0 + i * 12, stripeY, 12, 64,
                on ? (byte)255 : (byte)0, on ? (byte)255 : (byte)0, on ? (byte)255 : (byte)0);
        }
    }

    private static void AppendHex(System.Text.StringBuilder sb, ReadOnlySpan<byte> d, int off, int n)
    {
        for (int i = 0; i < n; i++)
        {
            int idx = off + i;
            sb.Append(idx >= 0 && idx < d.Length ? d[idx].ToString("X2") : "--");
        }
    }

    /// <summary>YUV 帧指纹：Y 第 500 行列 16..23 + 色度区第 250 行偏移 16..23（两侧口径必须一致）。</summary>
    private static string FpYuv(ReadOnlySpan<byte> d, int w, int h)
    {
        int yRow = Math.Min(500, Math.Max(0, h - 1));
        int cRow = Math.Min(250, Math.Max(0, h / 2 - 1));
        var sb = new System.Text.StringBuilder(40);
        AppendHex(sb, d, yRow * w + 16, 8);
        sb.Append('/');
        AppendHex(sb, d, w * h + cRow * w + 16, 8);
        return sb.ToString();
    }

    /// <summary>紧凑 BGRA 缓冲指纹：第 500 行像素 16..23（字节 64..71）。</summary>
    private static string FpBgra(ReadOnlySpan<byte> d, int w, int h)
    {
        int row = Math.Min(500, Math.Max(0, h - 1));
        var sb = new System.Text.StringBuilder(40);
        AppendHex(sb, d, row * w * 4 + 64, 8);
        return sb.ToString();
    }

    /// <summary>
    /// 写入打包 4 字节像素（BGRA32/RGBA32），按行拷贝并处理源 stride 对齐填充。
    /// </summary>
    private static unsafe void WriteBgra(ReadOnlySpan<byte> src, int width, int height, int srcStride, IntPtr dest, int destStride)
    {
        int srcStride2 = srcStride > 0 ? srcStride : destStride;
        byte* d = (byte*)dest;

        if (srcStride2 == destStride)
        {
            var copyLength = Math.Min(src.Length, destStride * height);
            src.Slice(0, copyLength).CopyTo(new Span<byte>(d, copyLength));
        }
        else
        {
            var rowBytes = Math.Min(srcStride2, destStride);
            for (int y = 0; y < height; y++)
            {
                int srcOffset = y * srcStride2;
                int available = src.Length - srcOffset;
                if (available <= 0) break;
                int n = Math.Min(rowBytes, available);
                src.Slice(srcOffset, n).CopyTo(new Span<byte>(d + (nuint)(y * destStride), n));
            }
        }
    }

    /// <summary>
    /// RGB24（打包 R,G,B 各 1 字节）→ BGRA32。AOT 友好，纯 Span 逐像素写。
    /// </summary>
    internal static unsafe void WriteRgb24ToBgra(SoftwareFrameResource sw, IntPtr dest, int destStride)
    {
        int srcStride = sw.Stride > 0 ? sw.Stride : sw.Width * 3;
        byte* dst = (byte*)dest;
        for (int y = 0; y < sw.Height; y++)
        {
            int srcRow = y * srcStride;
            byte* dstRow = dst + (nuint)(y * destStride);
            for (int x = 0; x < sw.Width; x++)
            {
                int s = srcRow + x * 3;
                byte r = sw.Data.Span[s], g = sw.Data.Span[s + 1], b = sw.Data.Span[s + 2];
                int di = x * 4;
                dstRow[di] = b;
                dstRow[di + 1] = g;
                dstRow[di + 2] = r;
                dstRow[di + 3] = 255;
            }
        }
    }

    private static short[] BuildYuvLut(Func<float, float> coeff)
    {
        var t = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int v = (int)Math.Round(coeff(i - 128));
            t[i] = v < short.MinValue ? short.MinValue : v > short.MaxValue ? short.MaxValue : (short)v;
        }
        return t;
    }

    /// <summary>
    /// YUV 平面/半平面（YUV420P/YUV422P/YUV444P/NV12/NV21）→ BGRA32。
    /// 假设紧凑平面布局（解码器 av_image_copy_to_buffer align=1 产出）：平面无行内填充、连续排列，
    /// 平面偏移仅由宽高与色度子采样推导（与 av_image_copy_to_buffer 的打包语义一致）。
    /// </summary>
    internal static unsafe void WriteYuvToBgra(SoftwareFrameResource sw, IntPtr dest, int destStride)
    {
        byte* dstBase = (byte*)dest;
        int w = sw.Width, h = sw.Height;
        // 源行 stride：优先用 SoftwareFrameResource 报告的 Stride（支持对齐/padding），
        // 未设置 (>0 视为有效) 时回退到 width。NV12/YUV420P 的 Y 与 UV 共用同一行 stride。
        int srcStride = sw.Stride > 0 ? sw.Stride : w;
        bool isNv = sw.Format is LingFan.Media.Abstractions.PixelFormat.NV12 or LingFan.Media.Abstractions.PixelFormat.NV21;

        int chromaW, chromaH;
        bool hSub, vSub;
        switch (sw.Format)
        {
            case LingFan.Media.Abstractions.PixelFormat.YUV444P:
                chromaW = w; chromaH = h; hSub = false; vSub = false; break;
            case LingFan.Media.Abstractions.PixelFormat.YUV422P:
                chromaW = (w + 1) / 2; chromaH = h; hSub = true; vSub = false; break;
            case LingFan.Media.Abstractions.PixelFormat.NV12:
            case LingFan.Media.Abstractions.PixelFormat.NV21:
                chromaW = (w + 1) / 2; chromaH = (h + 1) / 2; hSub = true; vSub = true; break;
            default: // YUV420P
                chromaW = (w + 1) / 2; chromaH = (h + 1) / 2; hSub = true; vSub = true; break;
        }

        int ySize = w * h;
        int uOff = ySize;
        int vOff = ySize + chromaW * chromaH;
        int uvOff = ySize;

        // 一次性诊断：打 UV 前 8 字节 + Y 前 2 字节 + sw.Stride + sw.Format，定位花屏成因。
        if (!_diagOnce)
        {
            _diagOnce = true;
            var d = sw.Data.Span;
            var sb = new System.Text.StringBuilder();
            sb.Append("[SKIA-CONVERT-DIAG] fmt=").Append(sw.Format)
              .Append(" w=").Append(w).Append(" h=").Append(h)
              .Append(" sw.Stride=").Append(sw.Stride)
              .Append(" srcStrideUsed=").Append(srcStride)
              .Append(" chromaW=").Append(chromaW).Append(" chromaH=").Append(chromaH)
              .Append(" ySize=").Append(ySize)
              .Append(" uvOff=").Append(uvOff).Append(" vOff=").Append(vOff)
              .Append(" uvPlen=").Append(d.Length - uvOff)
              .Append(" | Y[0,1]=").Append(d[0]).Append(',').Append(d[1])
              .Append(" | UV[0..7]=");
            for (int i = 0; i < 8 && uvOff + i < d.Length; i++) sb.Append(d[uvOff + i]).Append(i < 7 ? "," : "");
            _diagLogger?.LogInformation(sb.ToString());
        }

        // 色彩矩阵选择：默认（未指定 / BT.601-Full）走既有 LUT（BT.601-Full），其余按色彩空间取 float 系数。
        // limited range 需对 Y 做 (Y-16)×1.1644 补偿；系数来源见行 70-84 文档依据。
        var ci = sw.ColorInfo;
        bool useLut = ci is null || !ci.Value.IsSpecified
            || (ci.Value.Standard == LingFan.Media.Abstractions.ColorStandard.Bt601
                && ci.Value.Range == LingFan.Media.Abstractions.ColorRange.Full);
        float kR = 0f, kU = 0f, kV = 0f, kUB = 0f, yScale = 1f;
        int yOff = 0;
        if (!useLut)
        {
            // 此时 useLut=false ⇒ ci 非空（见上方短路条件）；用 ! 抑制可空告警。
            var std = ci!.Value.Standard;
            var range = ci.Value.Range;
            yScale = range == LingFan.Media.Abstractions.ColorRange.Limited ? 1.1644f : 1f;
            yOff = range == LingFan.Media.Abstractions.ColorRange.Limited ? 16 : 0;
            switch (std)
            {
                case LingFan.Media.Abstractions.ColorStandard.Bt709:
                    kR = 1.5748f; kU = 0.1873f; kV = 0.4681f; kUB = 1.8556f; break;
                case LingFan.Media.Abstractions.ColorStandard.Bt2020:
                    kR = 1.6787f; kU = 0.1873f; kV = 0.6504f; kUB = 2.1418f; break;
                default: // BT.601 limited
                    kR = 1.5960f; kU = 0.3918f; kV = 0.8130f; kUB = 2.0173f; break;
            }
        }

        // 【性能关键】内层热循环：裸指针（消除 Span 边界检查）+ 每像素对步进（U/V 共享一次读取）
        // + 指针递增写。实测：Span 逐像素索引在高分辨率下每帧耗时达秒级（画面近乎定格）；
        // 指针化 + 双像素步进为数量级改善，Release JIT 下更低。
        // CPU 逐像素转换终究是 Tier0 兜底路径；1080p+ 的正解是 Tier2 硬解硬渲（GPU 采样 YUV）。
        var srcSpan = sw.Data.Span;
        fixed (byte* srcBase = srcSpan)
        fixed (short* pRv = Rv, pGu = Gu, pGv = Gv, pBu = Bu)
        {
            byte* yPlane = srcBase;
            byte* uPlane = srcBase + uOff;
            byte* vPlane = srcBase + vOff;
            byte* uvPlane = srcBase + uvOff;

            for (int y = 0; y < h; y++)
            {
                byte* dstRow = dstBase + (nuint)(y * destStride);
                byte* yRow = yPlane + (nuint)(y * srcStride);
                int cRow = vSub ? (y >> 1) : y;
                byte* uRow = uPlane + (nuint)(cRow * chromaW);
                byte* vRow = vPlane + (nuint)(cRow * chromaW);
                byte* uvRow = uvPlane + (nuint)(cRow * srcStride);
                // 【色度垂直双线性上采样】消除 NV12 4:2:0 在 y 方向的"水平边"色度块。
                // y 奇数时 cRow0 != cRow1，U/V = (uvRow0 + uvRow1) / 2；y 偶数走最近邻（uvRow1 = uvRow0）。
                // 水平方向仍为 2:1 共享（每 2 个 Y 像素 1 个 UV 对）——完整消除需水平+垂直双线性，
                // 但垂直一维插值已消除 2x2 方格的"水平边"（3x 缩放下从 6x6 方格→6x3 横条，视觉改善明显）。
                int cRow1 = cRow + 1;
                bool vertChromaInterp = vSub && (y & 1) == 1 && cRow1 < chromaH;
                byte* uvRow1 = vertChromaInterp ? uvPlane + (nuint)(cRow1 * srcStride) : uvRow;
                byte* uRow1 = vertChromaInterp ? uPlane + (nuint)(cRow1 * chromaW) : uRow;
                byte* vRow1 = vertChromaInterp ? vPlane + (nuint)(cRow1 * chromaW) : vRow;

                // 双像素步进：4:2:0/4:2:2 色度水平共享（hSub），偶数列取一次 U/V 写两像素。
                int x = 0;
                for (; x + 1 < w; x += 2)
                {
                    int yv0 = yRow[x];
                    int yv1 = yRow[x + 1];

                    int cu, cv;
                    if (isNv)
                    {
                        // 【水平+垂直完整双线性色度上采样】
                        // 取 4 邻 UV 像素对：(cRow0, cCol0), (cRow0, cCol1), (cRow1, cCol0), (cRow1, cCol1)
                        // x 偶数 + y 偶数：U = U00 (最近邻)
                        // x 奇数 + y 偶数：U = (U00 + U01) / 2
                        // x 偶数 + y 奇数：U = (U00 + U10) / 2
                        // x 奇数 + y 奇数：U = (U00 + U01 + U10 + U11) / 4
                        // V 同理。彻底消除 NV12 4:2:0 在 x 方向（每 2 个 Y 共享 1 个 UV）的色度"垂直边"块。
                        int cCol0_off = x & ~1;          // (x&~1)/2 == cCol0 索引，U 在偶字节
                        int cCol1_off = Math.Min(cCol0_off + 2, Math.Max(0, srcStride - 2)); // 右侧边界 clamp，防读到下一行
                        byte* p00 = uvRow + cCol0_off;
                        byte* p01 = uvRow + cCol1_off;
                        int cu00, cv00, cu01, cv01;
                        if (sw.Format == LingFan.Media.Abstractions.PixelFormat.NV12)
                        {
                            cu00 = p00[0]; cv00 = p00[1];
                            cu01 = p01[0]; cv01 = p01[1];
                        }
                        else // NV21: V 在前
                        {
                            cv00 = p00[0]; cu00 = p00[1];
                            cv01 = p01[0]; cu01 = p01[1];
                        }
                        int cu10 = 0, cv10 = 0, cu11 = 0, cv11 = 0;
                        if (vertChromaInterp)
                        {
                            byte* p10 = uvRow1 + cCol0_off;
                            byte* p11 = uvRow1 + cCol1_off;
                            if (sw.Format == LingFan.Media.Abstractions.PixelFormat.NV12)
                            {
                                cu10 = p10[0]; cv10 = p10[1];
                                cu11 = p11[0]; cv11 = p11[1];
                            }
                            else
                            {
                                cv10 = p10[0]; cu10 = p10[1];
                                cv11 = p11[0]; cu11 = p11[1];
                            }
                        }
                        // 4 邻加权平均（权重取决于 x 和 y 的奇偶性）
                        if (vertChromaInterp)
                        {
                            if ((x & 1) == 0) // x 偶数
                            {
                                cu = (cu00 + cu10 + 1) >> 1;
                                cv = (cv00 + cv10 + 1) >> 1;
                            }
                            else // x 奇数
                            {
                                cu = (cu00 + cu01 + cu10 + cu11 + 2) >> 2;
                                cv = (cv00 + cv01 + cv10 + cv11 + 2) >> 2;
                            }
                        }
                        else
                        {
                            if ((x & 1) == 0) // x 偶数
                            {
                                cu = cu00;
                                cv = cv00;
                            }
                            else // x 奇数
                            {
                                cu = (cu00 + cu01 + 1) >> 1;
                                cv = (cv00 + cv01 + 1) >> 1;
                            }
                        }
                    }
                    else
                    {
                        int cCol = hSub ? (x >> 1) : x;
                        cu = uRow[cCol];
                        cv = vRow[cCol];
                    }

                    int r, g, b, du, dv, yl;
                    if (useLut)
                    {
                        int rv = pRv[cv], gu = pGu[cu], gv = pGv[cv], bu = pBu[cu];

                        // 像素 0
                        r = yv0 + rv; g = yv0 + gu + gv; b = yv0 + bu;
                        byte* d = dstRow + (nuint)(x * 4);
                        d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        d[3] = 255;

                        // 像素 1（共享 U/V）
                        r = yv1 + rv; g = yv1 + gu + gv; b = yv1 + bu;
                        d += 4;
                        d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        d[3] = 255;
                    }
                    else
                    {
                        du = cu - 128; dv = cv - 128;
                        int kr = (int)(dv * kR), kg1 = (int)(du * kU), kg2 = (int)(dv * kV), kb = (int)(du * kUB);
                        yl = (int)((yv0 - yOff) * yScale);

                        byte* d = dstRow + (nuint)(x * 4);
                        r = yl + kr; g = yl - kg1 - kg2; b = yl + kb;
                        d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        d[3] = 255;

                        yl = (int)((yv1 - yOff) * yScale);
                        d += 4;
                        r = yl + kr; g = yl - kg1 - kg2; b = yl + kb;
                        d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        d[3] = 255;
                    }
                }

                // 奇数尾列（宽为奇数时）
                if (x < w)
                {
                    int yv = yRow[x];
                    int cu, cv;
                    if (isNv)
                    {
                        byte* uv = uvRow + (nuint)(x & ~1);
                        if (sw.Format == LingFan.Media.Abstractions.PixelFormat.NV12)
                        {
                            cu = uv[0];
                            cv = uv[1];
                        }
                        else
                        {
                            cv = uv[0];
                            cu = uv[1];
                        }
                    }
                    else
                    {
                        int cCol = hSub ? (x >> 1) : x;
                        cu = uRow[cCol];
                        cv = vRow[cCol];
                    }

                    int r, g, b;
                    if (useLut)
                    {
                        r = yv + pRv[cv]; g = yv + pGu[cu] + pGv[cv]; b = yv + pBu[cu];
                    }
                    else
                    {
                        int du = cu - 128, dv = cv - 128;
                        int yl = (int)((yv - yOff) * yScale);
                        r = yl + (int)(dv * kR);
                        g = yl - (int)(du * kU) - (int)(dv * kV);
                        b = yl + (int)(du * kUB);
                    }
                    byte* d = dstRow + (nuint)(x * 4);
                    d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                    d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                    d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                    d[3] = 255;
                }
            }
        }
    }
}

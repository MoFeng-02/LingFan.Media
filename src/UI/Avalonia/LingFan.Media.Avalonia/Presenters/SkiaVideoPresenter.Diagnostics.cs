using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
namespace LingFan.Media.Avalonia;

// SkiaVideoPresenter 的诊断水印职责段（部分类，按职责拆分，编译期等价）。
// 帧水印烧入与全链路指纹采样（FP-SRC/FP-STG 对账）；像素转换在 Conversion 主段。
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
}

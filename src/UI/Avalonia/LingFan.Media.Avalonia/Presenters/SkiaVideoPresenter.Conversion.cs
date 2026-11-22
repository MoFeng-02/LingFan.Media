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

}

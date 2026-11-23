using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Backends.VulkanVideo;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.Vulkan;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VulkanVideoHardDecodeProbe;

// 帧落盘诊断：把渲染器收到的真实帧（CPU 软解 / GPU 硬解回读）转 RGBA 后写极简 PNG。
// 自包含零依赖，不引用任何渲染器/后端模块，保持探针的独立取证地位。
internal static class FrameDumper
{
    internal static int DumpedCount;

    internal static void DumpFrame(VideoFrame frame, int presentIndex, string dir)
    {
        // 诊断上限：硬解零拷贝诊断只关注前 3 帧（关键帧 + 紧随其后的 P 帧），避免落大量 PNG。
        if (DumpedCount >= 3) return;
        try
        {
            byte[]? rgba = null; int w = 0, h = 0;
            if (frame.Resource is SoftwareFrameResource sfr)
            {
                w = sfr.Width; h = sfr.Height;
                var span = sfr.Data.Span;
                switch (sfr.Format)
                {
                    case PixelFormat.NV12:
                        rgba = new byte[w * h * 4];
                        SemiPlanarToRgba(span, w, h, rgba, false);
                        break;
                    case PixelFormat.NV21:
                        rgba = new byte[w * h * 4];
                        SemiPlanarToRgba(span, w, h, rgba, true);
                        break;
                    case PixelFormat.BGRA32:
                        rgba = new byte[w * h * 4];
                        ReorderBgraToRgba(span, w * h, rgba);
                        break;
                    case PixelFormat.RGBA32:
                        rgba = new byte[w * h * 4];
                        span.Slice(0, w * h * 4).CopyTo(rgba);
                        break;
                    default:
                        Console.WriteLine($"  [HEADFUL-SAVE] 帧 {presentIndex} 格式 {sfr.Format} 不可落盘(CPU)，跳过");
                        return;
                }
            }
            else if (frame.Resource is IGpuTextureResource gpu)
            {
                using var rb = gpu.ReadbackToCpu();
                w = rb.Width; h = rb.Height;
                var span = rb.Data.Span;
                rgba = new byte[w * h * 4];
                ReorderBgraToRgba(span, w * h, rgba);
            }
            else
            {
                Console.WriteLine($"  [HEADFUL-SAVE] 帧 {presentIndex} 资源类型未知，跳过");
                return;
            }

            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"frame_{presentIndex:D5}.png");
            EncodeRgbaPng(path, w, h, rgba);
            PrintPixelStats(presentIndex, rgba, w, h);
            Interlocked.Increment(ref DumpedCount);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [HEADFUL-SAVE] 帧 {presentIndex} 落盘失败: {ex.Message}");
        }
    }

    private static void SemiPlanarToRgba(ReadOnlySpan<byte> nv, int w, int h, byte[] rgba, bool nv21)
    {
        int ySize = w * h;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int yi = y * w + x;
                int ui = ySize + (y / 2) * w + (x / 2) * 2;
                int Y = nv[yi], U = nv[ui], V = nv[ui + 1];
                if (nv21) (U, V) = (V, U);
                int c = Y - 16, d = U - 128, e = V - 128;
                int o = yi * 4;
                rgba[o] = (byte)Clamp((298 * c + 409 * e + 128) >> 8);
                rgba[o + 1] = (byte)Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                rgba[o + 2] = (byte)Clamp((298 * c + 516 * d + 128) >> 8);
                rgba[o + 3] = 255;
            }
        }
    }

    private static void ReorderBgraToRgba(ReadOnlySpan<byte> bgra, int pixels, byte[] rgba)
    {
        for (int i = 0; i < pixels; i++)
        {
            int si = i * 4, di = i * 4;
            rgba[di] = bgra[si + 2];
            rgba[di + 1] = bgra[si + 1];
            rgba[di + 2] = bgra[si];
            rgba[di + 3] = bgra[si + 3];
        }
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// <summary>
    /// 量化落盘帧像素统计（绿屏数据驱动诊断）：直接回答「解码输出是否为空」——
    /// 若 maxR/G/B≈0 且非零像素 ≈0%，说明 DPB 内容为空（解码静默失败）；
    /// 若非零像素占比高，说明解码有真实数据，绿屏在显示/采样路径。
    /// </summary>
    private static void PrintPixelStats(int presentIndex, byte[] rgba, int w, int h)
    {
        ulong n = (ulong)((long)w * h);
        if (n == 0) return;
        long nonZero = 0;
        ulong sumR = 0, sumG = 0, sumB = 0;
        int maxR = 0, maxG = 0, maxB = 0;
        for (ulong i = 0; i < n; i++)
        {
            int r = rgba[(int)(i * 4)], g = rgba[(int)(i * 4 + 1)], b = rgba[(int)(i * 4 + 2)];
            if (r != 0 || g != 0 || b != 0) nonZero++;
            sumR += (ulong)r; sumG += (ulong)g; sumB += (ulong)b;
            if (r > maxR) maxR = r;
            if (g > maxG) maxG = g;
            if (b > maxB) maxB = b;
        }
        double pct = nonZero * 100.0 / (double)n;
        ulong meanR = sumR / n, meanG = sumG / n, meanB = sumB / n;
        Console.WriteLine($"  [HEADFUL-SAVE] 帧 {presentIndex} 统计: 尺寸={w}x{h} 非零像素={pct:F1}% " +
                          $"均值(R,G,B)=({meanR},{meanG},{meanB}) max(R,G,B)=({maxR},{maxG},{maxB})");
        if (pct < 1.0)
            Console.WriteLine($"  [HEADFUL-SAVE]   → DPB 内容疑似为空（解码静默失败 / 起始码 / SPS / 参考帧配置问题）");
        else if (meanR == 0 && meanG == 135 && meanB == 0)
            // 均值(0,135,0) = YuvToRgb(0,0,0) = NV12 全零 → 解码器一个像素都没写进去（绿屏成因，非显示/采样路径问题）
            Console.WriteLine($"  [HEADFUL-SAVE]   → DPB 仍为全零 NV12（均值(0,135,0)=YUV(0,0,0)），解码未写入真实像素");
        else
            Console.WriteLine($"  [HEADFUL-SAVE]   → DPB 含真实像素（解码有输出），绿屏在显示/采样/布局路径");
    }

    // 极简 PNG 编码器（RGBA / color type 6，ZLibStream 走 BCL，无第三方依赖）
    private static readonly uint[] CrcTable = BuildCrc();

    internal static void EncodeRgbaPng(string path, int w, int h, ReadOnlySpan<byte> rgba)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 0x08; ihdr[9] = 0x06;
        WriteChunk(fs, "IHDR", ihdr);

        using var ms = new MemoryStream();
        using (var zs = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[w * 4 + 1];
            row[0] = 0;
            for (int y = 0; y < h; y++)
            {
                rgba.Slice(y * w * 4, w * 4).CopyTo(row.AsSpan(1));
                zs.Write(row);
            }
        }
        WriteChunk(fs, "IDAT", ms.ToArray());
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream fs, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        fs.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        fs.Write(typeBytes);
        fs.Write(data);
        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, Crc(typeBytes, data));
        fs.Write(crcBuf);
    }

    private static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte x in a) crc = (crc >> 8) ^ CrcTable[(crc ^ x) & 0xFF];
        foreach (byte x in b) crc = (crc >> 8) ^ CrcTable[(crc ^ x) & 0xFF];
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
}

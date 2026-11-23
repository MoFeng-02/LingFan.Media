using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.OpenAL;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinuxHeadlessProbe;

// 帧落盘诊断：把视频帧（CPU 软解内存 / GPU 硬解回读）转 RGBA 后写极简 PNG。自包含零依赖。
// 支持 YUV420P（ffmpeg Linux 软解默认）/ NV12 / NV21 / BGRA32 / RGBA32。
// 均匀抽帧落盘比纯随机更利于判读（不会漏掉某一段），用于肉眼确认解码/上色是否正确。
internal static class FrameDumper
{
    internal static int DumpedCount;

    internal static void DumpFrame(VideoFrame frame, int frameIndex, string dir)
    {
        try
        {
            byte[]? rgba = null; int w = 0, h = 0;
            if (frame.Resource is SoftwareFrameResource sfr)
            {
                w = sfr.Width; h = sfr.Height;
                var span = sfr.Data.Span;
                switch (sfr.Format)
                {
                    case PixelFormat.YUV420P:
                        rgba = new byte[w * h * 4];
                        PlanarYuv420ToRgba(span, w, h, rgba);
                        break;
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
                        Console.WriteLine($"  [SAVE] 帧 {frameIndex} 格式 {sfr.Format} 不可落盘，跳过");
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
                Console.WriteLine($"  [SAVE] 帧 {frameIndex} 资源类型未知，跳过");
                return;
            }

            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"frame_{frameIndex:D6}.png");
            EncodeRgbaPng(path, w, h, rgba);
            Interlocked.Increment(ref DumpedCount);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [SAVE] 帧 {frameIndex} 落盘失败: {ex.Message}");
        }
    }

    // I420 planar（紧凑布局：Y[w*h] U[w*h/4] V[w*h/4]，无 stride padding）。
    private static void PlanarYuv420ToRgba(ReadOnlySpan<byte> data, int w, int h, byte[] rgba)
    {
        int ySize = w * h;
        int uvSize = w * h / 4;
        ReadOnlySpan<byte> yp = data.Slice(0, ySize);
        ReadOnlySpan<byte> up = data.Slice(ySize, uvSize);
        ReadOnlySpan<byte> vp = data.Slice(ySize + uvSize, uvSize);
        int halfW = w / 2;
        for (int y = 0; y < h; y++)
        {
            int yRow = y * w;
            int uvRow = (y / 2) * halfW;
            int o = yRow * 4;
            for (int x = 0; x < w; x++)
            {
                int Y = yp[yRow + x];
                int U = up[uvRow + (x / 2)];
                int V = vp[uvRow + (x / 2)];
                int c = Y - 16, d = U - 128, e = V - 128;
                rgba[o]     = (byte)Clamp((298 * c + 409 * e + 128) >> 8);
                rgba[o + 1] = (byte)Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                rgba[o + 2] = (byte)Clamp((298 * c + 516 * d + 128) >> 8);
                rgba[o + 3] = 255;
                o += 4;
            }
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
                rgba[o]     = (byte)Clamp((298 * c + 409 * e + 128) >> 8);
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

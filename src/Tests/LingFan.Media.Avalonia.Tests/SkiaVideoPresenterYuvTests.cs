using System;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Avalonia.Tests;

/// <summary>
/// SkiaVideoPresenter 像素格式转换单元测试（U11）。
/// 验证 YUV420P / YUV444P / NV12 / RGB24 → BGRA32 的转换数学正确性。
/// 纯 CPU 逻辑，无需 Avalonia 渲染表面；通过 internal <see cref="SkiaVideoPresenter.WriteYuvToBgra"/> 直接验证。
/// </summary>
public class SkiaVideoPresenterYuvTests
{
    // BT.601 全范围下，纯红 (R=255,G=0,B=0) 的 YUV 近似值
    private const byte RedY = 76;   // 0.299*255 ≈ 76
    private const byte RedU = 85;   // 128 - 0.169*255 ≈ 85
    private const byte RedV = 255;  // 128 + 0.5*255 ≈ 255（限幅）

    [Fact]
    public void Yuv420P_RedImage_ConvertsToRedBgra()
    {
        // 2x2 全红 YUV420P（紧凑平面：Y[4] + U[1] + V[1]）
        var data = new byte[] { RedY, RedY, RedY, RedY, RedU, RedV };
        var sw = new SoftwareFrameResource(2, 2, PixelFormat.YUV420P, data.AsMemory());

        var dest = new byte[2 * 2 * 4];
        ConvertAndAssertRed(data, sw, dest, destStride: 2 * 4);
    }

    [Fact]
    public void Yuv444P_RedImage_ConvertsToRedBgra()
    {
        // 2x2 全红 YUV444P（Y[4] + U[4] + V[4]）
        var data = new byte[]
        {
            RedY, RedY, RedY, RedY,
            RedU, RedU, RedU, RedU,
            RedV, RedV, RedV, RedV
        };
        var sw = new SoftwareFrameResource(2, 2, PixelFormat.YUV444P, data.AsMemory());

        var dest = new byte[2 * 2 * 4];
        ConvertAndAssertRed(data, sw, dest, destStride: 2 * 4);
    }

    [Fact]
    public void Nv12_RedImage_ConvertsToRedBgra()
    {
        // 2x2 全红 NV12（Y[4] + 交错 UV[4]：色度行宽 = w*2 = 4 字节，与 av_image_copy_to_buffer 紧凑布局一致）
        var data = new byte[] { RedY, RedY, RedY, RedY, RedU, RedV, RedU, RedV };
        var sw = new SoftwareFrameResource(2, 2, PixelFormat.NV12, data.AsMemory());

        var dest = new byte[2 * 2 * 4];
        ConvertAndAssertRed(data, sw, dest, destStride: 2 * 4);
    }

    [Fact]
    public void Rgb24_RedImage_ConvertsToRedBgra()
    {
        // 2x2 全红 RGB24（每像素 3 字节 R,G,B）
        var data = new byte[]
        {
            255, 0, 0,  255, 0, 0,
            255, 0, 0,  255, 0, 0
        };
        var sw = new SoftwareFrameResource(2, 2, PixelFormat.RGB24, data.AsMemory());

        var dest = new byte[2 * 2 * 4];
        var handle = GCHandle.Alloc(dest, GCHandleType.Pinned);
        try
        {
            SkiaVideoPresenter.WriteRgb24ToBgra(data.AsSpan(), sw, handle.AddrOfPinnedObject(), 2 * 4);
        }
        finally
        {
            handle.Free();
        }

        // RGB24 走 WriteRgb24ToBgra（精确，非 LUT 近似），应为纯红
        AssertRedPixel(dest, 0, expectR: 255);
        AssertRedPixel(dest, 4, expectR: 255);
        AssertRedPixel(dest, 8, expectR: 255);
        AssertRedPixel(dest, 12, expectR: 255);
    }

    private static void ConvertAndAssertRed(byte[] srcData, SoftwareFrameResource sw, byte[] dest, int destStride)
    {
        var handle = GCHandle.Alloc(dest, GCHandleType.Pinned);
        try
        {
            SkiaVideoPresenter.WriteYuvToBgra(srcData.AsSpan(), sw, handle.AddrOfPinnedObject(), destStride);
        }
        finally
        {
            handle.Free();
        }

        // LUT 近似应得到接近纯红的结果（R≈254 因舍入），所有 4 像素一致
        AssertRedPixel(dest, 0, expectR: 254);
        AssertRedPixel(dest, 4, expectR: 254);
        AssertRedPixel(dest, 8, expectR: 254);
        AssertRedPixel(dest, 12, expectR: 254);
    }

    private static void AssertRedPixel(byte[] dest, int offset, int expectR)
    {
        // BGRA 布局：dest[offset]=B, [offset+1]=G, [offset+2]=R, [offset+3]=A
        Assert.Equal(0, dest[offset]);       // B
        Assert.Equal(0, dest[offset + 1]);   // G
        Assert.InRange(dest[offset + 2], expectR - 2, expectR + 2); // R（容差舍入）
        Assert.Equal(255, dest[offset + 3]); // A
    }
}

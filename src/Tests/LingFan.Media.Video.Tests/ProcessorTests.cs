using System;
using LingFan.Media.Abstractions;
using LingFan.Media.Video;
using LingFan.Media.Video.Processors;
using Xunit;

namespace LingFan.Media.Video.Tests;

/// <summary>
/// 视频处理器真实算法验证（纯 CPU，无需媒体文件）。
/// 重点校验“像素级正确性”与“所有权转移（输入帧 Dispose / 透传不 Dispose）”。
/// </summary>
public class ProcessorTests
{
    private static VideoFrame MakeBgra(int w, int h, byte[] bgra, TimeSpan duration)
    {
        var res = new SoftwareFrameResource(w, h, PixelFormat.BGRA32, w * h * 4);
        bgra.CopyTo(res.Data.Span);
        return new VideoFrame(w, h, PixelFormat.BGRA32, res, TimeSpan.Zero, duration, false);
    }

    private static ReadOnlySpan<byte> Pix(VideoFrame f) =>
        ((SoftwareFrameResource)f.Resource!).Data.Span;

    [Fact]
    public void Scaling_Identity_ReturnsSameFrameUntouched()
    {
        var input = MakeBgra(2, 2, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], TimeSpan.Zero);
        var p = new ScalingProcessor(2, 2); // 与目标尺寸一致 → 透传

        var result = p.Process(input);

        Assert.True(ReferenceEquals(input, result));
        Assert.False(input.IsDisposed); // 透传不 Dispose
    }

    [Fact]
    public void Scaling_Bilinear_ConstantColorPreserved()
    {
        // 2x2 纯红（BGRA 顺序：B,G,R,A）
        var input = MakeBgra(2, 2, [255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255], TimeSpan.Zero);
        var p = new ScalingProcessor(1, 1); // 缩到 1x1

        var result = p.Process(input);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Width);
        Assert.Equal(1, result.Height);
        Assert.True(input.IsDisposed); // 真实缩放：输入帧被 Dispose
        var px = Pix(result);
        Assert.Equal(255, px[0]); // B
        Assert.Equal(0, px[1]);   // G
        Assert.Equal(0, px[2]);   // R
        Assert.Equal(255, px[3]); // A
    }

    [Fact]
    public void ColorSpace_BgraToRgba_SwapsRAndB()
    {
        // BGRA 顺序：[B=1, G=2, R=3, A=4]
        var input = MakeBgra(1, 1, [1, 2, 3, 4], TimeSpan.Zero);
        var p = new ColorSpaceConverter(PixelFormat.RGBA32);

        var result = p.Process(input);

        Assert.NotNull(result);
        Assert.Equal(PixelFormat.RGBA32, result!.Format);
        Assert.True(input.IsDisposed);
        var px = Pix(result);
        Assert.Equal(3, px[0]); // R（原 B 位）
        Assert.Equal(2, px[1]); // G
        Assert.Equal(1, px[2]); // B（原 R 位）
        Assert.Equal(4, px[3]); // A
    }

    [Fact]
    public void Deinterlace_Blend_AveragesFields()
    {
        // 2x2：偶数行（顶场）红，奇数行（底场）蓝
        var input = MakeBgra(2, 2,
            [255, 0, 0, 255, 255, 0, 0, 255,   // 行0 顶场（红）
             0, 0, 255, 255, 0, 0, 255, 255],  // 行1 底场（蓝）
            TimeSpan.Zero);
        var p = new Deinterlacer(DeinterlaceMode.Blend);

        var result = p.Process(input);

        Assert.NotNull(result);
        Assert.True(input.IsDisposed);
        var px = Pix(result);
        // Blend：两场平均 → 品红（B=127,G=0,R=127）
        Assert.Equal(127, px[0]);
        Assert.Equal(0, px[1]);
        Assert.Equal(127, px[2]);
        Assert.Equal(255, px[3]);
        // 两行结果一致（均为两场平均）
        Assert.Equal(px[0], px[4]);
        Assert.Equal(px[2], px[6]);
    }

    [Fact]
    public void FrameRate_DownConvert_DropsHalfFrames()
    {
        var p = new FrameRateConverter(30f); // 目标 30fps
        var dur60 = TimeSpan.FromSeconds(1.0 / 60.0); // 源 60fps

        int nullCount = 0, frameCount = 0;
        for (int i = 0; i < 4; i++)
        {
            // 每轮新建帧（丢帧路径会 Dispose 输入帧；保留路径返回同一实例由 result.Dispose 释放）
            var input = MakeBgra(1, 1, [10, 20, 30, 40], dur60);
            var result = p.Process(input);
            if (result is null) nullCount++;
            else { frameCount++; result.Dispose(); }
        }

        Assert.Equal(2, nullCount);  // 丢帧：60→30 丢弃一半
        Assert.Equal(2, frameCount);
    }

    [Fact]
    public void DisabledProcessor_PassthroughWithoutDispose()
    {
        var input = MakeBgra(2, 2, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], TimeSpan.Zero);
        var scaling = new ScalingProcessor(4, 4) { IsEnabled = false };
        var color = new ColorSpaceConverter(PixelFormat.RGBA32) { IsEnabled = false };
        var deint = new Deinterlacer { IsEnabled = false };
        var fps = new FrameRateConverter(30f) { IsEnabled = false };

        Assert.True(ReferenceEquals(input, scaling.Process(input)));
        Assert.True(ReferenceEquals(input, color.Process(input)));
        Assert.True(ReferenceEquals(input, deint.Process(input)));
        Assert.True(ReferenceEquals(input, fps.Process(input)));
        Assert.False(input.IsDisposed);
    }

    [Fact]
    public void FrameRateConverter_Reset_ClearsHeldState()
    {
        // 升帧率：源 30fps → 目标 60fps，使 _held 被设置
        var p = new FrameRateConverter(60f);
        var dur30 = TimeSpan.FromSeconds(1.0 / 30.0);

        var a = MakeBgra(1, 1, [10, 20, 30, 40], dur30);
        var b = MakeBgra(1, 1, [40, 50, 60, 70], dur30);

        // 首帧（奇数轮）：返回原帧 a（不 Dispose），_held = copy(a)
        var r1 = p.Process(a);
        Assert.True(ReferenceEquals(a, r1));

        // 模拟 Seek：重置处理器
        p.Reset();

        // 次帧 b（重置后 _held 已清空，不应返回陈旧的 a）
        var r2 = p.Process(b);
        Assert.True(ReferenceEquals(b, r2)); // 无陈旧 _held，返回当前帧 b

        r1?.Dispose(); // a 为透传帧，由调用方释放
        r2?.Dispose(); // b 为透传帧，由调用方释放
    }
}

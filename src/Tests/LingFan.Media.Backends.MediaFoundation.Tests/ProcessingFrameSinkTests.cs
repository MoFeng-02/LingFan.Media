using System;
using System.Threading;
using FluentAssertions;
using LingFan.Media.Abstractions;
using LingFan.Media.Consumers;
using NSubstitute;
using Xunit;

namespace LingFan.Media.Backends.MediaFoundation.Tests;

/// <summary>
/// <see cref="ProcessingFrameSink"/> 单元回归（C-9.2 / C-9.3）。
/// 验证双路径分发（GPU 零拷贝 / CPU Span）与生命周期闭环（Attach / Detach / Dispose）。
/// 不依赖真实后端，使用契约层假对象（NSubstitute + 真实帧构造）。
/// </summary>
public sealed class ProcessingFrameSinkTests
{
    [Fact]
    public void Consume_RoutesGpuAndCpuPaths()
    {
        var gpuHit = false;
        var cpuHit = false;
        var anyHit = 0;

        using var sink = new ProcessingFrameSink(
            onFrame: _ => Interlocked.Increment(ref anyHit),
            onGpu: (_, _) => gpuHit = true,
            onCpu: (_, _) => cpuHit = true);

        var gpuFrame = new VideoFrame(
            1920, 1080, PixelFormat.YUV420P,
            Substitute.For<IGpuTextureResource>(),
            TimeSpan.Zero, TimeSpan.Zero, true);
        var cpuFrame = new VideoFrame(
            1920, 1080, PixelFormat.YUV420P,
            new SoftwareFrameResource(1920, 1080, PixelFormat.YUV420P, 100),
            TimeSpan.Zero, TimeSpan.Zero, true);

        sink.Consume(gpuFrame);
        sink.Consume(cpuFrame);

        gpuHit.Should().BeTrue("GPU 纹理帧应走零拷贝路径");
        cpuHit.Should().BeTrue("CPU 帧应走 Span 路径");
        anyHit.Should().Be(2, "统一回调应每帧触发一次");
    }

    [Fact]
    public void Attach_SubscribesToPlayerEvent()
    {
        var player = Substitute.For<IMediaPlayer>();
        using var sink = new ProcessingFrameSink(onFrame: _ => { });

        sink.Attach(player);

        player.Received(1).VideoFrameAvailable += Arg.Any<Action<VideoFrame>>();
    }

    [Fact]
    public void Detach_UnsubscribesFromPlayerEvent()
    {
        var player = Substitute.For<IMediaPlayer>();
        using var sink = new ProcessingFrameSink(onFrame: _ => { });

        sink.Attach(player);
        sink.Detach();

        player.Received(1).VideoFrameAvailable -= Arg.Any<Action<VideoFrame>>();
    }

    [Fact]
    public void Dispose_DetachesOnceAndIsIdempotent()
    {
        var player = Substitute.For<IMediaPlayer>();
        var sink = new ProcessingFrameSink(onFrame: _ => { });

        sink.Attach(player);
        sink.Dispose();
        sink.Dispose(); // 幂等：第二次 Dispose 不应再次 -=

        player.Received(1).VideoFrameAvailable -= Arg.Any<Action<VideoFrame>>();
    }

    [Fact]
    public void Consume_DoesNotDisposeBorrowedFrame()
    {
        // 帧为只读借用：sink 不得 Dispose 外部帧（所有权归管线）。
        var player = Substitute.For<IMediaPlayer>();
        var frame = new VideoFrame(
            64, 64, PixelFormat.YUV420P,
            new SoftwareFrameResource(64, 64, PixelFormat.YUV420P, 16),
            TimeSpan.Zero, TimeSpan.Zero, true);

        using var sink = new ProcessingFrameSink(onFrame: _ => { });
        sink.Attach(player);
        sink.Consume(frame);

        frame.IsDisposed.Should().BeFalse("sink 不应释放外部借用的帧");
    }
}

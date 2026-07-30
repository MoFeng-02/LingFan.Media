using System;
using FluentAssertions;
using LingFan.Media.Abstractions;
using LingFan.Media.Consumers;
using NSubstitute;
using Xunit;

namespace LingFan.Media.Backends.MediaFoundation.Tests;

/// <summary>
/// <see cref="ProcessingAudioSink"/> 单元回归（C-9 音频侧对称件）。
/// 验证回调分发与生命周期闭环（Attach / Detach / Dispose）。
/// 不依赖真实后端，使用契约层假对象（NSubstitute + 真实帧构造）。
/// </summary>
public sealed class ProcessingAudioSinkTests
{
    [Fact]
    public void Consume_InvokesCallbackWithBorrowedFrame()
    {
        AudioFrame? received = null;
        using var sink = new ProcessingAudioSink(onAudio: f => received = f);

        var frame = new AudioFrame(
            ReadOnlyMemory<byte>.Empty, 44100, 2, SampleFormat.S16,
            TimeSpan.Zero, TimeSpan.Zero, 1024);

        sink.Consume(frame);

        received.Should().BeSameAs(frame, "回调应收到传入的只读借用音频帧");
    }

    [Fact]
    public void Attach_SubscribesToAudioDataAvailable()
    {
        var player = Substitute.For<IMediaPlayer>();
        using var sink = new ProcessingAudioSink(onAudio: _ => { });

        sink.Attach(player);

        player.Received(1).AudioDataAvailable += Arg.Any<Action<AudioFrame>>();
    }

    [Fact]
    public void Detach_UnsubscribesFromAudioDataAvailable()
    {
        var player = Substitute.For<IMediaPlayer>();
        using var sink = new ProcessingAudioSink(onAudio: _ => { });

        sink.Attach(player);
        sink.Detach();

        player.Received(1).AudioDataAvailable -= Arg.Any<Action<AudioFrame>>();
    }

    [Fact]
    public void Dispose_DetachesOnceAndIsIdempotent()
    {
        var player = Substitute.For<IMediaPlayer>();
        var sink = new ProcessingAudioSink(onAudio: _ => { });

        sink.Attach(player);
        sink.Dispose();
        sink.Dispose(); // 幂等：第二次 Dispose 不应再次 -=

        player.Received(1).AudioDataAvailable -= Arg.Any<Action<AudioFrame>>();
    }

    [Fact]
    public void Consume_DoesNotDisposeBorrowedFrame()
    {
        // 帧为只读借用：sink 不得 Dispose 外部帧（所有权归管线）。
        var player = Substitute.For<IMediaPlayer>();
        var frame = new AudioFrame(
            ReadOnlyMemory<byte>.Empty, 44100, 2, SampleFormat.S16,
            TimeSpan.Zero, TimeSpan.Zero, 1024);

        using var sink = new ProcessingAudioSink(onAudio: _ => { });
        sink.Attach(player);
        sink.Consume(frame);

        frame.IsDisposed.Should().BeFalse("sink 不应释放外部借用的帧");
    }
}

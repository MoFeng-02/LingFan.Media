using System;
using System.Collections.Generic;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Core;

/// <summary>
/// <see cref="IFrameChannel"/> 的线程安全多播实现。订阅者先快照再派发，允许派发期间安全增删订阅。
/// 单一管线线程投递为主，订阅稳定后快照复用、热路径零分配。
/// </summary>
/// <remarks>
/// 设计约束：管线始终在 <see cref="Emit"/> 调用后于 <c>finally</c> 中
/// <c>ReturnFrame</c> 释放帧——<b>本通道与所有 Sink 均为只读借用，绝不 Dispose</b>。
/// 通道只负责扇出；多播下任一 Sink 在 <see cref="IFrameSink.OnFrame"/> 内 Dispose 会让后续订阅方
/// 读到已释放帧（use-after-free），故 Sink 一律不得 Dispose。
/// </remarks>
internal sealed class FrameChannel : IFrameChannel
{
    private readonly List<IFrameSink> _sinks = new();
    private readonly object _gate = new();

    public IDisposable Subscribe(IFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate) _sinks.Add(sink);
        return new Subscription(this, sink);
    }

    public void Emit(VideoFrame frame)
    {
        IFrameSink[] snapshot;
        lock (_gate) snapshot = _sinks.ToArray();
        foreach (var sink in snapshot)
            sink.OnFrame(frame);
    }

    public void Unsubscribe(IFrameSink sink)
    {
        lock (_gate) _sinks.Remove(sink);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly FrameChannel _owner;
        private readonly IFrameSink _sink;
        private bool _disposed;

        public Subscription(FrameChannel owner, IFrameSink sink)
        {
            _owner = owner;
            _sink = sink;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Unsubscribe(_sink);
        }
    }
}

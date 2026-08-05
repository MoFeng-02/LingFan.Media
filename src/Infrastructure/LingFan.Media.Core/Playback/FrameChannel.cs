using System;
using System.Collections.Generic;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Core;

/// <summary>
/// <see cref="IFrameChannel"/> 的线程安全多播实现。订阅者先快照再派发，允许派发期间安全增删订阅。
/// 单一管线线程投递为主，订阅稳定后快照复用、热路径零分配。
/// </summary>
/// <remarks>
/// 设计约束（来自帧路由宪法）：帧必有且仅有一个所有者消费（归还对象池）。本通道只负责扇出，
/// 所有权由订阅的 Sink 约定——主视频 Sink 消费并释放；只读型消费者须另经独立机制，不在此重复释放。
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

    private void Unsubscribe(IFrameSink sink)
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

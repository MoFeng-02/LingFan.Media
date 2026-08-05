using System;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Core;

/// <summary>
/// 将 <see cref="Action{VideoFrame}"/> 适配为 <see cref="IFrameSink"/>，供公开事件
/// <c>VideoFrameAvailable</c> 复用统一 <see cref="IFrameChannel"/> 通道。
/// 事件 remove 时按委托引用匹配同一实例以便退订。
/// </summary>
internal sealed class DelegateFrameSink : IFrameSink
{
    private readonly Action<VideoFrame> _handler;

    public DelegateFrameSink(Action<VideoFrame> handler) => _handler = handler;

    public void OnFrame(VideoFrame frame) => _handler(frame);

    public override bool Equals(object? obj) => obj is DelegateFrameSink d && d._handler == _handler;

    public override int GetHashCode() => _handler.GetHashCode();
}

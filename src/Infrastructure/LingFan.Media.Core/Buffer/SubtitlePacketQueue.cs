using System.Threading.Channels;

namespace LingFan.Media.Core;

/// <summary>
/// 字幕数据包队列。Demuxer → SubtitleDecoder 之间的缓冲区。
/// </summary>
/// <remarks>
/// <para>字幕帧（SubtitleFrame）仅含文本和时间戳，无 GPU 资源、无需 Dispose。</para>
/// <para>但字幕数据包（MediaPacket）需要 Dispose，因此队列持有包的所有权。</para>
/// <para>线程安全：使用 <see cref="System.Threading.Channels.Channel{T}"/> 实现。</para>
/// </remarks>
public sealed class SubtitlePacketQueue
{
    private readonly Channel<MediaPacket> _channel;

    /// <summary>
    /// 初始化 <see cref="SubtitlePacketQueue"/> 的新实例。
    /// </summary>
    /// <param name="capacity">最大容量（包数），默认 100。</param>
    public SubtitlePacketQueue(int capacity = 100)
    {
        _channel = Channel.CreateBounded<MediaPacket>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>当前队列长度。</summary>
    public int Count => _channel.Reader.Count;

    /// <summary>
    /// 入队。所有权从生产者转移到队列。
    /// </summary>
    public bool TryEnqueue(MediaPacket packet)
    {
        return _channel.Writer.TryWrite(packet);
    }

    /// <summary>
    /// 尝试出队（非阻塞）。所有权转移到消费者。
    /// </summary>
    public bool TryDequeue(out MediaPacket? packet)
    {
        return _channel.Reader.TryRead(out packet);
    }

    /// <summary>
    /// 清空队列并 Dispose 所有包。
    /// </summary>
    public void Clear()
    {
        while (_channel.Reader.TryRead(out var packet))
        {
            packet.Dispose();
        }
    }

    /// <summary>
    /// 标记队列完成（流结束）。
    /// </summary>
    public void Complete()
    {
        _channel.Writer.TryComplete();
    }
}

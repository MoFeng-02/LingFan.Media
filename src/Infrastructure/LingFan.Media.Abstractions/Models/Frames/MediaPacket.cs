namespace LingFan.Media.Abstractions;

/// <summary>
/// 解封装后的压缩数据包。实现 <see cref="IDisposable"/>。
/// </summary>
/// <remarks>
/// <para>内存安全注释：</para>
/// <list type="bullet">
/// <item>FFmpeg 的 av_read_frame 返回的 AVPacket 内部 buffer 由 FFmpeg 管理，下一次读取会复用</item>
/// <item>FFmpegDemuxer 在 ReadPacketAsync 中必须拷贝 data 到独立 buffer</item>
/// <item>MediaPacket 独立拥有数据副本</item>
/// </list>
/// </remarks>
public sealed class MediaPacket : IDisposable
{
    /// <summary>所属轨道索引。</summary>
    public int TrackIndex { get; }

    /// <summary>压缩数据（只读视图，底层 buffer 由 packet 拥有）。</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>时间戳（PTS）。</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>数据包持续时间。</summary>
    public TimeSpan Duration { get; }

    /// <summary>是否关键帧。</summary>
    public bool KeyFrame { get; }

    private bool _disposed;

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 初始化 <see cref="MediaPacket"/> 的新实例。
    /// </summary>
    public MediaPacket(int trackIndex, ReadOnlyMemory<byte> data,
        TimeSpan timestamp, TimeSpan duration, bool keyFrame)
    {
        TrackIndex = trackIndex;
        Data = data;
        Timestamp = timestamp;
        Duration = duration;
        KeyFrame = keyFrame;
    }

    /// <summary>释放底层 buffer。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

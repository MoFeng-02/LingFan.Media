namespace LingFan.Media.Abstractions;

/// <summary>
/// 缓冲管理器接口。
/// </summary>
/// <remarks>
/// <para>管理 Demuxer 到 Decoder 之间的数据包缓冲。</para>
/// <para>FrameBuffer 无 SubtitleFrameQueue——字幕帧仅含文本和时间戳，</para>
/// <para>无 GPU 资源、无需 Dispose、无实时性要求。字幕帧由 SubtitleProcessor 内部缓存管理。</para>
/// </remarks>
public interface IBufferManager
{
    /// <summary>当前已缓存时长。</summary>
    TimeSpan BufferedDuration { get; }

    /// <summary>当前缓冲大小（字节）。</summary>
    long BufferedBytes { get; }

    /// <summary>是否达到可播放阈值。</summary>
    bool IsReady { get; }

    /// <summary>当前缓冲状态。</summary>
    BufferState State { get; }

    /// <summary>目标缓冲时长。</summary>
    TimeSpan TargetDuration { get; set; }

    /// <summary>缓冲进度变更事件。</summary>
    event EventHandler<BufferProgressEventArgs>? BufferProgressChanged;

    /// <summary>开始缓冲（预读取）。支持取消。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>停止缓冲。</summary>
    void Stop();

    /// <summary>清空所有缓冲。</summary>
    void Clear();
}

namespace LingFan.Media.Abstractions;

/// <summary>
/// 解封装器接口。将容器（MP4/MKV/...）拆解为独立的轨道数据包。
/// </summary>
/// <remarks>
/// <para>接口定义在 Abstractions，实际实现（如 FFmpegDemuxer）在 Backends 模块。</para>
/// <para>继承 IMediaComponent，拥有 Dispose() + DisposeAsync() 两条释放路径。</para>
/// <para>线程安全：OpenAsync 后的 Tracks/Metadata 可跨线程读取；ReadPacketAsync/SeekAsync 不可并发调用。</para>
/// </remarks>
public interface IMediaDemuxer : IMediaComponent
{
    /// <summary>打开媒体流，进行格式探测和轨道解析。</summary>
    Task OpenAsync(IMediaStream stream, CancellationToken ct = default);

    /// <summary>解析出的轨道列表。</summary>
    IReadOnlyList<MediaTrack> Tracks { get; }

    /// <summary>容器元数据。</summary>
    MediaMetadata Metadata { get; }

    /// <summary>读取下一个数据包。返回 null 表示流结束。</summary>
    ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default);

    /// <summary>定位到指定位置。返回是否成功。</summary>
    Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default);

    /// <summary>关闭解封装器。</summary>
    void Close();
}

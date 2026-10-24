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

    /// <summary>定位到指定位置。<see langword="true"/> 只表示「定位请求已被接受」，不保证精确落点。</summary>
    /// <remarks>
    /// <para><b>返回值语义</b>：<see langword="true"/> 表示请求被接受、后续读取从新位置继续；
    /// 容器通常只能定位到关键帧/块边界，因此<b>不等于</b>已精确落在 <paramref name="position"/>。
    /// 需要精确落点的一方必须读回实际位置确认，或自行丢弃目标时间之前的帧。</para>
    /// <para><b>失败语义</b>：容器本身不可定位时返回 <see langword="false"/>，不抛异常。</para>
    /// <para><b>状态语义</b>：已释放（<c>Dispose</c> / <c>DisposeAsync</c> 之后）时调用<b>必须</b>抛
    /// <see cref="ObjectDisposedException"/>。至于「已创建但尚未 <see cref="OpenAsync"/>」与
    /// 「<see cref="Close"/> 之后尚未释放」这两种状态，本接口<b>不统一规定</b>：实现可返回
    /// <see langword="false"/> 表示未定位，也可抛 <see cref="InvalidOperationException"/> 表示调用时机错误，
    /// 但必须在实现自身的文档里写明所采用的一种；调用方不得依赖任何一侧的具体行为，
    /// 应先保证 <see cref="OpenAsync"/> 成功、并在释放后不再调用。</para>
    /// </remarks>
    Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default);

    /// <summary>关闭解封装器。</summary>
    void Close();
}

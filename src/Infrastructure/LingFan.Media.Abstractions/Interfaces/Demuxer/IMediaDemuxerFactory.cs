namespace LingFan.Media.Abstractions;

/// <summary>
/// 解封装器工厂接口。
/// </summary>
/// <remarks>
/// <para>Singleton 工厂，无状态。每次 Create() 返回新实例（每次播放新建，不共享）。</para>
/// <para>优先使用 <see cref="CreateAsync"/>（异步格式探测不阻塞线程，支持 CT）。</para>
/// </remarks>
public interface IMediaDemuxerFactory
{
    /// <summary>根据媒体流创建对应的 IMediaDemuxer 实例。</summary>
    IMediaDemuxer Create(IMediaStream stream);

    /// <summary>异步根据媒体流创建对应的 IMediaDemuxer 实例。</summary>
    /// <param name="stream">媒体数据流。</param>
    /// <param name="ct">取消令牌。</param>
    Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default);
}

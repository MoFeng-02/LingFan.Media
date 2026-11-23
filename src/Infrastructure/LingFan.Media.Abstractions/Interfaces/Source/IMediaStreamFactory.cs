namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体流工厂接口。
/// </summary>
/// <remarks>
/// <para>Singleton 工厂。每次 Create() 返回新实例。</para>
/// <para>持有 IHttpClientFactory 引用（用于网络流连接池管理，非纯无状态）。</para>
/// <para>优先使用 <see cref="CreateAsync"/>（网络流场景不阻塞线程，支持 CT）。</para>
/// </remarks>
public interface IMediaStreamFactory
{
    /// <summary>根据媒体源类型创建对应的 IMediaStream 实例。</summary>
    /// <param name="source">媒体源。</param>
    /// <returns>新创建的媒体流实例（网络流须先 <see cref="IMediaStream.ConnectAsync"/> 再同步读取）。</returns>
    IMediaStream Create(IMediaSource source);

    /// <summary>异步根据媒体源类型创建对应的 IMediaStream 实例。</summary>
    /// <param name="source">媒体源。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新创建的媒体流实例。</returns>
    Task<IMediaStream> CreateAsync(IMediaSource source, CancellationToken ct = default);
}

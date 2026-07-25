namespace LingFan.Media.Sources;

/// <summary>
/// <see cref="IMediaStreamFactory"/> 实现。根据 <see cref="IMediaSource.Type"/> 创建对应的 <see cref="IMediaStream"/>。
/// </summary>
/// <remarks>
/// <para>Singleton 工厂。持有 <see cref="IHttpClientFactory"/> 引用用于创建 <see cref="NetworkMediaStream"/>。</para>
/// <para>使用 pattern matching switch（AOT 友好，无反射）。</para>
/// </remarks>
public sealed class MediaStreamFactory : IMediaStreamFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// 初始化 <see cref="MediaStreamFactory"/> 的新实例。
    /// </summary>
    /// <param name="httpClientFactory">HttpClient 工厂（用于网络流的连接池管理）。</param>
    public MediaStreamFactory(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc/>
    public IMediaStream Create(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Type switch
        {
            MediaSourceType.File => new FileMediaStream((FileMediaSource)source),
            MediaSourceType.Network => new NetworkMediaStream((NetworkMediaSource)source, _httpClientFactory),
            MediaSourceType.Stream => new PassThroughMediaStream((StreamMediaSource)source),
            _ => throw new ArgumentException($"不支持的媒体源类型: {source.Type}", nameof(source))
        };
    }

    /// <inheritdoc/>
    public Task<IMediaStream> CreateAsync(IMediaSource source, CancellationToken ct = default)
    {
        // V1: Create 本身仅 new，网络连接延迟到首次 ReadAsync。
        // 未来可在此处异步建立网络连接（DNS/TCP），V1 保持与同步一致。
        return Task.FromResult(Create(source));
    }
}

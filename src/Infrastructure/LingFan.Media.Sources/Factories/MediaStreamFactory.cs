using System.Net;
using LingFan.Media.Sources.Security;

namespace LingFan.Media.Sources;

/// <summary>
/// <see cref="IMediaStreamFactory"/> 实现。根据 <see cref="IMediaSource.Type"/> 创建对应的 <see cref="IMediaStream"/>。
/// </summary>
/// <remarks>
/// <para>Singleton 工厂。持有 <see cref="IHttpClientFactory"/> 引用用于创建 <see cref="NetworkMediaStream"/>。</para>
/// <para>使用 pattern matching switch（AOT 友好，无反射）。</para>
/// <para><see cref="CreateAsync"/> 为网络流场景的优先入口：在 DNS 解析后做 SSRF 校验（L20-L22），
/// 属真实 I/O（<c>Dns.GetHostAddressesAsync</c>），必须 <c>await</c>，绝非伪异步；同步 <see cref="Create"/> 保留为原生同步边界（连接延迟到首次 Read）。</para>
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
        return CreateCore(source);
    }

    /// <inheritdoc/>
    public async Task<IMediaStream> CreateAsync(IMediaSource source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);

        // 网络流：真实异步建立网络连接（DNS 解析），并在解析后做 SSRF 校验（L20-L22）。
        // DNS 重绑定防护要求在校验通过后再建立连接，故校验点必须位于 DNS 解析之后。
        if (source.Type == MediaSourceType.Network && source is NetworkMediaSource netSource)
        {
            var uri = new Uri(netSource.Url);
            var ips = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
            SsrfGuard.Validate(uri.Host, ips, netSource.AllowPrivateAddresses);

            // 将已校验的 IP 透传给流，避免建连时二次解析（建连点仍按 URL 重新解析，属已知 DNS 重绑定窗口）。
            return CreateCore(netSource, ips);
        }

        // 非网络路径：纯内存，无 I/O。async 方法同步完成（不引入伪异步状态机）。
        return CreateCore(source);
    }

    /// <summary>
    /// 根据媒体源类型创建对应的 <see cref="IMediaStream"/> 实例（核心实现）。
    /// </summary>
    /// <param name="source">媒体源。</param>
    /// <param name="preResolvedIps">网络流已由 <see cref="CreateAsync"/> 解析并校验的 IP（可选，避免建连时二次 DNS）。</param>
    private IMediaStream CreateCore(IMediaSource source, IPAddress[]? preResolvedIps = null)
    {
        return source.Type switch
        {
            MediaSourceType.File => new FileMediaStream((FileMediaSource)source),
            MediaSourceType.Network => new NetworkMediaStream((NetworkMediaSource)source, _httpClientFactory, preResolvedIps),
            MediaSourceType.Stream => new PassThroughMediaStream((StreamMediaSource)source),
            _ => throw new ArgumentException($"不支持的媒体源类型: {source.Type}", nameof(source))
        };
    }
}

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using LingFan.Media.Sources.Security;

namespace LingFan.Media.Sources;

/// <summary>
/// 网络流包装实现 <see cref="IMediaStream"/>。
/// </summary>
/// <remarks>
/// <para>非线程安全（IMediaStream 契约：ReadAsync/Seek 不可并发调用）。</para>
/// <para>使用 HttpClient 以 ResponseHeadersRead 模式下载，支持 CancellationToken。</para>
/// <para>HTTP 连接在 <see cref="ConnectAsync"/>（由 Demuxer.OpenAsync 等异步路径前置调用）时惰性建立，
/// 之后同步 <see cref="Read"/> 仅做已连接流的逐块读取。Close 后所有操作抛 ObjectDisposedException。</para>
/// <para>
/// 套接字耗尽防护：常见场景（无 SSL 绕过）使用 <see cref="IHttpClientFactory"/> 获取池化 HttpClient，
/// 由工厂管理 HttpMessageHandler 生命周期（定期回收连接池），避免频繁创建/释放 HttpClient 导致
/// TIME_WAIT 堆积。Close 时不 Dispose 工厂管理的 HttpClient（仅 Dispose response/stream）。
/// 仅当 source 需要 SSL 证书绕过时才创建专用 HttpClient（设 PooledConnectionLifetime 缓解耗尽）。
/// </para>
/// <para>
/// Cookie 处理：通过 Cookie 请求头传递（而非 CookieContainer），兼容工厂管理的共享 handler。
/// </para>
/// </remarks>
public sealed class NetworkMediaStream : IMediaStream
{
    private readonly NetworkMediaSource _source;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    /// <summary>从 URL 解析出的主机名（用于 DNS 解析与 SSRF 校验）。</summary>
    private readonly string _host;

    /// <summary>由 <see cref="MediaStreamFactory.CreateAsync"/> 预解析并校验的 IP（避免建连时二次 DNS）。</summary>
    private readonly IPAddress[]? _resolvedIps;

    private HttpResponseMessage? _response;
    private Stream? _responseStream;
    private long _position;
    private long _length = -1;
    private bool _closed;

    /// <inheritdoc/>
    public long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _length;
        }
    }

    /// <inheritdoc/>
    public long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _position;
        }
    }

    /// <inheritdoc/>
    public bool CanSeek
    {
        get
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return false;
        }
    }

    /// <inheritdoc/>
    public string Location => _source.Url;

    /// <summary>
    /// 初始化 <see cref="NetworkMediaStream"/> 的新实例。
    /// </summary>
    /// <param name="source">网络媒体源。</param>
    /// <param name="httpClientFactory">HttpClient 工厂（用于池化连接，防止套接字耗尽）。</param>
    public NetworkMediaStream(NetworkMediaSource source, IHttpClientFactory httpClientFactory, IPAddress[]? resolvedIps = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _source = source;
        _host = new Uri(source.Url).Host;
        _resolvedIps = resolvedIps;

        if (source.AllowInsecureHttps)
        {
            // SSL 证书绕过：统一经 IHttpClientFactory 命名 client（"LingFanMedia_Insecure"），
            // 其自定义 SocketsHttpHandler（RemoteCertificateValidationCallback = true）在 AddLingFanMedia 中注册。
            // 工厂管理 HttpMessageHandler 生命周期，避免套接字耗尽；Close 时不 Dispose（_ownsClient = false）。
            _httpClient = httpClientFactory.CreateClient("LingFanMedia_Insecure");
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
            _ownsClient = false;
        }
        else
        {
            // 常见场景：使用 IHttpClientFactory 获取池化命名 HttpClient（"LingFanMedia"）。
            // B-DNS: 命名 client 的 SocketsHttpHandler 挂载 SsrfConnectGuard.ConnectCallback
            //（DNS pinning，闭合「校验后重解析」TOCTOU 缺口），在 AddLingFanMedia 中注册。
            // 工厂管理 HttpMessageHandler 生命周期，避免套接字耗尽。Close 时不 Dispose（由工厂管理）。
            _httpClient = httpClientFactory.CreateClient("LingFanMedia");
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
            _ownsClient = false;
        }
    }


    /// <inheritdoc/>
    public int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        if (buffer.Length == 0)
            return 0;

        // 同步边界（FFmpeg AVIO 回调）无法 await。建连必须前置到异步路径（ConnectAsync）。
        // 此处不再硬阻塞（无 .GetAwaiter().GetResult()），未预建连即视为调用契约违例。
        if (_responseStream is null)
            throw new InvalidOperationException(
                "网络流必须先调用 ConnectAsync 建立连接，再经同步 Read 读取（FFmpeg AVIO 原生回调为同步边界，无法 await）。");

        int read = _responseStream.Read(buffer);
        _position += read;
        return read;
    }

    /// <inheritdoc/>
    public Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return EnsureConnectedAsync(ct);
    }

    /// <inheritdoc/>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        // 空 buffer 直接返回 0（接口契约）
        if (buffer.Length == 0)
            return 0;

        // 首次读取时建立 HTTP 连接
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        // 从网络流读取，必须支持 CancellationToken
        var read = await _responseStream!.ReadAsync(buffer, ct).ConfigureAwait(false);
        _position += read;
        return read;
    }

    /// <inheritdoc/>
    public long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        throw new NotSupportedException("网络流不支持 Seek。");
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (_closed)
            return;

        _closed = true;

        _responseStream?.Dispose();
        _response?.Dispose();

        // 仅 Dispose 自建的 HttpClient（SSL 绕过场景）。
        // 工厂管理的 HttpClient 不 Dispose — 由 IHttpClientFactory 管理 handler 生命周期。
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// 惰性建立 HTTP 连接（由 <see cref="ConnectAsync"/> 在异步路径中调用，先于同步 Read）。
    /// </summary>
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_responseStream is not null)
            return;

        // SSRF 防护（L20-L22）：DNS 解析后对真实目标 IP 做私有/回环/保留/CGNAT 校验。
        // 此路径由 ConnectAsync（Demuxer.OpenAsync 异步前置）触发，属真实异步 I/O，可安全 await。
        // _resolvedIps 已由 CreateAsync 预解析校验则复用（避免二次 DNS），否则在此解析。
        var ips = _resolvedIps ?? await Dns.GetHostAddressesAsync(_host, ct).ConfigureAwait(false);
        SsrfGuard.Validate(_host, ips, _source.AllowPrivateAddresses);

        using var request = new HttpRequestMessage(HttpMethod.Get, _source.Url);

        // B-DNS: DNS pinning——把已校验 IP 经请求选项传给 SsrfConnectGuard.ConnectCallback，
        // 强制套接字只连校验过的 IP，闭合「Validate 后 SendAsync 二次解析」的重绑定窗口。
        request.Options.Set(SsrfConnectGuard.ValidatedIpsKey, ips);
        request.Options.Set(SsrfConnectGuard.ValidatedHostKey, _host);
        request.Options.Set(SsrfConnectGuard.AllowPrivateAddressesKey, _source.AllowPrivateAddresses);

        // 自定义 HTTP 头
        foreach (var header in _source.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Cookie 通过请求头传递（兼容工厂管理的共享 handler，无需 CookieContainer）
        if (_source.Cookies is { Count: > 0 })
        {
            var cookieHeader = string.Join("; ", _source.Cookies.Select(c => $"{c.Name}={c.Value}"));
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        // 使用连接超时（不影响后续流式读取）
        using var timeoutCts = new CancellationTokenSource(_source.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        _response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
            .ConfigureAwait(false);

        try
        {
            _response.EnsureSuccessStatusCode();

            _responseStream = await _response.Content
                .ReadAsStreamAsync(ct)
                .ConfigureAwait(false);

            // 从 Content-Length 头获取长度（可能为 -1 表示未知）
            if (_response.Content.Headers.ContentLength is { } contentLength)
            {
                _length = contentLength;
            }
        }
        catch
        {
            // EnsureSuccessStatusCode 或 ReadAsStreamAsync 失败时释放 response 防止泄漏
            _response.Dispose();
            _response = null;
            throw;
        }
    }
}

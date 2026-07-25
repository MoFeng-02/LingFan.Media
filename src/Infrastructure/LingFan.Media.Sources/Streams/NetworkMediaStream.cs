using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;

namespace LingFan.Media.Sources;

/// <summary>
/// 网络流包装实现 <see cref="IMediaStream"/>。
/// </summary>
/// <remarks>
/// <para>非线程安全（IMediaStream 契约：ReadAsync/Seek 不可并发调用）。</para>
/// <para>使用 HttpClient 以 ResponseHeadersRead 模式下载，支持 CancellationToken。</para>
/// <para>HTTP 连接在首次 ReadAsync 时惰性建立，Close 后所有操作抛 ObjectDisposedException。</para>
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

    /// <summary>
    /// 初始化 <see cref="NetworkMediaStream"/> 的新实例。
    /// </summary>
    /// <param name="source">网络媒体源。</param>
    /// <param name="httpClientFactory">HttpClient 工厂（用于池化连接，防止套接字耗尽）。</param>
    public NetworkMediaStream(NetworkMediaSource source, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _source = source;

        if (source.AllowInsecureHttps)
        {
            // SSL 证书绕过需要自定义 handler — IHttpClientFactory 的池化 handler 不支持每请求 SSL 配置。
            // 创建专用 HttpClient + SocketsHttpHandler（设 PooledConnectionLifetime 缓解套接字耗尽）。
            // 这是罕见场景（默认 HTTPS 验证开启），每 Session 一个专用 client 可接受。
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                },
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            };

            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _ownsClient = true;
        }
        else
        {
            // 常见场景：使用 IHttpClientFactory 获取池化 HttpClient。
            // 工厂管理 HttpMessageHandler 生命周期，避免套接字耗尽。
            // Close 时不 Dispose（由工厂管理）。
            _httpClient = httpClientFactory.CreateClient();
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

        // 首次读取时同步建立 HTTP 连接。
        // EnsureConnectedAsync 返回 Task（非 ValueTask），
        // Task.GetAwaiter().GetResult() 保证阻塞到完成。
        if (_responseStream is null)
            EnsureConnectedAsync(CancellationToken.None).GetAwaiter().GetResult();

        int read = _responseStream!.Read(buffer);
        _position += read;
        return read;
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
    /// 惰性建立 HTTP 连接（首次 Read/ReadAsync 时调用）。
    /// </summary>
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_responseStream is not null)
            return;

        using var request = new HttpRequestMessage(HttpMethod.Get, _source.Url);

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

using System.IO;
using System.Net;
using LingFan.Media.Sources.Security;

namespace LingFan.Media.Sources;

/// <summary>
/// 网络流包装实现 <see cref="IMediaStream"/>。
/// </summary>
/// <remarks>
/// <para>非线程安全（IMediaStream 契约：ReadAsync/Seek 不可并发调用）。</para>
/// <para>使用 HttpClient 以 ResponseHeadersRead 模式下载，支持 CancellationToken。</para>
/// <para>定位：服务器宣告 <c>Accept-Ranges: bytes</c> 时，<see cref="Seek"/> 经 HTTP Range
/// 重开连接实现真实定位（206 响应即新读取起点；服务器忽略 Range 时保留原连接并报错）。</para>
/// <para>读超时：数据阶段单次读取超过 <see cref="NetworkMediaSource.ReadTimeout"/> 无任何数据，
/// 即强制中断连接并抛 <see cref="TimeoutException"/>（同步 Read 经看门狗强制关闭连接解除阻塞，
/// 异步 ReadAsync 经 CancellationToken 实现）。中断后流为终态，须重建播放会话。</para>
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

    /// <summary>0 = 连接正常；1 = 连接已因读超时被看门狗强制中断（中断后流不可继续使用，须重建播放会话）。</summary>
    private int _broken;

    /// <summary>服务器已宣告支持字节范围请求（响应含 Accept-Ranges: bytes），连接建立后为真。</summary>
    private volatile bool _canSeek;

    /// <summary>建连路径已解析并校验的目标 IP（Range 重开连接时复用，避免二次 DNS 与重复校验）。</summary>
    private IPAddress[]? _validatedIps;

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
    /// <remarks>
    /// 取决于服务器是否宣告 <c>Accept-Ranges: bytes</c>（建连后判定）；连接建立前恒为 false。
    /// 支持时 <see cref="Seek"/> 经 HTTP Range 重开连接实现真实定位。
    /// </remarks>
    public bool CanSeek
    {
        get
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _canSeek;
        }
    }

    /// <inheritdoc/>
    public string Location => _source.Url;

    /// <summary>默认 User-Agent（随程序集版本）；用户经 Headers 显式提供 User-Agent 时以用户值为准。</summary>
    internal static string DefaultUserAgent { get; } =
        $"LingFanMedia/{typeof(NetworkMediaStream).Assembly.GetName().Version?.ToString(3) ?? "1.0"}";

    /// <inheritdoc/>
    /// <remarks>
    /// 合并规则：用户自定义头优先；默认 User-Agent 与 Cookie 仅在用户未提供同名头时补充。
    /// 供按地址直开的后端透传（该头集合与本体请求使用的头一致）。
    /// </remarks>
    public IReadOnlyDictionary<string, string>? SuggestedOpenHeaders => BuildOpenHeaders();

    /// <summary>构造建议打开头集合（纯内存，可重复调用）。</summary>
    private IReadOnlyDictionary<string, string> BuildOpenHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in _source.Headers)
        {
            headers[header.Key] = header.Value;
        }

        if (!headers.ContainsKey("User-Agent"))
        {
            headers["User-Agent"] = DefaultUserAgent;
        }

        if (_source.Cookies is { Count: > 0 } && !headers.ContainsKey("Cookie"))
        {
            headers["Cookie"] = string.Join("; ", _source.Cookies.Select(c => $"{c.Name}={c.Value}"));
        }

        return headers;
    }

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
            // 命名 client 的 SocketsHttpHandler 挂载 SsrfConnectGuard.ConnectCallback
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
        ThrowIfBroken();

        if (buffer.Length == 0)
            return 0;

        // 同步边界（FFmpeg AVIO 回调）无法 await。建连必须前置到异步路径（ConnectAsync）。
        // 此处不再硬阻塞（无 .GetAwaiter().GetResult()），未预建连即视为调用契约违例。
        if (_responseStream is null)
            throw new InvalidOperationException(
                "网络流必须先调用 ConnectAsync 建立连接，再经同步 Read 读取（FFmpeg AVIO 原生回调为同步边界，无法 await）。");

        var readTimeout = _source.ReadTimeout;
        if (readTimeout == Timeout.InfiniteTimeSpan)
        {
            int read = _responseStream.Read(buffer);
            _position += read;
            return read;
        }

        // 读超时看门狗：同步边界无 CancellationToken 可用，超时即强制关闭底层连接，
        // 使阻塞中的同步读立即以异常解除阻塞（连接关闭会中止 socket 接收等待）。
        using var watchdog = new CancellationTokenSource(readTimeout);
        using (watchdog.Token.Register(static state => ((NetworkMediaStream)state!).BreakConnection(), this))
        {
            try
            {
                int read = _responseStream.Read(buffer);
                _position += read;
                return read;
            }
            catch (Exception ex) when (Volatile.Read(ref _broken) != 0)
            {
                throw new TimeoutException(
                    $"网络读取停滞超过 {readTimeout.TotalSeconds:0.#} 秒，连接已被读超时看门狗中断。", ex);
            }
        }
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
        ThrowIfBroken();

        // 空 buffer 直接返回 0（接口契约）
        if (buffer.Length == 0)
            return 0;

        // 首次读取时建立 HTTP 连接（建连时长由 Timeout 管，不占读超时预算）
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var readTimeout = _source.ReadTimeout;
        if (readTimeout == Timeout.InfiniteTimeSpan)
        {
            var readUnbounded = await _responseStream!.ReadAsync(buffer, ct).ConfigureAwait(false);
            _position += readUnbounded;
            return readUnbounded;
        }

        // 读超时看门狗：数据阶段单次读取超过 ReadTimeout 无任何数据即判定连接停滞。
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(readTimeout);
        try
        {
            var read = await _responseStream!.ReadAsync(buffer, watchdog.Token).ConfigureAwait(false);
            _position += read;
            return read;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 外部 CT 未取消而看门狗到期 → 读停滞，中断连接并转为明确的超时异常
            BreakConnection();
            throw new TimeoutException(
                $"网络读取停滞超过 {readTimeout.TotalSeconds:0.#} 秒，连接已被读超时看门狗中断。");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>经 HTTP Range 重开连接实现定位（对服务器发 <c>GET Range: bytes=offset-</c>，206 响应即新读取起点），
    /// 与主流播放器的 HTTP Seek 语义一致；服务器忽略 Range 返回 200 时保留原连接并抛错（位置绝不错位）。</para>
    /// <para>同步边界：FFmpeg AVIO 定位回调为 C 函数指针签名，使用 BCL 同步 Send API 完成重连
    /// （原生同步边界桥接，非异步调用硬阻塞）。仅当 <see cref="CanSeek"/> 为 true 时可达。</para>
    /// </remarks>
    public long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        ThrowIfBroken();

        if (_responseStream is null || !_canSeek)
            throw new NotSupportedException("网络流不可定位（服务器未宣告 Accept-Ranges: bytes 或尚未建立连接）。");

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length < 0
                ? throw new NotSupportedException("流长度未知（无 Content-Length），无法自尾部定位。")
                : _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null)
        };

        if (target < 0 || (_length >= 0 && target > _length))
            throw new IOException($"定位偏移 {target} 超出流范围（0..{_length}）。");

        if (target == _position)
            return _position;

        SwitchConnectionTo(target);
        return _position;
    }

    /// <summary>
    /// 以 Range 请求重开连接并把读取起点切换到指定字节偏移（同步，仅供 <see cref="Seek"/>）。
    /// </summary>
    /// <remarks>
    /// 失败时保留原连接与读取状态（重开成功后旧连接才释放）；新请求复用已校验 IP（DNS pinning）、
    /// 自定义头与 Cookie；连接/响应头阶段受 <see cref="NetworkMediaSource.Timeout"/> 约束。
    /// </remarks>
    private void SwitchConnectionTo(long target)
    {
        HttpResponseMessage newResponse;
        using (var timeoutCts = new CancellationTokenSource(_source.Timeout))
        {
            using var request = BuildGetRequest(target);
            try
            {
                newResponse = _httpClient.Send(request, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Seek 重开连接超时（{_source.Timeout.TotalSeconds:0.#} 秒）。");
            }
            catch (Exception ex)
            {
                throw new IOException($"Seek 重开连接失败（目标偏移 {target}）：{ex.Message}", ex);
            }
        }

        try
        {
            newResponse.EnsureSuccessStatusCode();

            // 服务器宣称支持 Range 却对非零起点返回 200（完整正文）——绝不能把 0 号字节当 target 号字节读。
            if ((int)newResponse.StatusCode == 200 && target > 0)
                throw new IOException("服务器未按 Range 请求返回 206，定位被拒绝（保留原连接）。");

            // 首连未拿到 Content-Length 时，经 Content-Range 的 total 补全（bytes X-Y/Z 之 Z）。
            // 解析须在换流前完成：畸形头异常走本 catch 释放新响应、保留原连接，不误杀已接管的新流。
            long? totalLength = null;
            if (_length < 0 && newResponse.Content.Headers.ContentRange is { HasLength: true } range)
            {
                totalLength = range.Length ?? -1;
            }

            var newStream = newResponse.Content.ReadAsStream();

            // 新流就绪后才释放旧连接（失败路径不破坏原读取状态）
            var oldStream = _responseStream;
            var oldResponse = _response;
            _response = newResponse;
            _responseStream = newStream;
            try { oldStream?.Dispose(); } catch { /* 旧连接释放失败不影响新流 */ }
            try { oldResponse?.Dispose(); } catch { /* 同上 */ }

            _position = target;
            if (totalLength is { } total)
            {
                _length = total;
            }
        }
        catch
        {
            newResponse.Dispose();
            throw;
        }
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
    /// 强制中断底层连接（读超时看门狗回调）。阻塞中的同步/异步读会立即以异常解除阻塞。
    /// </summary>
    /// <remarks>
    /// 可从看门狗定时器线程与读取线程并发进入，经 <see cref="Interlocked"/> 保证只执行一次释放。
    /// 中断后流进入 broken 终态：连接无法原地恢复（新连接从流头部开始，与已消费字节错位），
    /// 后续读写抛 <see cref="IOException"/>，须重建播放会话恢复。
    /// </remarks>
    private void BreakConnection()
    {
        if (Interlocked.Exchange(ref _broken, 1) != 0)
            return;

        try { _responseStream?.Dispose(); } catch { /* 看门狗路径：释放失败不掩盖超时成因 */ }
        try { _response?.Dispose(); } catch { /* 同上 */ }
    }

    /// <summary>broken 终态守卫：连接已因读超时中断后拒绝后续读写。</summary>
    private void ThrowIfBroken()
    {
        if (Volatile.Read(ref _broken) != 0)
            throw new IOException("网络连接已因读超时中断，流不可继续读取；请重建播放会话。");
    }

    /// <summary>
    /// 惰性建立 HTTP 连接（由 <see cref="ConnectAsync"/> 在异步路径中调用，先于同步 Read）。
    /// </summary>
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_responseStream is not null)
            return;

        // SSRF 防护：DNS 解析后对真实目标 IP 做私有/回环/保留/CGNAT 校验。
        // 此路径由 ConnectAsync（Demuxer.OpenAsync 异步前置）触发，属真实异步 I/O，可安全 await。
        // _resolvedIps 已由 CreateAsync 预解析校验则复用（避免二次 DNS），否则在此解析。
        var ips = _resolvedIps ?? await Dns.GetHostAddressesAsync(_host, ct).ConfigureAwait(false);
        SsrfGuard.Validate(_host, ips, _source.AllowPrivateAddresses);
        _validatedIps = ips;

        // 使用连接超时（不影响后续流式读取）
        using var timeoutCts = new CancellationTokenSource(_source.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var request = BuildGetRequest(null);
        var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
            .ConfigureAwait(false);

        try
        {
            response.EnsureSuccessStatusCode();

            _responseStream = await response.Content
                .ReadAsStreamAsync(ct)
                .ConfigureAwait(false);
            _response = response;

            // 可定位能力：服务器宣告 Accept-Ranges: bytes（206 响应本身亦证明支持）。
            // 压缩传输例外：Range/Content-Length 均按压缩字节计，而自动解压后的流按明文字节读，
            // 两者错位——压缩响应一律视为不可定位、长度未知（保守正确）。
            var contentEncoding = response.Content.Headers.ContentEncoding;
            var compressed = contentEncoding.Count > 0
                             && contentEncoding.Any(e => !string.Equals(e, "identity", StringComparison.OrdinalIgnoreCase));
            _canSeek = !compressed &&
                       (response.Headers.AcceptRanges.Contains("bytes")
                        || response.StatusCode == HttpStatusCode.PartialContent);

            // 从 Content-Length 头获取长度（可能为 -1 表示未知；压缩传输时为压缩长度，按未知处理）
            if (!compressed && response.Content.Headers.ContentLength is { } contentLength)
            {
                _length = contentLength;
            }
        }
        catch
        {
            // EnsureSuccessStatusCode 或 ReadAsStreamAsync 失败时释放 response 防止泄漏
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 构造 GET 请求：建议打开头（默认 UA/用户自定义头/Cookie，与 <see cref="SuggestedOpenHeaders"/> 同源）、
    /// SSRF DNS pinning（已校验 IP 经请求选项传给 SsrfConnectGuard.ConnectCallback，强制套接字只连校验过的 IP）；
    /// rangeStart 非空时附 <c>Range: bytes=start-</c>（定位重开连接）。
    /// </summary>
    private HttpRequestMessage BuildGetRequest(long? rangeStart)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _source.Url);

        if (_validatedIps is { } validated)
        {
            request.Options.Set(SsrfConnectGuard.ValidatedIpsKey, validated);
            request.Options.Set(SsrfConnectGuard.ValidatedHostKey, _host);
            request.Options.Set(SsrfConnectGuard.AllowPrivateAddressesKey, _source.AllowPrivateAddresses);
        }

        // 建议打开头（含默认 UA 补全；用户同名头优先）
        foreach (var header in BuildOpenHeaders())
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (rangeStart is { } start)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, null);
        }

        return request;
    }
}

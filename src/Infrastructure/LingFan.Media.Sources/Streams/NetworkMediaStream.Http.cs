using System.IO;
using System.Net;
using LingFan.Media.Sources.Security;

namespace LingFan.Media.Sources;

/// <summary>
/// NetworkMediaStream 的 HTTP 连接段（部分类，由 NetworkMediaStream.cs 按职责拆分，编译期等价）。
/// 惰性建连（SSRF 校验/DNS pinning）、Range 重开连接与 GET 请求构造。
/// </summary>
public sealed partial class NetworkMediaStream
{
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

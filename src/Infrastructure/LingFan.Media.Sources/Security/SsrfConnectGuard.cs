using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace LingFan.Media.Sources.Security;

/// <summary>
/// B-DNS: SSRF 建连级防护——<see cref="SocketsHttpHandler.ConnectCallback"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>闭合的 TOCTOU 缺口</b>：<see cref="NetworkMediaStream"/> 在发送请求前用
/// <see cref="SsrfGuard.Validate"/> 校验 DNS 解析结果，但默认 handler 的 <c>SendAsync</c>
/// 会按 URL <b>重新解析 DNS</b>——攻击者可在「校验后、建连前」窗口把域名重绑定到内网 IP 绕过校验。
/// 本回调强制套接字只连接「已校验的固定 IP」（DNS pinning），消除该窗口。</para>
/// <para><b>重定向防护</b>：<c>AllowAutoRedirect</c> 开启时重定向目标主机与固定主机不同，
/// 此时现场重新解析并经 <see cref="SsrfGuard.Validate"/> 校验后再连接——重定向到内网同样被拦截。</para>
/// <para><b>传参通道</b>：经 <see cref="HttpRequestMessage.Options"/> 携带（BCL 中立机制，
/// 兼容 IHttpClientFactory 共享 handler——每请求独立，无状态污染）。</para>
/// <para><b>异步纪律</b>：全程 async/await 真异步（DNS/套接字均为真实 I/O），暴露 CancellationToken，
/// 无同步阻塞、无伪异步。AOT 兼容：静态方法 + 无反射。</para>
/// </remarks>
public static class SsrfConnectGuard
{
    /// <summary>请求级选项：已由 <see cref="SsrfGuard.Validate"/> 校验的固定 IP（DNS pinning）。</summary>
    public static readonly HttpRequestOptionsKey<IPAddress[]> ValidatedIpsKey = new("LingFan.Media.ValidatedIps");

    /// <summary>请求级选项：固定 IP 对应的主机名（重定向后主机变化时判定 pinning 是否仍适用）。</summary>
    public static readonly HttpRequestOptionsKey<string> ValidatedHostKey = new("LingFan.Media.ValidatedHost");

    /// <summary>请求级选项：是否允许私有/保留地址（透传给现场校验路径）。</summary>
    public static readonly HttpRequestOptionsKey<bool> AllowPrivateAddressesKey = new("LingFan.Media.AllowPrivateAddresses");

    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectCallback"/> 实现：只连接已校验 IP；
    /// 无 pinning 信息或主机不匹配（重定向）时现场解析 + SSRF 校验后连接。
    /// </summary>
    /// <param name="context">建连上下文（目标端点 + 发起请求）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已连接的网络流。</returns>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        string host = context.DnsEndPoint.Host;
        int port = context.DnsEndPoint.Port;

        bool allowPrivate = false;
        IPAddress[]? ips = null;

        if (context.InitialRequestMessage is { } request)
        {
            request.Options.TryGetValue(AllowPrivateAddressesKey, out allowPrivate);

            // DNS pinning：仅当目标主机与校验时主机一致才使用固定 IP（重定向后主机不同则重新校验）
            if (request.Options.TryGetValue(ValidatedIpsKey, out var pinnedIps) &&
                request.Options.TryGetValue(ValidatedHostKey, out var pinnedHost) &&
                string.Equals(pinnedHost, host, StringComparison.OrdinalIgnoreCase))
            {
                ips = pinnedIps;
            }
        }

        if (ips is null)
        {
            // 现场解析路径（重定向目标 / 未携带 pinning 的调用方）：解析后必须过 SSRF 校验
            ips = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            SsrfGuard.Validate(host, ips, allowPrivate);
        }

        // 双栈套接字（未指定地址族 → IPv6 双模式，v4 经映射地址兼容）
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(ips, port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

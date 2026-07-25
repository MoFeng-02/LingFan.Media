using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;

namespace LingFan.Media.Sources;

/// <summary>
/// 网络流媒体源。
/// </summary>
/// <remarks>
/// 不可变对象，线程安全。支持自定义 HTTP 头、Cookie、超时、HTTPS 证书绕过。
/// 内置 SSRF 防护（拒绝 file:// 协议，默认拒绝内网 IP）。
/// 同时实现 <see cref="IMediaSource"/> 和 <see cref="IMediaSourceMetadata"/>。
/// </remarks>
public sealed class NetworkMediaSource : IMediaSource, IMediaSourceMetadata
{
    /// <summary>完整 URL。</summary>
    public string Url { get; }

    /// <summary>自定义 HTTP 头。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>网络超时（默认 30 秒）。</summary>
    public TimeSpan Timeout { get; }

    /// <summary>是否允许不安全 HTTPS（默认 false）。</summary>
    public bool AllowInsecureHttps { get; }

    /// <summary>是否允许访问内网 IP 地址（默认 false，SSRF 防护）。</summary>
    public bool AllowPrivateAddresses { get; }

    /// <summary>Cookie 列表。</summary>
    public IReadOnlyList<MediaCookie>? Cookies { get; }

    /// <inheritdoc/>
    public MediaSourceType Type => MediaSourceType.Network;

    /// <inheritdoc/>
    public string Identifier => Url;

    /// <inheritdoc/>
    public string? Name { get; }

    /// <inheritdoc/>
    public string? ContentType { get; }

    /// <inheritdoc/>
    public bool IsLive { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> ExtraFields { get; }

    /// <summary>
    /// 初始化 <see cref="NetworkMediaSource"/> 的新实例。
    /// </summary>
    /// <param name="url">完整 URL（仅支持 http/https）。</param>
    /// <param name="headers">自定义 HTTP 头。</param>
    /// <param name="timeout">网络超时（默认 30 秒）。</param>
    /// <param name="allowInsecureHttps">是否允许不安全 HTTPS。</param>
    /// <param name="cookies">Cookie 列表。</param>
    /// <param name="name">显示名称。</param>
    /// <param name="contentType">MIME 类型。</param>
    /// <param name="isLive">是否直播流。</param>
    /// <param name="extraFields">额外元数据。</param>
    /// <param name="allowPrivateAddresses">是否允许访问内网 IP（SSRF 防护开关）。</param>
    public NetworkMediaSource(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null,
        bool allowInsecureHttps = false,
        IReadOnlyList<MediaCookie>? cookies = null,
        string? name = null,
        string? contentType = null,
        bool isLive = false,
        IReadOnlyDictionary<string, string>? extraFields = null,
        bool allowPrivateAddresses = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL 不能为空。", nameof(url));

        // URL 解析与安全校验
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("URL 格式无效。", nameof(url));

        // 拒绝 file:// 协议（防止网络源访问本地文件系统）
        if (uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("网络媒体源不支持 file:// 协议，请使用 FileMediaSource。", nameof(url));

        // 仅允许 http/https
        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"不支持的网络协议: {uri.Scheme}，仅支持 http/https。", nameof(url));

        // SSRF 防护：检查内网 IP
        if (!allowPrivateAddresses && IsPrivateOrLoopbackAddress(uri.Host))
            throw new ArgumentException(
                $"目标地址 {uri.Host} 是内网/回环地址，已拒绝。如需访问内网，请设置 allowPrivateAddresses: true。",
                nameof(url));

        Url = url;
        Headers = headers ?? FrozenDictionary<string, string>.Empty;
        Timeout = timeout ?? TimeSpan.FromSeconds(30);
        AllowInsecureHttps = allowInsecureHttps;
        AllowPrivateAddresses = allowPrivateAddresses;
        Cookies = cookies;
        Name = name;
        ContentType = contentType;
        IsLive = isLive;
        ExtraFields = extraFields ?? FrozenDictionary<string, string>.Empty;
    }

    /// <summary>
    /// 检查目标主机是否为内网/回环地址。
    /// </summary>
    /// <param name="host">主机名或 IP 地址字符串。</param>
    /// <returns>是内网/回环地址返回 true。</returns>
    private static bool IsPrivateOrLoopbackAddress(string host)
    {
        if (string.IsNullOrEmpty(host))
            return false;

        // localhost 主机名
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        // 尝试解析为 IP 地址
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                // IPv4 私有/保留地址段检查
                var bytes = ip.GetAddressBytes();
                // 127.0.0.0/8 — 回环
                if (bytes[0] == 127) return true;
                // 10.0.0.0/8 — 私有 A 类
                if (bytes[0] == 10) return true;
                // 172.16.0.0/12 — 私有 B 类
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                // 192.168.0.0/16 — 私有 C 类
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                // 169.254.0.0/16 — 链路本地
                if (bytes[0] == 169 && bytes[1] == 254) return true;
                // 0.0.0.0/8 — 本机网络
                if (bytes[0] == 0) return true;
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // IPv6 回环 ::1
                if (IPAddress.IsLoopback(ip)) return true;
                // IPv6 链路本地 fe80::/10
                if (ip.IsIPv6LinkLocal) return true;
                // IPv6 站点本地 fec0::/10（已弃用但仍检查）
                if (ip.IsIPv6SiteLocal) return true;

                var v6Bytes = ip.GetAddressBytes();

                // IPv6 唯一本地地址 fc00::/7（含 fd00::/8，IPv6 私有地址等价物）
                if (v6Bytes.Length == 16 && (v6Bytes[0] == 0xfc || v6Bytes[0] == 0xfd))
                    return true;

                // IPv4-mapped IPv6 地址 ::ffff:a.b.c.d — 防止通过 IPv6 映射绕过 IPv4 检查
                // 标准格式: 前10字节为0，第11/12字节为0xff
                if (v6Bytes.Length == 16 &&
                    v6Bytes[10] == 0xff && v6Bytes[11] == 0xff)
                {
                    var mappedIp = new IPAddress(v6Bytes[12..]);
                    return IsPrivateOrLoopbackAddress(mappedIp.ToString());
                }

                // IPv4-compatible IPv6 地址 ::a.b.c.d（deprecated RFC 4291）— 前12字节全零
                // 防止通过 IPv6 兼容格式绕过 IPv4 检查（::1 已被上面 IsLoopback 捕获）
                if (v6Bytes.Length == 16 &&
                    v6Bytes[0] == 0 && v6Bytes[1] == 0 && v6Bytes[2] == 0 && v6Bytes[3] == 0 &&
                    v6Bytes[4] == 0 && v6Bytes[5] == 0 && v6Bytes[6] == 0 && v6Bytes[7] == 0 &&
                    v6Bytes[8] == 0 && v6Bytes[9] == 0 && v6Bytes[10] == 0 && v6Bytes[11] == 0)
                {
                    var compatIp = new IPAddress(v6Bytes[12..]);
                    return IsPrivateOrLoopbackAddress(compatIp.ToString());
                }
            }
        }

        return false;
    }
}

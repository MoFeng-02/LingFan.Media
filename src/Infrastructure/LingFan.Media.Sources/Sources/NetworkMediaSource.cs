using System.Collections.Frozen;
using System.Net;
using LingFan.Media.Sources.Security;

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

    /// <summary>网络超时（连接与响应头阶段，默认 30 秒）。</summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// 读超时（数据阶段）：单次读取在该时长内未读到任何数据即判定连接停滞并中断连接。
    /// 默认 10 秒；设为 <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> 可关闭（不推荐）。
    /// </summary>
    public TimeSpan ReadTimeout { get; }

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
    /// <param name="readTimeout">读超时（数据阶段，默认 10 秒；传 <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> 关闭）。</param>
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
        bool allowPrivateAddresses = false,
        TimeSpan? readTimeout = null)
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
        ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(10);
        AllowInsecureHttps = allowInsecureHttps;
        AllowPrivateAddresses = allowPrivateAddresses;
        Cookies = cookies;
        Name = name;
        ContentType = contentType;
        IsLive = isLive;
        ExtraFields = extraFields ?? FrozenDictionary<string, string>.Empty;
    }

    /// <summary>
    /// 检查目标主机是否为内网/回环/保留地址（构造期字符串级快查）。
    /// </summary>
    /// <remarks>
    /// <para>名单单一真源 = <see cref="SsrfGuard.IsPrivateOrLoopbackOrReserved"/>（与 DNS 解析后校验、
    /// 建连级校验完全同源，杜绝双清单漂移）；编码字面量（hex/十进制/八进制点分）经
    /// <see cref="SsrfGuard.TryParseIpLiteral"/> 补齐（IPAddress.TryParse 不识别这些形式）。</para>
    /// <para>本检查是快失败层，DNS 解析后的 <see cref="SsrfGuard.Validate"/>（工厂层）与
    /// 建连级 pinning（ConnectCallback）仍逐层兜底。</para>
    /// </remarks>
    /// <param name="host">主机名或 IP 地址字符串（IPv6 含方括号形式，TryParse 可解析）。</param>
    /// <returns>是内网/回环/保留地址返回 true。</returns>
    private static bool IsPrivateOrLoopbackAddress(string host)
    {
        if (string.IsNullOrEmpty(host))
            return false;

        // localhost 主机名（非 IP 形式，字面量解析不覆盖）
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        // 标准点分/方括号 IPv6 字面量
        if (IPAddress.TryParse(host, out var ip) && SsrfGuard.IsPrivateOrLoopbackOrReserved(ip))
            return true;

        // 编码字面量：0x7f000001 / 2130706433 / 0177.0.0.1
        return SsrfGuard.TryParseIpLiteral(host, out var literal)
               && SsrfGuard.IsPrivateOrLoopbackOrReserved(literal);
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security;

namespace LingFan.Media.Sources.Security;

/// <summary>
/// SSRF 防护辅助。
/// </summary>
/// <remarks>
/// <para>在网络连接建立前的 DNS 解析点对目标 IP 进行私有/回环/链路本地/保留/CGNAT 检查，
/// 并解析十六进制/十进制 IP 字面量表示法（L21），防止通过编码形式绕过 <see cref="NetworkMediaSource"/> 构造期的字符串检查。</para>
/// <para>所有方法均为纯 CPU 计算（无 I/O、无状态），可安全在异步路径中调用，不会引入伪异步或同步阻塞。</para>
/// <para>调用点：<see cref="MediaStreamFactory.CreateAsync"/>（DNS 解析后快速失败）与
/// <see cref="NetworkMediaStream"/> 的建连路径（确保两条工厂路径均受保护）。</para>
/// </remarks>
public static class SsrfGuard
{
    /// <summary>
    /// 校验解析后的目标 IP 与主机字面量，命中私有/保留范围且未显式允许时抛 <see cref="SecurityException"/>。
    /// </summary>
    /// <param name="host">原始主机名/IP 字面量（用于 L21 编码形式检查）。</param>
    /// <param name="resolvedIps">DNS 解析得到的 IP 列表（L20 DNS 重绑定防护）。</param>
    /// <param name="allowPrivateAddresses">是否允许访问内网（SSRF 防护开关）。</param>
    /// <exception cref="SecurityException">命中私有/保留地址且未允许。</exception>
    public static void Validate(string host, IPAddress[] resolvedIps, bool allowPrivateAddresses)
    {
        ArgumentNullException.ThrowIfNull(resolvedIps);

        // L20：DNS 重绑定防护 —— 校验解析后的真实 IP 是否在私有/保留范围
        foreach (var ip in resolvedIps)
        {
            if (IsPrivateOrLoopbackOrReserved(ip) && !allowPrivateAddresses)
            {
                throw new SecurityException(
                    $"DNS 解析结果 {ip} 位于私有/回环/保留地址范围（含 CGNAT 100.64.0.0/10），已拒绝（SSRF 防护）。" +
                    "如需访问内网，请设置 allowPrivateAddresses: true。");
            }
        }

        // L21：十六进制/十进制 IP 字面量（DNS 可能不解析，需单独解析检查）
        if (TryParseIpLiteral(host, out var literalIp) &&
            IsPrivateOrLoopbackOrReserved(literalIp) && !allowPrivateAddresses)
        {
            throw new SecurityException(
                $"主机 {host} 解析为内网/保留地址 {literalIp}，已拒绝（SSRF 防护）。");
        }
    }

    /// <summary>
    /// 判断 IP 是否为私有/回环/链路本地/保留/CGNAT 地址（L22：新增 CGNAT 100.64.0.0/10）。
    /// </summary>
    /// <param name="ip">待检查 IP。</param>
    /// <returns>是私有/保留地址返回 true。</returns>
    public static bool IsPrivateOrLoopbackOrReserved(IPAddress ip)
    {
        if (ip is null)
            return false;

        // IPv4-mapped / IPv4-compatible IPv6 统一映射回 IPv4 检查
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
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
            // 0.0.0.0/8 — 本地网络
            if (bytes[0] == 0) return true;
            // 100.64.0.0/10 — CGNAT（L22）
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
            // 224.0.0.0/4 起 — 多播/保留（含 240.0.0.0/4）
            if (bytes[0] >= 224) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::1 回环
            if (IPAddress.IsLoopback(ip)) return true;
            // fe80::/10 链路本地
            if (ip.IsIPv6LinkLocal) return true;
            // fec0::/10 站点本地（已弃用但仍检查）
            if (ip.IsIPv6SiteLocal) return true;

            var b = ip.GetAddressBytes();
            if (b.Length == 16)
            {
                // fc00::/7 唯一本地地址（含 fd00::/8，IPv6 私有等价物）
                if (b[0] == 0xfc || b[0] == 0xfd) return true;
                // 多播/保留（ff00::/8 起）
                if (b[0] >= 0xf0) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// L21：解析十六进制（0x7f000001）/ 十进制（2130706433）/ 八进制点分（0177.0.0.1）IP 字面量表示法。
    /// </summary>
    /// <remarks>
    /// <para>十六进制与十进制整数字面量无点分结构（避免误判标准点分十进制，如 192.168.1.1）；
    /// 八进制点分仅当某段以 0 开头且长度 &gt; 1 时才按八进制解释（如 0177.0.0.1 → 127.0.0.1），
    /// 标准点分十进制（无前导零）不会误命中。</para>
    /// <para>这些方法与 <see cref="NetworkMediaSource"/> 构造期的字符串检查互补，
    /// 覆盖 DNS 可能不解析、但字面量本身即指向内网的编码形式（L21）。</para>
    /// </remarks>
    /// <param name="host">主机名/IP 字面量。</param>
    /// <param name="ip">解析出的 IP；失败为 null。</param>
    /// <returns>成功解析返回 true。</returns>
    public static bool TryParseIpLiteral(string host, [NotNullWhen(true)] out IPAddress? ip)
    {
        ip = null;
        if (string.IsNullOrEmpty(host))
            return false;

        // 十六进制：0x7f000001 → 127.0.0.1
        if (host.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(host.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var val) &&
                val >= 0 && val <= 0xFFFFFFFF)
            {
                ip = FromInt((uint)val);
                return true;
            }
            return false;
        }

        // 十进制整数（无点分结构）：2130706433 → 127.0.0.1
        if (host.IndexOf('.') < 0 &&
            long.TryParse(host, NumberStyles.None, CultureInfo.InvariantCulture, out var decimalVal) &&
            decimalVal >= 0 && decimalVal <= 0xFFFFFFFF)
        {
            ip = FromInt((uint)decimalVal);
            return true;
        }

        // 八进制点分（0177.0.0.1 → 127.0.0.1）：仅当确含前导零段时按八进制解释（L21）
        if (TryParseDottedOctal(host, out var octalIp))
        {
            ip = octalIp;
            return true;
        }

        return false;
    }

    /// <summary>
    /// L21：解析八进制点分 IP 表示法（如 <c>0177.0.0.1</c> → 127.0.0.1）。
    /// </summary>
    /// <remarks>
    /// 仅当四段中至少一段以 0 开头且长度 &gt; 1（即确实为八进制）时才解析，
    /// 标准点分十进制（如 192.168.1.1，无前导零）直接返回 false，避免与正常地址重复解析。
    /// </remarks>
    /// <param name="host">主机名/IP 字面量。</param>
    /// <param name="ip">解析出的 IP；失败为 null。</param>
    /// <returns>成功解析返回 true。</returns>
    private static bool TryParseDottedOctal(string host, [NotNullWhen(true)] out IPAddress? ip)
    {
        ip = null;

        var parts = host.Split('.');
        if (parts.Length != 4)
            return false;

        var bytes = new byte[4];
        bool anyOctal = false;

        for (int i = 0; i < 4; i++)
        {
            var part = parts[i];
            // 合法段长度：1–4（八进制 0377 为 4 字符；十进制 255 为 3 字符）
            if (part.Length == 0 || part.Length > 4)
                return false;

            // 以 0 开头且长度 > 1 → 视为八进制（L21 八进制表示法）。
            // 注：.NET 的 NumberStyles 无 Octal 枚举，故手动按 8 进制累加解析。
            if (part.Length > 1 && part[0] == '0')
            {
                int octalValue = 0;
                for (int j = 1; j < part.Length; j++)
                {
                    if (!char.IsDigit(part[j]))
                        return false;
                    octalValue = octalValue * 8 + (part[j] - '0');
                }

                if (octalValue is < 0 or > 255)
                    return false;

                anyOctal = true;
                bytes[i] = (byte)octalValue;
            }
            else if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var decimalValue))
            {
                if (decimalValue is < 0 or > 255)
                    return false;
                bytes[i] = (byte)decimalValue;
            }
            else
            {
                return false;
            }
        }

        // 仅当确实含八进制段时才视为编码形式
        if (!anyOctal)
            return false;

        ip = new IPAddress(bytes);
        return true;
    }

    /// <summary>
    /// 将 32 位整数按网络字节序（大端）拆成 4 字节构造 <see cref="IPAddress"/>，
    /// 避免 <c>new IPAddress(uint)</c> 的端序歧义。
    /// </summary>
    private static IPAddress FromInt(uint value)
    {
        return new IPAddress(
        [
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value,
        ]);
    }
}

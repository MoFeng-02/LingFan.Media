using System.Net;
using FluentAssertions;
using LingFan.Media.Sources.Security;

namespace LingFan.Media.Sources.Tests;

public class SsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]      // 回环
    [InlineData("10.0.0.5")]       // 私有 A 类
    [InlineData("172.16.0.1")]     // 私有 B 类
    [InlineData("192.168.1.1")]   // 私有 C 类
    [InlineData("169.254.0.1")]    // 链路本地
    [InlineData("100.64.0.1")]     // CGNAT 100.64.0.0/10 (L22)
    [InlineData("100.127.255.254")] // CGNAT 上界
    [InlineData("::1")]            // IPv6 回环
    [InlineData("fe80::1")]        // IPv6 链路本地
    [InlineData("fc00::1")]        // IPv6 ULA
    [InlineData("fd00::1")]        // IPv6 ULA
    public void IsPrivateOrLoopbackOrReserved_RejectsPrivateAndReserved(string ipText)
    {
        var ip = IPAddress.Parse(ipText);
        SsrfGuard.IsPrivateOrLoopbackOrReserved(ip).Should().BeTrue();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("203.0.113.5")]    // TEST-NET（公开文档用，非保留拦截范围）
    [InlineData("2001:4860:4860::8888")] // 公共 IPv6
    public void IsPrivateOrLoopbackOrReserved_AllowsPublic(string ipText)
    {
        var ip = IPAddress.Parse(ipText);
        SsrfGuard.IsPrivateOrLoopbackOrReserved(ip).Should().BeFalse();
    }

    [Fact]
    public void Validate_ThrowsOnPrivateResolvedIp_WhenNotAllowed()
    {
        var ips = new[] { IPAddress.Parse("100.64.0.1") }; // CGNAT
        var act = () => SsrfGuard.Validate("cdn.example.com", ips, allowPrivateAddresses: false);
        act.Should().Throw<System.Security.SecurityException>();
    }

    [Fact]
    public void Validate_AllowsPrivate_WhenExplicitlyPermitted()
    {
        var ips = new[] { IPAddress.Parse("10.0.0.1") };
        var act = () => SsrfGuard.Validate("intra.example.com", ips, allowPrivateAddresses: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_AllowsPublicIp()
    {
        var ips = new[] { IPAddress.Parse("8.8.8.8") };
        var act = () => SsrfGuard.Validate("public.example.com", ips, allowPrivateAddresses: false);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("0x7f000001", "127.0.0.1")]   // 十六进制
    [InlineData("2130706433", "127.0.0.1")]   // 十进制整数
    [InlineData("0177.0.0.1", "127.0.0.1")]  // 八进制点分（L21）
    public void TryParseIpLiteral_ParsesEncodedForms(string literal, string expected)
    {
        SsrfGuard.TryParseIpLiteral(literal, out var ip).Should().BeTrue();
        ip!.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("192.168.1.1")]  // 标准点分十进制（不应按整数误解析）
    [InlineData("not-an-ip")]
    [InlineData("")]
    public void TryParseIpLiteral_RejectsNonIntegerLiterals(string literal)
    {
        SsrfGuard.TryParseIpLiteral(literal, out _).Should().BeFalse();
    }

    [Fact]
    public void Validate_ThrowsOnEncodedPrivateLiteral_HostOnly()
    {
        // DNS 可能不解析 0x7f000001，但字面量本身应被拦截（L21）
        var ips = new[] { IPAddress.Parse("8.8.8.8") };
        var act = () => SsrfGuard.Validate("0x7f000001", ips, allowPrivateAddresses: false);
        act.Should().Throw<System.Security.SecurityException>();
    }

    [Fact]
    public void Validate_ThrowsOnOctalLiteral_HostOnly() // L21：八进制点分 0177.0.0.1 → 127.0.0.1
    {
        var ips = new[] { IPAddress.Parse("8.8.8.8") };
        var act = () => SsrfGuard.Validate("0177.0.0.1", ips, allowPrivateAddresses: false);
        act.Should().Throw<System.Security.SecurityException>();
    }
}

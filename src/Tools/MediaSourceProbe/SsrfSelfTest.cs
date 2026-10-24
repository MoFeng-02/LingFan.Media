using System.Net;
using LingFan.Media.Sources;
using LingFan.Media.Sources.Security;

namespace MediaSourceProbe;

/// <summary>
/// SSRF 守卫离线自检：不联网，逐条验证构造期拒绝/放行、编码字面量解析与解析结果级校验。
/// </summary>
/// <remarks>
/// 用于在改动守卫名单后快速确认防护面没有漂移；联网场景的拒绝行为由常规跑测覆盖。
/// 退出码：全部通过为 0，任一失败为 1。
/// </remarks>
internal static class SsrfSelfTest
{
    public static int Run()
    {
        Console.WriteLine("=== SSRF 守卫离线自检（不联网） ===");
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok)
            {
                pass++;
                Console.WriteLine($"  [PASS] {name}");
            }
            else
            {
                fail++;
                Console.WriteLine($"  [FAIL] {name}  {detail}");
            }
        }

        // 1) 构造期拒绝：内网/回环/保留地址（字符串级检查）
        string[] rejectedHosts =
        {
            "localhost",
            "127.0.0.1", "10.0.0.5", "172.16.0.1", "172.31.255.255", "192.168.1.1",
            "169.254.1.1", "0.0.0.0", "100.64.0.1", "224.0.0.1",
            "[::1]", "[fe80::1]", "[fd00::1]", "[::ffff:127.0.0.1]",
        };
        foreach (var host in rejectedHosts)
        {
            bool rejected;
            try
            {
                _ = new NetworkMediaSource($"https://{host}/x.mp4");
                rejected = false;
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            Check($"构造期拒绝 {host}", rejected);
        }

        // 2) 构造期放行：显式允许内网
        bool allowed = true;
        try { _ = new NetworkMediaSource("https://127.0.0.1/x.mp4", allowPrivateAddresses: true); }
        catch (Exception ex) { allowed = false; Console.WriteLine($"    放行分支异常: {ex.Message}"); }
        Check("构造期放行（allowPrivateAddresses=true）", allowed);

        // 3) 编码字面量解析（DNS 不参与的绕过形式）
        Check("hex 0x7f000001 -> 127.0.0.1",
            SsrfGuard.TryParseIpLiteral("0x7f000001", out var hexIp) && hexIp!.Equals(IPAddress.Parse("127.0.0.1")));
        Check("decimal 2130706433 -> 127.0.0.1",
            SsrfGuard.TryParseIpLiteral("2130706433", out var decIp) && decIp!.Equals(IPAddress.Parse("127.0.0.1")));
        Check("octal 0177.0.0.1 -> 127.0.0.1",
            SsrfGuard.TryParseIpLiteral("0177.0.0.1", out var octIp) && octIp!.Equals(IPAddress.Parse("127.0.0.1")));
        Check("标准点分 192.168.1.1 不按字面量误判",
            !SsrfGuard.TryParseIpLiteral("192.168.1.1", out _));
        Check("hex 结果判私有",
            SsrfGuard.TryParseIpLiteral("0x7f000001", out var literal) && SsrfGuard.IsPrivateOrLoopbackOrReserved(literal!));

        // 4) Validate：解析结果级拒绝/放行
        bool validateRejects = false;
        try { SsrfGuard.Validate("host.test", [IPAddress.Parse("127.0.0.1")], allowPrivateAddresses: false); }
        catch (System.Security.SecurityException) { validateRejects = true; }
        Check("Validate 拒绝回环解析结果", validateRejects);

        bool validateAllowsPrivate = false;
        try { SsrfGuard.Validate("host.test", [IPAddress.Parse("127.0.0.1")], allowPrivateAddresses: true); validateAllowsPrivate = true; }
        catch { }
        Check("Validate 放行（allowPrivateAddresses=true）", validateAllowsPrivate);

        bool validateAllowsPublic = false;
        try { SsrfGuard.Validate("host.test", [IPAddress.Parse("8.8.8.8")], allowPrivateAddresses: false); validateAllowsPublic = true; }
        catch { }
        Check("Validate 放行公网地址", validateAllowsPublic);

        Console.WriteLine();
        Console.WriteLine($"=== SSRF 自检 {(fail == 0 ? "PASS" : "FAIL")}（{pass} 通过 / {fail} 失败） ===");
        return fail == 0 ? 0 : 1;
    }
}

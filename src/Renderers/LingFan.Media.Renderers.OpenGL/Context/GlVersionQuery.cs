using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.OpenGL.Context;

/// <summary>
/// 经 GL 1.1 基线 <c>glGetString(GL_VERSION)</c> 解析 GL 主/次版本号（如 <c>"3.3.0 ..."</c> → 3 / 3）。
/// </summary>
/// <remarks>跨平台通用（Win/Linux 上下文均已 MakeCurrent 后调用）。无反射，AOT 安全。</remarks>
internal static unsafe class GlVersionQuery
{
    private const uint GL_VERSION = 0x1F02;

    public static void Query(out int major, out int minor)
    {
        major = 0;
        minor = 0;
        nint p = GLNative.glGetString(GL_VERSION);
        if (p == nint.Zero) return;
        string? ver = Marshal.PtrToStringAnsi(p);
        if (string.IsNullOrEmpty(ver)) return;

        int dot = ver.IndexOf('.');
        if (dot < 0) return;
        int.TryParse(ver.AsSpan(0, dot), out major);

        int next = ver.IndexOf('.', dot + 1);
        int end = next < 0 ? ver.Length : next;
        int.TryParse(ver.AsSpan(dot + 1, end - dot - 1), out minor);
    }
}

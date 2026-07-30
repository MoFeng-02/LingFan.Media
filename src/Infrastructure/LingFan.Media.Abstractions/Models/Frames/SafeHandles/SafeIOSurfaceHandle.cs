using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Abstractions;

/// <summary>
/// Apple <c>IOSurfaceRef</c> 的 SafeHandle（拥有所有权，释放时调用 <c>CFRelease</c>）。
/// </summary>
/// <remarks>
/// <para><b>库归属</b>：<c>IOSurfaceRef</c> 是 CoreFoundation 对象，通过 <c>CFRetain</c>/<c>CFRelease</c> 管理引用计数，
/// LibraryImport 路径为 <c>/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation</c>
/// （中立原生边界，同 AHardwareBufferResource 直接 LibraryImport <c>libandroid.so</c> 的先例）。</para>
/// <para><b>获取方式</b>：通常由 <c>CVPixelBufferGetIOSurface</c>（Get 规则，返回非拥有引用）取得后
/// <c>CFRetain</c> 再交给本句柄接管所有权。</para>
/// <para><b>AOT 兼容</b>：sealed 类 + 静态 P/Invoke，无反射、无动态代码生成。</para>
/// <para>仅应在 macOS / iOS 平台创建；其他平台调用 <see cref="ReleaseHandle"/> 会因找不到 CoreFoundation 抛出异常。</para>
/// </remarks>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
internal sealed partial class SafeIOSurfaceHandle : SafeHandle
{
    private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>创建空句柄（供互操作层填充）。</summary>
    public SafeIOSurfaceHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <summary>
    /// 包装一个已 retain 的 <c>IOSurfaceRef</c>（本句柄接管所有权，释放时调用 <c>CFRelease</c>）。
    /// </summary>
    /// <param name="ioSurface">已 retain 的 <c>IOSurfaceRef</c>，必须非空。</param>
    public SafeIOSurfaceHandle(IntPtr ioSurface) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(ioSurface);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        CFRelease(handle);
        return true;
    }

    [LibraryImport(CoreFoundationLibrary, EntryPoint = "CFRelease")]
    private static partial void CFRelease(IntPtr cf);
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Abstractions;

/// <summary>
/// Apple <c>CVPixelBufferRef</c> 的 SafeHandle（拥有所有权，释放时调用 <c>CVPixelBufferRelease</c>）。
/// </summary>
/// <remarks>
/// <para><b>库归属</b>：<c>CVPixelBufferRelease</c> 属 CoreVideo framework，LibraryImport 路径为
/// <c>/System/Library/Frameworks/CoreVideo.framework/CoreVideo</c>
/// （中立原生边界，同 AHardwareBufferResource 直接 LibraryImport <c>libandroid.so</c> 的先例）。
/// <c>CVPixelBufferRelease(NULL)</c> 安全（no-op），但 SafeHandle 机制已保证仅在句柄有效时调用。</para>
/// <para><b>获取方式</b>：FFmpeg VideoToolbox 硬解输出 <c>AVFrame.data[3]</c> 即 <c>CVPixelBufferRef</c>
/// （非拥有引用），需先 <c>CVPixelBufferRetain</c> 再交给本句柄接管所有权。</para>
/// <para><b>AOT 兼容</b>：sealed 类 + 静态 P/Invoke，无反射、无动态代码生成。</para>
/// <para>仅应在 macOS / iOS 平台创建；其他平台调用 <see cref="ReleaseHandle"/> 会因找不到 CoreVideo 抛出异常。</para>
/// </remarks>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
internal sealed partial class SafeCVPixelBufferHandle : SafeHandle
{
    private const string CoreVideoLibrary = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";

    /// <summary>创建空句柄（供互操作层填充）。</summary>
    public SafeCVPixelBufferHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <summary>
    /// 包装一个已 retain 的 <c>CVPixelBufferRef</c>（本句柄接管所有权，释放时调用 <c>CVPixelBufferRelease</c>）。
    /// </summary>
    /// <param name="pixelBuffer">已 retain 的 <c>CVPixelBufferRef</c>，必须非空。</param>
    public SafeCVPixelBufferHandle(IntPtr pixelBuffer) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(pixelBuffer);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        CVPixelBufferRelease(handle);
        return true;
    }

    [LibraryImport(CoreVideoLibrary, EntryPoint = "CVPixelBufferRelease")]
    private static partial void CVPixelBufferRelease(IntPtr pixelBuffer);
}

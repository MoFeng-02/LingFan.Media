namespace LingFan.Media.Renderers.Metal.SafeHandles;

/// <summary>
/// IOSurface 的 SafeHandle 桩。
/// </summary>
/// <remarks>
/// V1 桩实现——Metal 渲染器尚未实现（Phase 3 目标）。
/// 未来实现时封装 IOSurfaceRef（Apple 共享内存，
/// CVPixelBuffer → IOSurface → MTLTexture 桥梁）。
/// </remarks>
internal sealed class SafeIOSurfaceHandle : SafeHandle
{
    public SafeIOSurfaceHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle() => true;
}

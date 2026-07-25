namespace LingFan.Media.Renderers.Metal.SafeHandles;

/// <summary>
/// CoreVideo 像素缓冲的 SafeHandle 桩。
/// </summary>
/// <remarks>
/// V1 桩实现——Metal 渲染器尚未实现（Phase 3 目标）。
/// 未来实现时封装 CVPixelBufferRef（Apple VideoToolbox 硬解输出）。
/// </remarks>
internal sealed class SafeCVPixelBufferHandle : SafeHandle
{
    public SafeCVPixelBufferHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle() => true;
}

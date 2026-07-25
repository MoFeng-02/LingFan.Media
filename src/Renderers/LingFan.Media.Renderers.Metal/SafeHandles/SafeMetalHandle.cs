namespace LingFan.Media.Renderers.Metal.SafeHandles;

/// <summary>
/// Metal 纹理的 SafeHandle 桩。
/// </summary>
/// <remarks>
/// V1 桩实现——Metal 渲染器尚未实现（Phase 3 目标）。
/// 未来实现时封装 SharpMetal 的 MTLTexture 对象。
/// </remarks>
internal sealed class SafeMetalHandle : SafeHandle
{
    public SafeMetalHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle() => true;
}

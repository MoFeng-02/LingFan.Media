namespace LingFan.Media.Renderers.Metal;

/// <summary>
/// Metal 枚举常量（与 Apple 官方 <c>Metal/MTLPixelFormat.h</c>、<c>MTLCommandBuffer.h</c> 等头文件数值一致）。
/// </summary>
/// <remarks>
/// <para>仅抽取本渲染器实际使用的子集，不引入 Metal 原生头文件依赖。</para>
/// <para>AOT 兼容：纯 static 常量，无反射。</para>
/// </remarks>
internal static class MetalConstants
{
    // ── MTLPixelFormat（部分）──
    public const nuint R8Unorm = 10;
    public const nuint RG8Unorm = 30;
    public const nuint RGBA8Unorm = 70;
    public const nuint BGRA8Unorm = 80;

    // ── MTLLoadAction ──
    public const nuint LoadActionClear = 2;

    // ── MTLStoreAction ──
    public const nuint StoreActionStore = 1;

    // ── MTLPrimitiveType ──
    public const nuint PrimitiveTypeTriangleStrip = 4;

    // ── MTLResourceOptions（共享存储，CPU/GPU 均可访问）──
    public const nuint ResourceStorageModeShared = 0;
}

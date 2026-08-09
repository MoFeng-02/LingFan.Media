namespace LingFan.Media.Platforms.IOS;

/// <summary>
/// iOS 平台服务。桩实现。
/// </summary>
/// <remarks>
/// <para>桩——<see cref="CreateHardwareDecoder"/> 和 <see cref="GetGPUContext"/> 抛出 <see cref="NotSupportedException"/>。
/// iOS 硬解 / GPU 互操作属 Phase 2-3 目标（VideoToolbox + Metal）。</para>
/// <para><b>GPU 零拷贝路径</b>：
/// VideoToolbox → CVPixelBuffer → IOSurface → MTLTexture → MetalRenderer → CAMetalLayer → Display</para>
/// <para><b>与 macOS 的差异</b>：iOS 不需要单独的 IOSurfaceInterop——iOS 上 CVPixelBuffer 可直接
/// 传入 <c>MTLTexture</c> 创建方法（<c>id&lt;MTLDevice&gt;::newTextureWithDescriptor:offset:</c>），
/// IOSurface 在 iOS 上是 CVPixelBuffer 的内部实现，无需显式操作。</para>
/// <para><b>异步策略</b>：全部同步（config / sync 分类）——属性为纯读取，方法为桩抛异常，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class IOSPlatformServices : IPlatformServices
{
    /// <inheritdoc/>
    public OSPlatform Platform => OSPlatform.Create("iOS");

    /// <inheritdoc/>
    public bool SupportsHardwareDecode => true;

    /// <inheritdoc/>
    public bool SupportsGPUInterop => true;

    /// <inheritdoc/>
    public IVideoDecoder? CreateHardwareDecoder(VideoCodec codec)
        => throw new NotSupportedException(
            "iOS 硬件解码尚未实现。VideoToolbox 硬解为 Phase 2-3 目标。");

    /// <inheritdoc/>
    public object? GetGPUContext(GPUApiType type)
        => throw new NotSupportedException(
            "iOS GPU 上下文尚未实现。Metal 渲染器为 Phase 2-3 目标。");
}

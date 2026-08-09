namespace LingFan.Media.Platforms.MacOS;

/// <summary>
/// macOS 平台服务。桩实现。
/// </summary>
/// <remarks>
/// <para>桩——<see cref="CreateHardwareDecoder"/> 和 <see cref="GetGPUContext"/> 抛出 <see cref="NotSupportedException"/>。
/// macOS 硬解 / GPU 互操作属 Phase 2-3 目标（VideoToolbox + Metal）。</para>
/// <para><b>GPU 零拷贝路径</b>：
/// VideoToolbox → CVPixelBuffer → IOSurface → MTLTexture → MetalRenderer → CAMetalLayer → Display</para>
/// <para><b>异步策略</b>：全部同步（config / sync 分类）——属性为纯读取，方法为桩抛异常，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class MacPlatformServices : IPlatformServices
{
    /// <inheritdoc/>
    public OSPlatform Platform => OSPlatform.OSX;

    /// <inheritdoc/>
    public bool SupportsHardwareDecode => true;

    /// <inheritdoc/>
    public bool SupportsGPUInterop => true;

    /// <inheritdoc/>
    public IVideoDecoder? CreateHardwareDecoder(VideoCodec codec)
        => throw new NotSupportedException(
            "macOS 硬件解码尚未实现。VideoToolbox 硬解为 Phase 2-3 目标。");

    /// <inheritdoc/>
    public object? GetGPUContext(GPUApiType type)
        => throw new NotSupportedException(
            "macOS GPU 上下文尚未实现。Metal 渲染器为 Phase 2-3 目标。");
}

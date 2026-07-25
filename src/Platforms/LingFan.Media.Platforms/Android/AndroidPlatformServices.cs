namespace LingFan.Media.Platforms.Android;

/// <summary>
/// Android 平台服务。V1 桩实现。
/// </summary>
/// <remarks>
/// <para>V1 桩——<see cref="CreateHardwareDecoder"/> 和 <see cref="GetGPUContext"/> 抛出 <see cref="NotSupportedException"/>。
/// Android 硬解 / GPU 互操作属 Phase 2-3 目标（MediaCodec + Vulkan）。</para>
/// <para><b>GPU 零拷贝路径</b>：
/// MediaCodec → AHardwareBuffer → VkImage → VulkanRenderer → Swapchain → TextureView（SurfaceFlinger 合成）→ Display</para>
/// <para><b>异步策略</b>：全部同步（config / sync 分类）——属性为纯读取，方法为桩抛异常，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class AndroidPlatformServices : IPlatformServices
{
    /// <inheritdoc/>
    public OSPlatform Platform => OSPlatform.Create("Android");

    /// <inheritdoc/>
    public bool SupportsHardwareDecode => true;

    /// <inheritdoc/>
    public bool SupportsGPUInterop => true;

    /// <inheritdoc/>
    public IVideoDecoder? CreateHardwareDecoder(VideoCodec codec)
        => throw new NotSupportedException(
            "Android 硬件解码尚未实现。MediaCodec 硬解为 Phase 2-3 目标。");

    /// <inheritdoc/>
    public object? GetGPUContext(GPUApiType type)
        => throw new NotSupportedException(
            "Android GPU 上下文尚未实现。Vulkan 渲染器为 Phase 2-3 目标。");
}

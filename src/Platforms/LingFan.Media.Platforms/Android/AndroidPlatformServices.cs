namespace LingFan.Media.Platforms.Android;

/// <summary>
/// Android 平台服务（工厂接缝为桩）。
/// </summary>
/// <remarks>
/// <para>桩——<see cref="CreateHardwareDecoder"/> 和 <see cref="GetGPUContext"/> 抛出 <see cref="NotSupportedException"/>。
/// 实际 Android 硬解由 MediaCodec 后端提供（AHardwareBuffer 零拷贝），不经本接口；
/// <see cref="SupportsHardwareDecode"/>/<see cref="SupportsGPUInterop"/> 为 true 表示该后端路径可用。</para>
/// <para><b>GPU 零拷贝路径</b>：
/// MediaCodec → AHardwareBuffer → VkImage → VulkanRenderer → Swapchain → TextureView（SurfaceFlinger 合成）→ Display</para>
/// <para><b>异步策略</b>：全部同步——属性为纯读取，方法为桩抛异常，无 I/O。
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
            "Android 硬件解码未经本工厂接缝实现；实际 MediaCodec 硬解路径由 MediaCodec 后端提供。");

    /// <inheritdoc/>
    public object? GetGPUContext(GPUApiType type)
        => throw new NotSupportedException(
            "Android GPU 上下文未经本工厂接缝实现。");
}

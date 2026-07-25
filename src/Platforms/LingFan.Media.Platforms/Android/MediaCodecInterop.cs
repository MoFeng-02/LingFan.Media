namespace LingFan.Media.Platforms.Android;

/// <summary>
/// MediaCodec 硬件解码互操作。V1 桩实现。
/// </summary>
/// <remarks>
/// <para>职责：通过 Android NDK MediaCodec API 创建硬件解码器，
/// 输出 AHardwareBuffer 用于 Vulkan 零拷贝渲染。</para>
/// <para><b>GPU 零拷贝路径</b>：
/// MediaCodec → AHardwareBuffer → VkImage → VulkanRenderer → Swapchain → TextureView（SurfaceFlinger 合成）→ Display</para>
/// <para>V1 桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// MediaCodec 硬解属 Phase 2-3 目标（Android）。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——NDK MediaCodec API 是同步边界，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class MediaCodecInterop
{
    /// <summary>
    /// 创建 MediaCodec 硬件解码器。
    /// </summary>
    /// <param name="codec">视频编解码器类型（H264 / H265 / VP9 / AV1）。</param>
    /// <returns>AMediaCodec NDK 句柄。</returns>
    public nint CreateMediaCodec(VideoCodec codec)
        => throw new NotSupportedException("MediaCodec 互操作尚未实现。Phase 2-3 目标。");

    /// <summary>
    /// 配置 MediaCodec 解码器（设置 Surface 或 AHardwareBuffer 输出）。
    /// </summary>
    /// <param name="codec">AMediaCodec 句柄。</param>
    /// <param name="width">视频宽度。</param>
    /// <param name="height">视频高度。</param>
    public void ConfigureDecoder(nint codec, int width, int height)
        => throw new NotSupportedException("MediaCodec 配置尚未实现。");

    /// <summary>
    /// 从 MediaCodec 输出获取 AHardwareBuffer（零拷贝路径）。
    /// </summary>
    /// <param name="codec">AMediaCodec 句柄。</param>
    /// <returns>AHardwareBuffer 句柄。</returns>
    public nint GetOutputHardwareBuffer(nint codec)
        => throw new NotSupportedException("MediaCodec AHardwareBuffer 输出尚未实现。");

    /// <summary>
    /// 销毁 MediaCodec 解码器。
    /// </summary>
    /// <param name="codec">AMediaCodec 句柄。</param>
    public void DestroyCodec(nint codec)
        => throw new NotSupportedException("MediaCodec 资源管理尚未实现。");
}

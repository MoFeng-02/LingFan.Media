namespace LingFan.Media.Platforms.IOS;

/// <summary>
/// iOS VideoToolbox 硬件解码互操作。V1 桩实现。
/// </summary>
/// <remarks>
/// <para>职责：通过 VideoToolbox 框架创建硬件解码器（VTDecompressionSession），
/// 输出 CVPixelBuffer，直接传入 Metal 创建 MTLTexture。</para>
/// <para><b>GPU 零拷贝路径（iOS）</b>：
/// VideoToolbox → CVPixelBuffer → MTLTexture → MetalRenderer → CAMetalLayer → Display</para>
/// <para><b>与 macOS 的差异</b>：iOS 上 CVPixelBuffer 内部即 IOSurface，
/// 可直接用于 Metal 纹理创建，无需显式 IOSurfaceInterop 步骤。</para>
/// <para>V1 桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// VideoToolbox 硬解属 Phase 2-3 目标（iOS）。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——CoreMedia / VideoToolbox C API 调用是同步边界，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class VideoToolboxInterop
{
    /// <summary>
    /// 创建 VideoToolbox 解压会话。
    /// </summary>
    /// <param name="codec">视频编解码器类型（H264 / H265 等）。</param>
    /// <param name="width">视频宽度。</param>
    /// <param name="height">视频高度。</param>
    /// <returns>VTDecompressionSession 原生句柄。</returns>
    public nint CreateDecompressionSession(VideoCodec codec, int width, int height)
        => throw new NotSupportedException("iOS VideoToolbox 互操作尚未实现。Phase 2-3 目标。");

    /// <summary>
    /// 解压一帧，输出 CVPixelBuffer。
    /// </summary>
    /// <param name="session">VTDecompressionSession 句柄。</param>
    /// <param name="sampleBuffer">CMSampleBuffer 句柄（含压缩数据）。</param>
    /// <returns>CVPixelBuffer 原生句柄（需调用方释放）。</returns>
    public nint DecompressFrame(nint session, nint sampleBuffer)
        => throw new NotSupportedException("iOS VideoToolbox 解压尚未实现。");

    /// <summary>
    /// 销毁解压会话。
    /// </summary>
    /// <param name="session">VTDecompressionSession 句柄。</param>
    public void DestroySession(nint session)
        => throw new NotSupportedException("iOS VideoToolbox 会话管理尚未实现。");
}

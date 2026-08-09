namespace LingFan.Media.Platforms.MacOS;

/// <summary>
/// VideoToolbox 硬件解码互操作。桩实现。
/// </summary>
/// <remarks>
/// <para>职责：通过 VideoToolbox 框架创建硬件解码器（VTDecompressionSession），
/// 输出 CVPixelBuffer，再通过 IOSurface 导入 Metal 实现零拷贝。</para>
/// <para><b>GPU 零拷贝路径</b>：
/// VideoToolbox → CVPixelBuffer → IOSurface → MTLTexture → MetalRenderer → CAMetalLayer → Display</para>
/// <para>桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// VideoToolbox 硬解属 Phase 2-3 目标（macOS）。</para>
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
        => throw new NotSupportedException("VideoToolbox 互操作尚未实现。Phase 2-3 目标。");

    /// <summary>
    /// 解压一帧，输出 CVPixelBuffer。
    /// </summary>
    /// <param name="session">VTDecompressionSession 句柄。</param>
    /// <param name="sampleBuffer">CMSampleBuffer 句柄（含压缩数据）。</param>
    /// <returns>CVPixelBuffer 原生句柄（需调用方释放）。</returns>
    public nint DecompressFrame(nint session, nint sampleBuffer)
        => throw new NotSupportedException("VideoToolbox 解压尚未实现。");

    /// <summary>
    /// 销毁解压会话。
    /// </summary>
    /// <param name="session">VTDecompressionSession 句柄。</param>
    public void DestroySession(nint session)
        => throw new NotSupportedException("VideoToolbox 会话管理尚未实现。");
}

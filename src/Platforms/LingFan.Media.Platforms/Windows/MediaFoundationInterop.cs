namespace LingFan.Media.Platforms.Windows;

/// <summary>
/// Media Foundation 硬件解码互操作。桩实现。
/// </summary>
/// <remarks>
/// <para>职责：通过 Media Foundation 的 <c>IMFTransform</c> 创建硬件解码器，
/// 输出 D3D11 纹理实现零拷贝路径。</para>
/// <para><b>GPU 零拷贝路径</b>：MF Hardware MFT → ID3D11Texture2D → D3D11Renderer → SwapChain → DirectComposition（IDCompositionVisual 合成到窗口 Visual 树）→ Display</para>
/// <para>桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// 硬解路径由 FFmpeg + D3D11VA 提供（在 Backends.FFmpeg 模块实现）。
/// 未来可添加 Media Foundation 后端作为 FFmpeg 的替代方案。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——COM 调用是同步边界，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class MediaFoundationInterop
{
    /// <summary>
    /// 创建 Media Foundation 硬件解码器（IMFTransform）。
    /// </summary>
    /// <param name="codec">视频编解码器类型。</param>
    /// <param name="width">视频宽度。</param>
    /// <param name="height">视频高度。</param>
    /// <returns>IMFTransform 原生 COM 句柄。</returns>
    public nint CreateHardwareMFT(VideoCodec codec, int width, int height)
        => throw new NotSupportedException(
            "Media Foundation 硬件解码尚未实现。硬解由 FFmpeg + D3D11VA 提供。");

    /// <summary>
    /// 为 IMFTransform 配置 D3D11 输出类型（零拷贝路径）。
    /// </summary>
    /// <param name="mft">IMFTransform COM 句柄。</param>
    /// <param name="d3d11Device">ID3D11Device COM 句柄。</param>
    public void ConfigureD3D11Output(nint mft, nint d3d11Device)
        => throw new NotSupportedException("Media Foundation D3D11 输出配置尚未实现。");

    /// <summary>
    /// 从 MF 硬解器输出提取 D3D11 纹理。
    /// </summary>
    /// <param name="mft">IMFTransform COM 句柄。</param>
    /// <returns>ID3D11Texture2D 原生句柄 + 子资源索引。</returns>
    public (nint texture, int index) GetOutputTexture(nint mft)
        => throw new NotSupportedException("Media Foundation 纹理提取尚未实现。");
}

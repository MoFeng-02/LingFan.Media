namespace LingFan.Media.Platforms.Windows;

/// <summary>
/// Windows 平台服务。V1 桩实现。
/// </summary>
/// <remarks>
/// <para>V1 桩——<see cref="CreateHardwareDecoder"/> 和 <see cref="GetGPUContext"/> 抛出 <see cref="NotSupportedException"/>。
/// V1 实际硬解路径由 FFmpeg + D3D11VA 提供（在 Backends.FFmpeg 模块实现），不经过此接口。</para>
/// <para><b>GPU 零拷贝路径</b>：DXVA2 / D3D11VA → ID3D11Texture2D → D3D11Renderer → SwapChain → DirectComposition（IDCompositionVisual 合成到窗口 Visual 树）→ Display</para>
/// <para><b>异步策略</b>：全部同步（config / sync 分类）——属性为纯读取，方法为桩抛异常，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class WindowsPlatformServices : IPlatformServices
{
    /// <inheritdoc/>
    public OSPlatform Platform => OSPlatform.Windows;

    /// <inheritdoc/>
    public bool SupportsHardwareDecode => true;

    /// <inheritdoc/>
    public bool SupportsGPUInterop => true;

    /// <inheritdoc/>
    public IVideoDecoder? CreateHardwareDecoder(VideoCodec codec)
        => throw new NotSupportedException(
            "Windows 硬件解码尚未通过 IPlatformServices 实现。V1 硬解路径由 FFmpeg + D3D11VA 提供。");

    /// <inheritdoc/>
    public object? GetGPUContext(GPUApiType type)
        => throw new NotSupportedException(
            "Windows GPU 上下文尚未通过 IPlatformServices 实现。V1 由 D3D11RendererFactory 直接创建 ID3D11Device。");
}

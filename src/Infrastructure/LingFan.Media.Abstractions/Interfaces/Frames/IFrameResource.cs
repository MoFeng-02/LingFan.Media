namespace LingFan.Media.Abstractions;

/// <summary>
/// 帧资源接口。统一 CPU 内存帧和 GPU 纹理帧的资源管理。
/// </summary>
/// <remarks>
/// 已知 sealed 实现（在各自模块中实现，此处仅定义接口）：
/// <list type="bullet">
/// <item><see cref="SoftwareFrameResource"/>（CPU 内存，在 Abstractions/Models 中）</item>
/// <item>D3D11TextureResource / VulkanImageResource / GLTextureResource / MetalTextureResource</item>
/// <item><see cref="AHardwareBufferResource"/>（Android，跨层契约，在 Abstractions/Models 中）</item>
/// <item><see cref="CVPixelBufferResource"/> / <see cref="IOSurfaceResource"/>（Apple，跨层契约，在 Abstractions/Models 中）</item>
/// </list>
/// Renderer 侧用 pattern matching 匹配类型，AOT 安全。
/// </remarks>
public interface IFrameResource : IDisposable
{
    /// <summary>帧宽度（像素）。</summary>
    int Width { get; }

    /// <summary>帧高度（像素）。</summary>
    int Height { get; }

    /// <summary>像素格式。</summary>
    PixelFormat Format { get; }
}

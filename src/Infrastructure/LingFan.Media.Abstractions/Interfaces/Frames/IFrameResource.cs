namespace LingFan.Media.Abstractions;

/// <summary>
/// 帧资源接口。统一 CPU 内存帧和 GPU 纹理帧的资源管理。
/// </summary>
/// <remarks>
/// 已知实现：
/// <list type="bullet">
/// <item><see cref="SoftwareFrameResource"/>（CPU 内存帧，纯托管实现，位于本契约层）</item>
/// <item>GPU 纹理帧由各渲染/后端模块实现，例如 D3D11TextureResource、VulkanImageResource、GLTextureResource、MetalTextureResource（分属对应渲染工程）</item>
/// <item>平台特定原生帧（如 Android 的 AHardwareBuffer、Apple 的 CVPixelBuffer/IOSurface）由对应平台模块实现，不在此契约层</item>
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

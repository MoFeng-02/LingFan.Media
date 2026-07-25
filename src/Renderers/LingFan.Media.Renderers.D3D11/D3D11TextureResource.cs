using LingFan.Media.Renderers.D3D11.SafeHandles;

namespace LingFan.Media.Renderers.D3D11;

/// <summary>
/// Direct3D 11 纹理帧资源。实现 <see cref="IFrameResource"/>。
/// </summary>
/// <remarks>
/// <para>用于 DXVA 硬件解码路径——FFmpeg DXVA 解码输出 ID3D11Texture2D COM 指针，
/// 由 <see cref="SafeD3D11TextureHandle"/> 管理生命周期。</para>
/// <para>V1：D3D11TextureResource 为最小实现，Present 路径以 SoftwareFrameResource 为主。
/// V2 启用 DXVA 零拷贝路径后由 FFmpeg 后端创建实例。</para>
/// <para>AOT 兼容：sealed 类，IFrameResource 多态 + pattern matching。</para>
/// </remarks>
internal sealed class D3D11TextureResource : IFrameResource
{
    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>D3D11 纹理 SafeHandle（拥有 COM 对象所有权）。</summary>
    public SafeD3D11TextureHandle Texture { get; }

    /// <summary>子资源索引（DXVA 共享纹理数组，通常为 0）。</summary>
    public int SubresourceIndex { get; }

    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="D3D11TextureResource"/> 的新实例。
    /// </summary>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="texture">D3D11 纹理 SafeHandle（接管所有权）。</param>
    /// <param name="subresourceIndex">子资源索引（默认 0）。</param>
    public D3D11TextureResource(
        int width,
        int height,
        PixelFormat format,
        SafeD3D11TextureHandle texture,
        int subresourceIndex = 0)
    {
        Width = width;
        Height = height;
        Format = format;
        Texture = texture ?? throw new ArgumentNullException(nameof(texture));
        SubresourceIndex = subresourceIndex;
    }

    /// <summary>
    /// 释放 D3D11 纹理 COM 资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Texture.Dispose();
    }
}

using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.FFmpeg.Decoders;

/// <summary>
/// D3D11VA 硬件解码输出的 GPU 纹理帧资源。实现 <see cref="IGpuTextureResource"/>。
/// </summary>
/// <remarks>
/// <para><b>职责</b>：包装 FFmpeg D3D11VA 硬解输出的 <c>ID3D11Texture2D*</c> COM 指针，
/// 供 <c>D3D11Renderer</c> 直接 GPU 拷贝（零拷贝路径）。</para>
/// <para><b>引用计数</b>：构造时 <c>Marshal.AddRef</c>（FFmpeg 持有原始引用，我们额外 AddRef 以独立管理生命周期），
/// <see cref="Dispose"/> 时 <c>Marshal.Release</c>。</para>
/// <para><b>零拷贝链路</b>：FFmpeg D3D11VA 硬解 → D3D11HardwareFrameResource（COM AddRef）
/// → D3D11Renderer CopySubresourceRegion → BackBuffer → SwapChain → DirectComposition → Display。</para>
/// <para><b>ReadbackToCpu 限制</b>：硬解纹理不支持 CPU 回读（需要 Vortice 互操作，Backends.FFmpeg 无 Vortice 依赖）。
/// 硬解路径需配合 D3D11 渲染器使用；如需 Skia 渲染，请使用软件解码。</para>
/// <para><b>异步策略</b>：全部同步（sync/native 分类）——COM AddRef/Release 与 IntPtr 操作均为同步，无 I/O await。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，实现中立 <see cref="IGpuTextureResource"/> 契约。</para>
/// </remarks>
internal sealed class D3D11HardwareFrameResource : IGpuTextureResource
{
    private readonly IntPtr _texturePtr;
    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <inheritdoc/>
    public IntPtr NativeTextureHandle => _disposed ? IntPtr.Zero : _texturePtr;

    /// <inheritdoc/>
    public int SubresourceIndex { get; }

    /// <summary>
    /// 初始化 <see cref="D3D11HardwareFrameResource"/> 的新实例。
    /// </summary>
    /// <param name="texturePtr">FFmpeg D3D11VA 输出的 ID3D11Texture2D COM 指针（构造时 AddRef，Dispose 时 Release）。</param>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <param name="format">像素格式（通常为 NV12）。</param>
    /// <param name="subresourceIndex">纹理数组索引（来自 AVFrame->data[1]）。</param>
    internal D3D11HardwareFrameResource(IntPtr texturePtr, int width, int height, PixelFormat format, int subresourceIndex)
    {
        if (texturePtr == IntPtr.Zero)
            throw new ArgumentNullException(nameof(texturePtr));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "纹理尺寸必须为正数。");

        _texturePtr = texturePtr;
        Width = width;
        Height = height;
        Format = format;
        SubresourceIndex = subresourceIndex;

        // AddRef：FFmpeg 持有原始引用（随 AVFrame 释放），我们额外增加引用以独立管理生命周期
        Marshal.AddRef(texturePtr);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 硬解纹理不支持 CPU 回读（Backends.FFmpeg 无 Vortice 依赖）。
    /// 硬解路径需配合 D3D11 渲染器使用。
    /// </remarks>
    public GpuTextureReadback ReadbackToCpu()
        => throw new NotSupportedException(
            "D3D11VA 硬件解码纹理不支持 CPU 回读。请使用 D3D11 渲染器直接渲染 GPU 纹理，" +
            "或使用软件解码以获得 CPU 可读帧。");

    /// <summary>
    /// 释放 COM 纹理引用。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_texturePtr != IntPtr.Zero)
        {
            Marshal.Release(_texturePtr);
        }
    }
}

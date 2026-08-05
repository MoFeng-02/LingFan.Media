using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Backends.MediaFoundation.Decoders;

/// <summary>
/// MF DXVA 硬件解码输出的 GPU 纹理帧资源。实现 <see cref="IGpuTextureResource"/>（Abstractions 中立契约）。
/// </summary>
/// <remarks>
/// <para><b>职责</b>：包装 MF 硬解经 <c>IMFDXGIBuffer.GetResource</c> 取出的 <c>ID3D11Texture2D*</c> COM 指针，
/// 供 <c>D3D11Renderer</c> 经 <see cref="IGpuTextureResource"/> 接口直接 GPU 拷贝（零拷贝路径），无需引用本 MF 模块。</para>
/// <para><b>引用计数</b>：<c>GetResource</c> 内部已 <c>QueryInterface</c> 增加引用；本资源持有该引用，
/// <see cref="Dispose"/> 时 <c>Marshal.Release</c>。无需额外 AddRef（与 FFmpeg <c>D3D11HardwareFrameResource</c> 的
/// 重复 AddRef 不同——FFmpeg 保留自身引用，MF 的纹理引用由 GetResource 独立给出）。</para>
/// <para><b>零拷贝链路</b>：MF 硬件 MFT → IMFSample(DXGI 纹理) → MfD3D11TextureResource（IGpuTextureResource）
/// → D3D11Renderer CopySubresourceRegion → BackBuffer → SwapChain → 显示（有头）；
/// 或 → ProcessingFrameSink OnGpu（无头 GPU 回调）。</para>
/// <para><b>ReadbackToCpu 限制</b>：硬解纹理不支持 CPU 回读。硬解路径需配合 D3D11 渲染器使用；
/// 如需 Skia 渲染，请关闭硬件加速走软件解码。</para>
/// <para><b>异步策略</b>：全部同步（sync/native 分类）——COM Release 为同步原生调用，无 I/O await。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，实现中立 <see cref="IGpuTextureResource"/> 契约。</para>
/// </remarks>
internal sealed class MfD3D11TextureResource : IGpuTextureResource
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
    /// 初始化 <see cref="MfD3D11TextureResource"/> 的新实例。
    /// </summary>
    /// <param name="texturePtr">MF DXVA 输出经 <c>IMFDXGIBuffer.GetResource</c> 取出的 ID3D11Texture2D COM 指针（已 AddRef，Dispose 时 Release）。</param>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <param name="format">像素格式（DXVA 标准输出 NV12）。</param>
    /// <param name="subresourceIndex">纹理数组索引（DXVA 输出纹理数组切片）。</param>
    internal MfD3D11TextureResource(IntPtr texturePtr, int width, int height, PixelFormat format, int subresourceIndex)
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
    }

    /// <inheritdoc/>
    /// <remarks>硬解纹理不支持 CPU 回读（MF 后端无 Vortice 依赖）。硬解路径需配合 D3D11 渲染器使用。</remarks>
    public GpuTextureReadback ReadbackToCpu()
        => throw new NotSupportedException(
            "MF DXVA 硬件解码纹理不支持 CPU 回读。请使用 D3D11 渲染器直接渲染 GPU 纹理，" +
            "或关闭硬件加速以获得 CPU 可读帧。");

    /// <summary>
    /// 释放 COM 纹理引用（由 <c>GetResource</c> 取得的引用）。
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

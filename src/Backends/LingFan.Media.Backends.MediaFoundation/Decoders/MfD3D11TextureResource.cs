using System.Buffers;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using Vortice.Direct3D11;
using Vortice.DXGI;

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
/// <para><b>ReadbackToCpu（已实现）</b>：MF DXVA 硬解纹理经 Vortice 回读为 BGRA32，供 Skia 软渲染兜底路径消费
/// （打通「GPU 解码 + 控件内 WriteableBitmap 上屏」链路）。</para>
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
    /// <remarks>
    /// <para>MF DXVA 硬解纹理 CPU 回读（NV12→BGRA），供 Skia 软渲染兜底路径消费——打通
    /// 「GPU 解码 + 控件内 WriteableBitmap 上屏」链路，保持 GPU 解码不退回软件解码。</para>
    /// <para>本类持有纹理 COM 指针（GetResource 已 AddRef、Dispose Release），
    /// 故 Vortice 包装 <c>srcTexture</c> 与本实例共享同一指针、无 finalizer，<b>绝不 Dispose</b> 以免重复 Release。</para>
    /// </remarks>
    public GpuTextureReadback ReadbackToCpu()
    {
        // 回读唯一实现收敛于 GPUShare.D3D11.D3D11TextureReadback（staging + Map + NV12→BGRA）。
        // 纹理 COM 引用归本类所有（构造 AddRef / Dispose Release），共享实现不改变引用计数。
        return LingFan.Media.GPUShare.D3D11.Readback.D3D11TextureReadback.Read(_texturePtr, SubresourceIndex, Format);
    }

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

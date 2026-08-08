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
/// （打通「GPU 解码 + 控件内 WriteableBitmap 上屏」链路）。实现移植自
/// <c>LingFan.Media.Renderers.D3D11.D3D11TextureResource.ReadbackToCpu</c>（同一 DXVA 输出场景，已验证）。</para>
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
    /// <para>实现移植自 <c>LingFan.Media.Renderers.D3D11.D3D11TextureResource.ReadbackToCpu</c>
    /// （同一 D3D11VA/DXVA 输出纹理场景，已验证）。本类持有纹理 COM 指针（GetResource 已 AddRef、Dispose Release），
    /// 故 Vortice 包装 <c>srcTexture</c> 与本实例共享同一指针、无 finalizer，<b>绝不 Dispose</b> 以免重复 Release。</para>
    /// </remarks>
    public unsafe GpuTextureReadback ReadbackToCpu()
    {
        // 与 GetResource 取得的引用共享同一 COM 指针的 Vortice 包装：仅用于取设备/上下文，不 Dispose（避免重复 Release）。
        var srcTexture = new ID3D11Texture2D(_texturePtr);
        try
        {
            using var device = srcTexture.Device;
            using var context = device.ImmediateContext;

            // 关键互斥（KeyedMutex）纹理需 AcquireSync 取得访问权（DXVA 共享纹理常见）。
            IDXGIKeyedMutex? keyedMutex = null;
            try { keyedMutex = srcTexture.QueryInterface<IDXGIKeyedMutex>(); }
            catch (Exception) { keyedMutex = null; }

            bool acquired = false;
            if (keyedMutex != null)
            {
                try { keyedMutex.AcquireSync(0, unchecked((int)0xFFFFFFFF)); acquired = true; }
                catch (Exception) { acquired = false; }
            }

            try
            {
                return ReadbackCore(device, context, srcTexture, (uint)SubresourceIndex);
            }
            finally
            {
                if (acquired && keyedMutex != null) keyedMutex.ReleaseSync(0);
                keyedMutex?.Dispose();
            }
        }
        finally
        {
            // srcTexture 与 _texturePtr 共享同一 COM 指针且 Vortice 包装无 finalizer，不 Dispose，
            // 避免重复 Release（_texturePtr 的 Release 由本类 Dispose 负责）。
        }
    }

    private unsafe GpuTextureReadback ReadbackCore(
        ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D srcTexture, uint subresource)
    {
        var desc = srcTexture.Description;
        int w = (int)desc.Width;
        int h = (int)desc.Height;

        var stagingDesc = new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1u,
            ArraySize = 1u,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        using var staging = device.CreateTexture2D(stagingDesc);
        context.CopySubresourceRegion(staging, 0u, 0u, 0u, 0u, srcTexture, subresource, null);
        MappedSubresource mapped = context.Map(staging, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int destStride = w * 4;
            int rowPitch = (int)mapped.RowPitch;
            int uvStart = h * rowPitch;
            int total = (h + h / 2) * rowPitch;
            int dataLen = h * destStride;
            var outData = ArrayPool<byte>.Shared.Rent(dataLen);
            try
            {
                ReadOnlySpan<byte> src = new ReadOnlySpan<byte>(mapped.DataPointer.ToPointer(), total);

                switch (Format)
                {
                    case PixelFormat.BGRA32:
                        CopyBgraRows(src, outData, w, h, rowPitch, destStride, swapRB: false);
                        break;
                    case PixelFormat.RGBA32:
                        CopyBgraRows(src, outData, w, h, rowPitch, destStride, swapRB: true);
                        break;
                    case PixelFormat.NV12:
                        Nv12ToBgra(src, outData, w, h, rowPitch, uvStart, destStride);
                        break;
                    default:
                        throw new NotSupportedException(
                            $"GPU 纹理回退暂不支持像素格式 {Format}（仅 BGRA32/RGBA32/NV12）。");
                }

                return new GpuTextureReadback(w, h, PixelFormat.BGRA32, outData, destStride, dataLen);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(outData);
                throw;
            }
        }
        finally
        {
            context.Unmap(staging, 0u);
        }
    }

    private static unsafe void CopyBgraRows(
        ReadOnlySpan<byte> src, byte[] dest, int w, int h, int srcRowPitch, int destStride, bool swapRB)
    {
        fixed (byte* d = dest)
        {
            for (int y = 0; y < h; y++)
            {
                ReadOnlySpan<byte> srcRow = src.Slice(y * srcRowPitch, w * 4);
                byte* dstRow = d + y * destStride;
                if (!swapRB)
                {
                    srcRow.CopyTo(new Span<byte>(dstRow, w * 4));
                }
                else
                {
                    for (int x = 0; x < w; x++)
                    {
                        dstRow[x * 4 + 0] = srcRow[x * 4 + 2];
                        dstRow[x * 4 + 1] = srcRow[x * 4 + 1];
                        dstRow[x * 4 + 2] = srcRow[x * 4 + 0];
                        dstRow[x * 4 + 3] = srcRow[x * 4 + 3];
                    }
                }
            }
        }
    }

    private static unsafe void Nv12ToBgra(
        ReadOnlySpan<byte> src, byte[] dest, int w, int h, int rowPitch, int uvStart, int destStride)
    {
        fixed (byte* d = dest)
        {
            for (int y = 0; y < h; y++)
            {
                byte* dstRow = d + y * destStride;
                int yBase = y * rowPitch;
                int uvRowBase = uvStart + (y >> 1) * rowPitch;
                for (int x = 0; x < w; x++)
                {
                    int yv = src[yBase + x];
                    int cCol = x >> 1;
                    int cu = src[uvRowBase + cCol * 2];
                    int cv = src[uvRowBase + cCol * 2 + 1];

                    int r = yv + (int)(1.402f * (cv - 128));
                    int g = yv + (int)(-0.344136f * (cu - 128) - 0.714136f * (cv - 128));
                    int b = yv + (int)(1.772f * (cu - 128));
                    r = r < 0 ? 0 : r > 255 ? 255 : r;
                    g = g < 0 ? 0 : g > 255 ? 255 : g;
                    b = b < 0 ? 0 : b > 255 ? 255 : b;

                    dstRow[x * 4 + 0] = (byte)b;
                    dstRow[x * 4 + 1] = (byte)g;
                    dstRow[x * 4 + 2] = (byte)r;
                    dstRow[x * 4 + 3] = 255;
                }
            }
        }
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

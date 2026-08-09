using System;
using LingFan.Media.Renderers.D3D11.SafeHandles;
using Vortice.DXGI;

namespace LingFan.Media.Renderers.D3D11;

/// <summary>
/// Direct3D 11 纹理帧资源。实现 <see cref="IFrameResource"/>。
/// </summary>
/// <remarks>
/// <para>用于 DXVA 硬件解码路径——FFmpeg DXVA 解码输出 ID3D11Texture2D COM 指针，
/// 由 <see cref="SafeD3D11TextureHandle"/> 管理生命周期。</para>
/// <para>D3D11TextureResource 为最小实现，Present 路径以 SoftwareFrameResource 为主。
/// 启用 DXVA 零拷贝路径后由 FFmpeg 后端创建实例。</para>
/// <para>AOT 兼容：sealed 类，IFrameResource 多态 + pattern matching。</para>
/// </remarks>
internal sealed class D3D11TextureResource : IGpuTextureResource
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

    /// <inheritdoc/>
    /// <remarks>返回 SafeHandle 内部 COM 指针（不增加引用计数，调用方须确保使用期间资源不被释放）。</remarks>
    IntPtr IGpuTextureResource.NativeTextureHandle => Texture.DangerousGetHandle();

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

    /// <inheritdoc/>
    public GpuTextureReadback ReadbackToCpu()
    {
        // 经 SafeHandle 获取原生 COM 指针，构造 Vortice 包装（与 SafeHandle 共享同一指针）。
        // Vortice 包装无 finalizer，不 Dispose 以免重复 Release（SafeHandle 持有引用）。
        bool success = false;
        Texture.DangerousAddRef(ref success);
        try
        {
            IntPtr ptr = Texture.DangerousGetHandle();
            var srcTexture = new ID3D11Texture2D(ptr);
            try
            {
                // 从纹理自身解析其所属设备（ID3D11DeviceChild.Device 经 GetDevice 取回父设备），
                // 避免跨模块耦合；纹理本身不实现 ID3D11Device，故不可用 QueryInterface。
                using var device = srcTexture.Device;
                // ImmediateContext 返回 AddRef 过的 COM 引用，
                // 须 Dispose 释放（与 D3D11RendererFactory 一致），否则 COM 引用泄漏到 GC finalizer。
                // using var 保证 context 在 device 之前 Dispose（声明逆序释放）。
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
                // srcTexture 与 SafeHandle 共享同一 COM 指针且 Vortice 包装无 finalizer，
                // 不 Dispose，避免重复 Release（SafeHandle 持有引用）。
            }
        }
        finally
        {
            if (success) Texture.DangerousRelease();
        }
    }

    private unsafe GpuTextureReadback ReadbackCore(
        ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D srcTexture, uint subresource)
    {
        var desc = srcTexture.Description;
        int w = (int)desc.Width;
        int h = (int)desc.Height;

        // 暂存纹理（同格式，CPU 可读），用于拷贝后 Map 读取。
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

        var staging = device.CreateTexture2D(stagingDesc);
        try
        {
            context.CopySubresourceRegion(staging, 0u, 0u, 0u, 0u, srcTexture, subresource, null);
            MappedSubresource mapped = context.Map(staging, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int destStride = w * 4;
                int rowPitch = (int)mapped.RowPitch;
                int uvStart = h * rowPitch;
                int total = (h + h / 2) * rowPitch;
                // B-CTR2: ArrayPool 租借替代每帧 new byte[]（1080p BGRA ≈ 8MB/帧，
                // 30fps 下每秒 240MB LOH 分配）。GpuTextureReadback 池化构造接管归还责任，
                // 消费方 using Dispose 时自动 Return。
                int dataLen = h * destStride;
                var outData = System.Buffers.ArrayPool<byte>.Shared.Rent(dataLen);
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
                    // 转换失败时归还租借数组（成功路径的归还责任已移交 GpuTextureReadback）
                    System.Buffers.ArrayPool<byte>.Shared.Return(outData);
                    throw;
                }
            }
            finally
            {
                context.Unmap(staging, 0u);
            }
        }
        finally
        {
            staging.Dispose();
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
                        // RGBA32 → BGRA32：通道位置交换（值随位置走）
                        // 源字节序 R,G,B,A（src[0]=R, src[2]=B）；目标字节序 B,G,R,A（dst[0]=B, dst[2]=R）
                        dstRow[x * 4 + 0] = srcRow[x * 4 + 2]; // B ← 源 B（字节2）
                        dstRow[x * 4 + 1] = srcRow[x * 4 + 1]; // G
                        dstRow[x * 4 + 2] = srcRow[x * 4 + 0]; // R ← 源 R（字节0）
                        dstRow[x * 4 + 3] = srcRow[x * 4 + 3]; // A
                    }
                }
            }
        }
    }

    private static unsafe void Nv12ToBgra(
        ReadOnlySpan<byte> src, byte[] dest, int w, int h, int rowPitch, int uvStart, int destStride)
    {
        // BT.601 全范围（与 SkiaVideoPresenter U11 CPU 路径一致，避免色彩漂移）。
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
                    int cCol = x >> 1; // 水平 2x 色度子采样
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
}

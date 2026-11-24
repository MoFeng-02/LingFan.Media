using System.Buffers;
using LingFan.Media.Abstractions;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.GPUShare.D3D11.Readback;

/// <summary>
/// D3D11 纹理 CPU 回读（staging 拷贝 + Map + 像素格式转 BGRA32）——全仓唯一实现。
/// </summary>
/// <remarks>
/// <para>消费方：FFmpeg D3D11VA / MF DXVA 硬解帧资源与 GL 跨 API 互操作纹理的
/// <c>ReadbackToCpu</c>（Skia 软渲染兜底路径）。纹理 COM 引用的 AddRef/Release 归调用方所有，
/// 本类<b>不改变引用计数</b>：内部 Vortice 包装与调用方共享同一指针、无 finalizer，绝不 Dispose。</para>
/// <para>KeyedMutex 纹理（DXVA 共享纹理常见）自动 AcquireSync/ReleaseSync；无互斥接口的纹理直接回读。</para>
/// <para>支持像素格式：BGRA32（直拷）/ RGBA32（通道交换）/ NV12（BT.601 全范围转 BGRA，
/// 系数唯一真源 <see cref="Bt601FullRange"/>）；其余格式抛 <see cref="NotSupportedException"/>。</para>
/// <para>异步策略：全部同步（sync/native 分类）——GPU 拷贝与 Map 为同步原生调用，无 I/O await。</para>
/// <para>AOT 兼容：sealed 静态类，Vortice 源生成 COM 封送，零反射。</para>
/// </remarks>
public static class D3D11TextureReadback
{
    /// <summary>
    /// 回读指定纹理子资源为 BGRA32 像素。
    /// </summary>
    /// <param name="texturePtr">ID3D11Texture2D COM 指针（调用方持有引用，本方法不 AddRef/Release）。</param>
    /// <param name="subresource">纹理数组子资源索引（单纹理为 0）。</param>
    /// <param name="format">源像素格式（BGRA32/RGBA32/NV12）。</param>
    /// <returns>BGRA32 回读结果（数组租借自 ArrayPool，归还责任随结果转移）。</returns>
    /// <exception cref="NotSupportedException">格式不在支持范围。</exception>
    public static unsafe GpuTextureReadback Read(IntPtr texturePtr, int subresource, PixelFormat format)
    {
        // 与调用方共享同一 COM 指针的 Vortice 包装：仅用于取设备/上下文，不 Dispose（避免重复 Release）。
        var srcTexture = new ID3D11Texture2D(texturePtr);
        using var device = srcTexture.Device;
        // ImmediateContext 返回 AddRef 过的 COM 引用，须 Dispose 释放。
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
            return ReadbackCore(device, context, srcTexture, (uint)subresource, format);
        }
        finally
        {
            if (acquired && keyedMutex != null) keyedMutex.ReleaseSync(0);
            keyedMutex?.Dispose();
        }
    }

    private static unsafe GpuTextureReadback ReadbackCore(
        ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D srcTexture,
        uint subresource, PixelFormat format)
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

                switch (format)
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
                            $"GPU 纹理回退暂不支持像素格式 {format}（仅 BGRA32/RGBA32/NV12）。");
                }

                return new GpuTextureReadback(w, h, PixelFormat.BGRA32, outData, destStride, dataLen);
            }
            catch
            {
                // 转换失败时归还租借数组（成功路径的归还责任已移交 GpuTextureReadback）
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
                        // RGBA32 → BGRA32：通道位置交换
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
        // BT.601 全范围（系数唯一真源 Bt601FullRange，与着色器/Skia CPU 路径一致，避免色彩漂移）。
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
                    int cu = src[uvRowBase + cCol * 2] - Bt601FullRange.ChromaCenter;
                    int cv = src[uvRowBase + cCol * 2 + 1] - Bt601FullRange.ChromaCenter;

                    int r = yv + (int)((float)Bt601FullRange.Rv * cv);
                    int g = yv + (int)((float)-Bt601FullRange.Gu * cu - (float)Bt601FullRange.Gv * cv);
                    int b = yv + (int)((float)Bt601FullRange.Bu * cu);
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
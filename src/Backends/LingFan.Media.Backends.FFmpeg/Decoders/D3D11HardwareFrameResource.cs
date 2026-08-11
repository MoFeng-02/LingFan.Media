using System.Buffers;
using System.Runtime.InteropServices;
using LingFan.Media.Backends.FFmpeg.SafeHandles;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.Backends.FFmpeg.Decoders;

/// <summary>
/// D3D11VA 硬件解码输出的 GPU 纹理帧资源。实现 <see cref="IGpuTextureResource"/>。
/// </summary>
/// <remarks>
/// <para><b>职责</b>：包装 FFmpeg D3D11VA 硬解输出的 <c>ID3D11Texture2D*</c> COM 指针，
/// 供 <c>D3D11Renderer</c> 直接 GPU 拷贝（零拷贝路径）。</para>
/// <para><b>切片所有权（解决「画面抽帧 + 跳场景」）</b>：D3D11VA 的
/// <c>AVHWFramesContext</c> 池语义是「少量 <c>ID3D11Texture2D</c> 纹理<b>数组</b>对象 + 每帧占用其中一个
/// array slice」。<c>data[0]</c> 是数组对象、<c>data[1]</c> 是切片索引，而<b>切片的占用权归
/// <c>AVFrame->buf[0]</c></b>（指向池内 buffer），<b>不</b>归纹理对象的 COM 引用计数。
/// 因此仅 <c>Marshal.AddRef(texture)</c> 只能保证「数组对象不被销毁」，
/// <b>完全阻止不了解码器把该切片分配给下一帧</b>。
/// 旧实现在 <c>CreateHardwareFrameFromAVFrame</c> 返回后立即 <c>av_frame_free</c>，切片当场回池 ⇒
/// 渲染线程稍后 <c>CopySubresourceRegion(texture, slice)</c> 拷到的是<b>几帧之后</b>的图像或半写入状态 ⇒
/// 画面「卡住数秒（切片暂未复用）→ 突然跳到别的场景（切片被复用）」+ 撕裂 + 驱动状态错乱崩溃。
/// 修法：持有 <c>av_frame_clone</c> 出的 AVFrame（对 <c>buf[0]</c> 引用计数 +1），
/// <see cref="Dispose"/> 时才释放 ⇒ 切片在渲染完成前绝不回池。这与软解 BGRA 路径
/// （<c>TryCreateZeroCopyResource</c>）和 MediaCodec 表面路径的既有做法一致。</para>
/// <para><b>引用计数</b>：构造时 <c>Marshal.AddRef</c> 纹理对象 + 持有克隆 AVFrame 引用；
/// <see cref="Dispose"/> 时 <c>Marshal.Release</c> + <c>av_frame_free</c>（顺序：先放帧引用再放纹理引用，
/// 保证纹理对象在切片回池期间仍然存活）。</para>
/// <para><b>零拷贝链路</b>：FFmpeg D3D11VA 硬解 → D3D11HardwareFrameResource（切片保活）
/// → D3D11Renderer CopySubresourceRegion → BackBuffer → SwapChain → DirectComposition → Display。</para>
/// <para><b>ReadbackToCpu（已实现）</b>：D3D11VA 硬解纹理经 Vortice 回读为 BGRA32，供 Skia 软渲染兜底路径消费
/// （打通「GPU 解码 + 控件内 WriteableBitmap 上屏」链路）。实现移植自
/// <c>LingFan.Media.Renderers.D3D11.D3D11TextureResource.ReadbackToCpu</c>（同一 D3D11VA 输出场景，已验证）。</para>
/// <para><b>异步策略</b>：全部同步（sync/native 分类）——COM AddRef/Release 与 IntPtr 操作均为同步，无 I/O await。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，实现中立 <see cref="IGpuTextureResource"/> 契约。</para>
/// </remarks>
internal sealed class D3D11HardwareFrameResource : IGpuTextureResource
{
    private readonly IntPtr _texturePtr;

    /// <summary>
    /// 克隆的 AVFrame，持有 D3D11VA 池内切片的引用计数，保证切片在本资源存活期间不被解码器复用。
    /// </summary>
    private readonly SafeAVFrameHandle? _frameOwner;

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
    /// <param name="frameOwner">
    /// <c>av_frame_clone</c> 出的 AVFrame 句柄，持有池内切片引用计数（<b>必传</b>，否则切片会被解码器提前复用）。
    /// 所有权转移给本实例，<see cref="Dispose"/> 时释放。
    /// </param>
    internal D3D11HardwareFrameResource(
        IntPtr texturePtr,
        int width,
        int height,
        PixelFormat format,
        int subresourceIndex,
        SafeAVFrameHandle frameOwner)
    {
        if (texturePtr == IntPtr.Zero)
        {
            frameOwner?.Dispose();
            throw new ArgumentNullException(nameof(texturePtr));
        }
        if (width <= 0 || height <= 0)
        {
            frameOwner?.Dispose();
            throw new ArgumentOutOfRangeException(nameof(width), "纹理尺寸必须为正数。");
        }
        ArgumentNullException.ThrowIfNull(frameOwner);

        _texturePtr = texturePtr;
        Width = width;
        Height = height;
        Format = format;
        SubresourceIndex = subresourceIndex;
        _frameOwner = frameOwner;

        // AddRef：FFmpeg 持有原始引用（随 AVFrame 释放），我们额外增加引用以独立管理纹理对象生命周期。
        // 这一份引用只保「纹理数组对象」不销毁；切片占用权由 _frameOwner 保障，二者缺一不可。
        Marshal.AddRef(texturePtr);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>D3D11VA 硬解纹理 CPU 回读（NV12→BGRA），供 Skia 软渲染兜底路径消费——打通
    /// 「GPU 解码 + 控件内 WriteableBitmap 上屏」链路，保持 GPU 解码不退回软件解码。</para>
    /// <para>实现移植自 <c>LingFan.Media.Renderers.D3D11.D3D11TextureResource.ReadbackToCpu</c>
    /// （同一 D3D11VA 输出纹理场景，已验证）；本类仅持有纹理 COM 指针（构造 AddRef、Dispose Release），
    /// 故 Vortice 包装 <c>srcTexture</c> 与本实例共享同一指针、无 finalizer，<b>绝不 Dispose</b> 以免重复 Release。</para>
    /// </remarks>
    public unsafe GpuTextureReadback ReadbackToCpu()
    {
        // 与 SafeHandle 共享同一 COM 指针的 Vortice 包装：仅用于取设备/上下文，不 Dispose（避免重复 Release）。
        // 完全限定：FFmpeg.AutoGen 同样导出 ID3D11Texture2D，须显式指向 Vortice 版本。
        var srcTexture = new Vortice.Direct3D11.ID3D11Texture2D(_texturePtr);
        try
        {
            // 从纹理自身解析其所属设备（ID3D11DeviceChild.Device 经 GetDevice 取回父设备），避免跨模块耦合。
            using var device = srcTexture.Device;
            // ImmediateContext 返回 AddRef 过的 COM 引用，须 Dispose 释放（与 D3D11RendererFactory 一致）。
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
        Vortice.Direct3D11.ID3D11Device device,
        Vortice.Direct3D11.ID3D11DeviceContext context,
        Vortice.Direct3D11.ID3D11Texture2D srcTexture,
        uint subresource)
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
                        dstRow[x * 4 + 0] = srcRow[x * 4 + 2]; // B ← 源 B
                        dstRow[x * 4 + 1] = srcRow[x * 4 + 1]; // G
                        dstRow[x * 4 + 2] = srcRow[x * 4 + 0]; // R ← 源 R
                        dstRow[x * 4 + 3] = srcRow[x * 4 + 3]; // A
                    }
                }
            }
        }
    }

    private static unsafe void Nv12ToBgra(
        ReadOnlySpan<byte> src, byte[] dest, int w, int h, int rowPitch, int uvStart, int destStride)
    {
        // BT.601 全范围（与 SkiaVideoPresenter CPU 路径一致，避免色彩漂移）。
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

    /// <summary>
    /// 释放池内切片引用与 COM 纹理引用。
    /// </summary>
    /// <remarks>
    /// 顺序固定：<b>先</b>释放 AVFrame 克隆（切片回池，可被解码器复用）、<b>后</b> Release 纹理对象，
    /// 确保切片归还发生在纹理数组对象仍然存活的窗口内，避免在已销毁对象上操作。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 切片保活引用：归还后解码器方可复用该 slice
        _frameOwner?.Dispose();

        if (_texturePtr != IntPtr.Zero)
        {
            Marshal.Release(_texturePtr);
        }
    }
}

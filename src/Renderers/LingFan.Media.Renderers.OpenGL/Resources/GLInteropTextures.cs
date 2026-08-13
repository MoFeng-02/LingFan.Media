using LingFan.Media.Abstractions;
using System.Buffers;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// 跨 API 零拷贝 GL 纹理帧资源（Windows：WGL_NV_DX_interop2 导入的 D3D11 共享纹理）。
/// </summary>
/// <remarks>
/// <para>由 <see cref="OpenGLGpuFrameProducer"/> 经 WGL_NV_DX_interop2 把 D3D11 共享纹理注册为 GL 纹理后构造，
/// 经中立 <see cref="IGpuTextureResource"/> 交由 <see cref="OpenGLShaderPipeline.PresentGpuTexture"/> 直接采样（零拷贝）。</para>
/// <para><b>生命周期</b>：Dispose 须在当前 GL 上下文（共享组）下完成——先 <c>wglDXUnregisterObjectNV</c> 解除 D3D 绑定，
/// 再 <c>glDeleteTextures</c>，最后释放每帧的 D3D11 共享纹理引用。桥接 D3D11 设备与 WGL 互操作句柄由生产者持有，
/// 此处不释放（避免与在途帧竞争）。</para>
/// <para><b>线程安全</b>：GL 上下文具线程亲和，Dispose 经 <see cref="OpenGLOffscreenDeviceContext"/> 绑定/解绑，
/// 与渲染线程交替安全（同 <see cref="IGlContext"/> 约定）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed unsafe class GLD3D11InteropTexture : IFrameResource, IGpuTextureResource
{
    private readonly uint _textureId;
    private readonly int _subresourceIndex;
    private readonly nint _interopDevice;
    private readonly nint _interopObject;   // wglDXRegisterObjectNV 返回的对象句柄（≠ GL 纹理 ID）
    private readonly ID3D11Texture2D _d3dTexture;
    private readonly OpenGLOffscreenDeviceContext _glContext;
    private readonly object _lock = new();
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public PixelFormat Format { get; }

    /// <summary>初始化 <see cref="GLD3D11InteropTexture"/> 的新实例。</summary>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="textureId">GL 纹理 ID（已与 D3D11 共享纹理注册）。</param>
    /// <param name="interopDevice">WGL_NV_DX_interop2 互操作句柄（生产者持有，本类仅引用）。</param>
    /// <param name="interopObject">wglDXRegisterObjectNV 返回的对象句柄（unregister / lock / unlock 须用此，非 GL 纹理 ID）。</param>
    /// <param name="d3dTexture">每帧 D3D11 共享纹理（本类拥有，Dispose 释放）。</param>
    /// <param name="glContext">共享设备上下文（unregister / 释放 GL 纹理所需）。</param>
    /// <param name="subresourceIndex">子资源索引（默认 0）。</param>
    public GLD3D11InteropTexture(
        int width, int height, PixelFormat format,
        uint textureId, nint interopDevice, nint interopObject, ID3D11Texture2D d3dTexture,
        OpenGLOffscreenDeviceContext glContext, int subresourceIndex = 0)
    {
        Width = width;
        Height = height;
        Format = format;
        _textureId = textureId;
        _interopDevice = interopDevice;
        _interopObject = interopObject;
        _d3dTexture = d3dTexture;
        _glContext = glContext;
        _subresourceIndex = subresourceIndex;
    }

    IntPtr IGpuTextureResource.NativeTextureHandle => (IntPtr)_textureId;

    int IGpuTextureResource.SubresourceIndex => _subresourceIndex;

    /// <summary>采样前获取 D3D 资源访问权（WGL_NV_DX_interop2 强制栅栏：防止解码侧写入与 GL 读取竞态/撕裂）。</summary>
    /// <remarks>须在当前 GL 上下文（与注册时同共享组）下调用；<see cref="OpenGLShaderPipeline.PresentGpuTexture"/> 绑定+绘制前调用。</remarks>
    internal void AcquireForRendering()
    {
        if (_interopObject == nint.Zero) return;
        nint obj = _interopObject;
        GLNative.WglDXLockObjectsNV(_interopDevice, 1, &obj);
    }

    /// <summary>采样后释放 D3D 资源访问权，交还解码侧写入（与 <see cref="AcquireForRendering"/> 配对）。</summary>
    internal void ReleaseForRendering()
    {
        if (_interopObject == nint.Zero) return;
        nint obj = _interopObject;
        GLNative.WglDXUnlockObjectsNV(_interopDevice, 1, &obj);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _glContext.EnsureCreated();
        _glContext.MakeCurrent();
        try
        {
            // 先解除 D3D 绑定（WGL 互操作对象句柄），再删 GL 纹理，次序不可反（否则未定义行为）。
            if (_interopObject != nint.Zero && GLNative.IsWglDxInteropAvailable())
                GLNative.WglDXUnregisterObjectNV(_interopDevice, _interopObject);
            uint tex = _textureId;
            GLNative.glDeleteTextures(1, &tex);
        }
        finally
        {
            _glContext.ReleaseCurrent();
        }

        _d3dTexture.Dispose();
    }

    /// <inheritdoc/>
    public unsafe GpuTextureReadback ReadbackToCpu()
    {
        // 经底层 D3D11 共享纹理回读 NV12 → BGRA32，供 Skia 软渲染兜底路径消费。
        // 与 MfD3D11TextureResource.ReadbackToCpu 同源：本类拥有 _d3dTexture，但此处仅取设备/上下文，不 Dispose（避免重复 Release）。
        var srcTexture = new ID3D11Texture2D(_d3dTexture.NativePointer);
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
                return ReadbackCore(device, context, srcTexture, (uint)_subresourceIndex);
            }
            finally
            {
                if (acquired && keyedMutex != null) keyedMutex.ReleaseSync(0);
                keyedMutex?.Dispose();
            }
        }
        finally
        {
            // srcTexture 与 _d3dTexture 共享同一 COM 指针且无 finalizer，不 Dispose，避免重复 Release
            // （_d3dTexture 的 Release 由本类 Dispose 负责）。
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
                            $"GL 跨 API 纹理回退暂不支持像素格式 {Format}（仅 BGRA32/RGBA32/NV12）。");
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
}

/// <summary>
/// 跨 API 零拷贝 GL 纹理帧资源（Linux：EGL_EXT_image_dma_buf_import 导入的 VAAPI dma_buf）。
/// </summary>
/// <remarks>
/// <para>由 <see cref="OpenGLGpuFrameProducer"/> 经 <c>eglCreateImageKHR</c> + <c>glEGLImageTargetTexture2DOES</c>
/// 把 VAAPI dma_buf 绑定为 GL 纹理后构造，经中立 <see cref="IGpuTextureResource"/> 交由渲染器直接采样（零拷贝）。</para>
/// <para><b>生命周期</b>：Dispose 在当前 EGL/GL 上下文下——先 <c>glDeleteTextures</c>，再 <c>eglDestroyImageKHR</c>；
/// EGLDisplay 由生产者（离屏共享组所有者）持有，此处不释放。</para>
/// <para><b>状态</b>：解码侧 VAAPI→EGL 导入为未来端点（见零拷贝架构铁律），当前仅作结构就绪；调用方（解码器）尚不产出
/// <see cref="GpuFrameImportKind.VaApiDmaBuf"/> 时本类不会被实例化，可用性探测失败即回落软解（S_OK≠被接受）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed unsafe class GLEglDmaBufTexture : IFrameResource, IGpuTextureResource
{
    private readonly uint _textureId;
    private readonly int _subresourceIndex;
    private readonly nint _eglDisplay;
    private readonly nint _eglImage;
    private readonly OpenGLOffscreenDeviceContext _glContext;
    private readonly object _lock = new();
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public PixelFormat Format { get; }

    /// <summary>初始化 <see cref="GLEglDmaBufTexture"/> 的新实例。</summary>
    public GLEglDmaBufTexture(
        int width, int height, PixelFormat format,
        uint textureId, nint eglDisplay, nint eglImage,
        OpenGLOffscreenDeviceContext glContext, int subresourceIndex = 0)
    {
        Width = width;
        Height = height;
        Format = format;
        _textureId = textureId;
        _eglDisplay = eglDisplay;
        _eglImage = eglImage;
        _glContext = glContext;
        _subresourceIndex = subresourceIndex;
    }

    IntPtr IGpuTextureResource.NativeTextureHandle => (IntPtr)_textureId;

    int IGpuTextureResource.SubresourceIndex => _subresourceIndex;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">解码侧 VAAPI→EGL 导入为未来端点（见零拷贝架构铁律），
    /// 当前调用方（解码器）尚不产出 <see cref="GpuFrameImportKind.VaApiDmaBuf"/>，本类不会被实例化；
    /// 故 CPU 回读为显式未支持路径，非静默假绿。</exception>
    public GpuTextureReadback ReadbackToCpu()
        => throw new NotSupportedException(
            "GLEglDmaBufTexture.ReadbackToCpu 为未来端点：解码侧 VAAPI→EGL 导入尚未启用（见零拷贝架构铁律）。");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _glContext.EnsureCreated();
        _glContext.MakeCurrent();
        try
        {
            uint tex = _textureId;
            GLNative.glDeleteTextures(1, &tex);
        }
        finally
        {
            _glContext.ReleaseCurrent();
        }

        if (_eglImage != nint.Zero)
            GLNative.EglDestroyImageKHR(_eglDisplay, _eglImage);
    }
}

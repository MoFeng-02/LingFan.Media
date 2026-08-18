using LingFan.Media.Abstractions;
using System.Buffers;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// 跨 API 零拷贝 GL 纹理帧资源（Windows：WGL_NV_DX_interop2 待导入的 D3D11 共享纹理）。
/// </summary>
/// <remarks>
/// <para>本类本身<b>不</b>持有已注册的 GL 纹理，而是把解码侧打开的每帧 D3D11 共享纹理引用 + 桥接 D3D11 设备
/// 交给 <see cref="OpenGLShaderPipeline.PresentGpuTexture"/>；管线在绘制时于当前 on-screen GL 上下文上执行
/// <c>wglDXOpenDeviceNV / wglDXRegisterObjectNV / wglDXLockObjectsNV</c>，绘制后立即 unregister/delete/close。
/// 这样避免 owner 离屏上下文与 on-screen 渲染上下文对同一 WGL 互操作对象的跨上下文歧义（共享组虽可共享纹理名，
/// 但 WGL 互操作对象由 NVIDIA 实现强关联注册上下文；在 on-screen 上下文上重注册是可靠做法）。</para>
/// <para><b>生命周期</b>：Dispose 仅释放本类拥有的每帧 D3D11 共享纹理引用；GL/WGL 资源由管线在单帧内用完即释。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed unsafe class GLD3D11InteropTexture : IFrameResource, IGpuTextureResource
{
    private readonly ID3D11Texture2D _d3dTexture;
    private readonly ID3D11Device _bridgeDevice;
    private readonly OpenGLOffscreenDeviceContext _glContext;
    private readonly int _subresourceIndex;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public PixelFormat Format { get; }

    /// <summary>用于在 on-screen 上下文上打开 WGL 互操作设备的桥接 D3D11 设备。</summary>
    internal ID3D11Device BridgeDevice => _bridgeDevice;

    /// <summary>解码侧经 <c>OpenSharedResource1</c> 打开的每帧 D3D11 共享纹理。</summary>
    internal ID3D11Texture2D D3dTexture => _d3dTexture;

    /// <summary>初始化 <see cref="GLD3D11InteropTexture"/> 的新实例。</summary>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="d3dTexture">每帧 D3D11 共享纹理（本类拥有，Dispose 释放）。</param>
    /// <param name="bridgeDevice">桥接 D3D11 设备（用于管线在 on-screen 上下文打开 WGL 互操作设备）。</param>
    /// <param name="glContext">共享组所有者离屏上下文（Linux EGL / 未来回读路径所需）。</param>
    /// <param name="subresourceIndex">子资源索引（默认 0）。</param>
    public GLD3D11InteropTexture(
        int width, int height, PixelFormat format,
        ID3D11Texture2D d3dTexture, ID3D11Device bridgeDevice,
        OpenGLOffscreenDeviceContext glContext, int subresourceIndex = 0)
    {
        Width = width;
        Height = height;
        Format = format;
        _d3dTexture = d3dTexture;
        _bridgeDevice = bridgeDevice;
        _glContext = glContext;
        _subresourceIndex = subresourceIndex;
    }

    /// <summary>无已注册 GL 纹理；管线在绘制时现场注册，不依赖此句柄。</summary>
    IntPtr IGpuTextureResource.NativeTextureHandle => IntPtr.Zero;

    int IGpuTextureResource.SubresourceIndex => _subresourceIndex;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
/// <para><b>状态</b>：单平面变体（旧路径）；当前 NV12 由 <see cref="GLDmaBufNv12Texture"/> 双平面实现替代。
/// 解码侧现已产出 <see cref="GpuFrameImportKind.LinuxDmaBufFd"/>，可用性探测失败即回落软解（S_OK≠被接受）。</para>
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
    /// 当前调用方（解码器）尚不产出 <see cref="GpuFrameImportKind.LinuxDmaBufFd"/>，本类不会被实例化；
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
        lock (_glContext.GlAccessLock)
        {
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
        }

        if (_eglImage != nint.Zero)
            GLNative.EglDestroyImageKHR(_eglDisplay, _eglImage);
    }
}

/// <summary>
/// 跨 API 零拷贝 GL 纹理帧资源（Linux：EGL_EXT_image_dma_buf_import 导入的 VAAPI NV12 dma_buf，双平面）。
/// </summary>
/// <remarks>
/// <para>由 <see cref="OpenGLGpuFrameProducer"/> 把 composed NV12 dma_buf 拆为 Y(R8) / UV(GR88) 两个 EGLImage
/// 并各自绑为 GL 纹理，经中立 <see cref="IGpuTextureResource"/> 交由渲染器用 NV12 shader 采样（零拷贝）。</para>
/// <para><b>生命周期</b>：Dispose 在当前 EGL/GL 上下文下——先 <c>glDeleteTextures</c>（两纹理），再
/// <c>eglDestroyImageKHR</c>（两 EGLImage）；EGLDisplay 由生产者（离屏共享组所有者）持有，此处不释放。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed unsafe class GLDmaBufNv12Texture : IFrameResource, IGpuTextureResource
{
    private readonly uint _yTexture;
    private readonly uint _uvTexture;
    private readonly nint _eglDisplay;
    private readonly nint _eglImageY;
    private readonly nint _eglImageUV;
    private readonly OpenGLOffscreenDeviceContext _glContext;
    private readonly object _lock = new();
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public PixelFormat Format { get; }

    /// <summary>Y 平面 GL 纹理（R8）。</summary>
    public uint YTexture => _yTexture;
    /// <summary>UV 平面 GL 纹理（GR88）。</summary>
    public uint UVTexture => _uvTexture;

    /// <summary>初始化 <see cref="GLDmaBufNv12Texture"/> 的新实例。</summary>
    public GLDmaBufNv12Texture(
        int width, int height, uint yTexture, uint uvTexture,
        nint eglDisplay, nint eglImageY, nint eglImageUV,
        OpenGLOffscreenDeviceContext glContext, int subresourceIndex = 0)
    {
        Width = width;
        Height = height;
        Format = PixelFormat.NV12;
        _yTexture = yTexture;
        _uvTexture = uvTexture;
        _eglDisplay = eglDisplay;
        _eglImageY = eglImageY;
        _eglImageUV = eglImageUV;
        _glContext = glContext;
    }

    IntPtr IGpuTextureResource.NativeTextureHandle => (IntPtr)_yTexture;

    int IGpuTextureResource.SubresourceIndex => 0;

    /// <inheritdoc/>
    public GpuTextureReadback ReadbackToCpu()
        => throw new NotSupportedException(
            "GLDmaBufNv12Texture.ReadbackToCpu 为未来端点：VAAPI→EGL 双平面 NV12 导入已启用，CPU 回读暂未实现。");

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
        lock (_glContext.GlAccessLock)
        {
            _glContext.MakeCurrent();
            try
            {
                uint y = _yTexture, uv = _uvTexture;
                GLNative.glDeleteTextures(1, &y);
                GLNative.glDeleteTextures(1, &uv);
            }
            finally
            {
                _glContext.ReleaseCurrent();
            }
        }

        if (_eglImageY != nint.Zero) GLNative.EglDestroyImageKHR(_eglDisplay, _eglImageY);
        if (_eglImageUV != nint.Zero) GLNative.EglDestroyImageKHR(_eglDisplay, _eglImageUV);
    }
}

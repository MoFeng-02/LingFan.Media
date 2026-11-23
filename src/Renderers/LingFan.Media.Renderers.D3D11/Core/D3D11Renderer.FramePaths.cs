using System.Buffers;
using LingFan.Media.Renderers.D3D11.DirectComposition;
using LingFan.Media.Renderers.D3D11.Shaders;

namespace LingFan.Media.Renderers.D3D11;

// D3D11Renderer 的帧呈现路径职责段（部分类，按职责拆分，编译期等价）。
// 软帧 staging 上传 / GPU 纹理零拷贝 / NV12 与 BGRA Shader 缩放路径；Attach/Present 分派/Dispose 在主文件。
internal sealed partial class D3D11Renderer
{
    /// <summary>
    /// 软件帧渲染路径：CPU 数据 → D3D11 Texture → CopyResource → BackBuffer。
    /// </summary>
    private void PresentSoftwareFrame(SoftwareFrameResource sw, int width, int height)
    {
        // 确保暂存纹理存在且尺寸匹配
        EnsureStagingTexture((uint)width, (uint)height);

        // 行距须尊重零拷贝原生帧的对齐 stride；
        // 紧凑帧 Stride=0 退化为 width*4。与 D3D11ShaderPipeline.UploadPlanes 保持一致，避免行错位。
        uint rowPitch = (uint)(sw.Stride > 0 ? sw.Stride : width * 4);

        if (sw.Format == PixelFormat.BGRA32)
        {
            // BGRA32 → B8G8R8A8_UNorm：直接拷贝（布局一致）
            // UpdateSubresource<T>(ReadOnlySpan<T> srcData, ID3D11Resource dst, uint subresource, uint rowPitch, uint depthPitch, Box? dstBox)
            _context.UpdateSubresource<byte>(
                sw.Data.Span,
                _stagingTexture!,
                0u,
                rowPitch,
                0u,
                null);
        }
        else if (sw.Format == PixelFormat.RGBA32)
        {
            // RGBA32 → B8G8R8A8_UNorm：需要交换 R/B 通道
            int dataSize = width * height * 4;
            byte[] rented = ArrayPool<byte>.Shared.Rent(dataSize);
            try
            {
                Span<byte> bgra = rented.AsSpan(0, dataSize);
                var srcSpan = sw.Data.Span;
                int pixelCount = width * height;
                for (int i = 0; i < pixelCount; i++)
                {
                    bgra[i * 4 + 0] = srcSpan[i * 4 + 2]; // B ← R
                    bgra[i * 4 + 1] = srcSpan[i * 4 + 1]; // G ← G
                    bgra[i * 4 + 2] = srcSpan[i * 4 + 0]; // R ← B
                    bgra[i * 4 + 3] = srcSpan[i * 4 + 3]; // A ← A
                }

                _context.UpdateSubresource<byte>(
                    bgra,
                    _stagingTexture!,
                    0u,
                    rowPitch,
                    0u,
                    null);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        else
        {
            throw new NotSupportedException(
                $"D3D11 渲染器 不支持像素格式 {sw.Format}，仅支持 BGRA32 和 RGBA32。");
        }

        // 拷贝暂存纹理到 BackBuffer
        _context.CopyResource(_backBuffer, _stagingTexture);
    }

    /// <summary>
    /// GPU 纹理渲染路径（BGRA32/RGBA32）：零拷贝 CopySubresourceRegion（支持纹理数组）。
    /// </summary>
    private void PresentGpuTexture(IGpuTextureResource gpu)
    {
        IntPtr ptr = gpu.NativeTextureHandle;
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException("GPU 纹理句柄无效。");

        // 创建临时 Vortice 包装器（不接管所有权——帧资源持有引用）
        var srcTexture = new ID3D11Texture2D(ptr);
        try
        {
            if (gpu.SubresourceIndex > 0)
            {
                // 纹理数组：拷贝指定切片
                _context.CopySubresourceRegion(
                    _backBuffer, 0u, 0u, 0u, 0u,
                    srcTexture, (uint)gpu.SubresourceIndex, null);
            }
            else
            {
                // 单纹理：直接拷贝整个资源
                _context.CopyResource(_backBuffer, srcTexture);
            }
        }
        finally
        {
            // 抑制终结器——防止 Vortice 包装器的 finalizer 调用 Release
            GC.SuppressFinalize(srcTexture);
        }
    }

    /// <summary>
    /// GPU 纹理渲染路径（NV12/NV21）：经中间 SRV 纹理 + Shader 缩放/转换（DXVA 硬解）。
    /// </summary>
    private void PresentGpuTextureViaShader(IGpuTextureResource gpu, int targetWidth, int targetHeight, AspectRatioMode mode)
    {
        IntPtr ptr = gpu.NativeTextureHandle;
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException("GPU 纹理句柄无效。");

        var srcTexture = new ID3D11Texture2D(ptr);
        try
        {
            _shaderPipeline!.PresentFromGpuTexture(
                srcTexture, gpu.SubresourceIndex,
                gpu.Width, gpu.Height,
                _renderTargetView!, targetWidth, targetHeight, mode);
        }
        finally
        {
            GC.SuppressFinalize(srcTexture);
        }
    }

    /// <summary>
    /// GPU 纹理渲染路径（BGRA32/RGBA32 且尺寸 ≠ 渲染目标）：经 Shader 采样缩放。
    /// </summary>
    /// <remarks>
    /// 源 GPU 纹理可绑定 SRV 直接采样（与 NV12 硬解纹理不同），故 GPU→GPU 拷贝到缓存中间纹理后
    /// 复用 <c>PSRgb</c> Shader 双线性缩放，零 CPU 往返。替代原先抛 <see cref="NotSupportedException"/> 的行为——
    /// 帧尺寸本应逐帧可变，源分辨率 ≠ 窗口尺寸属正常缩放场景（如 4K 视频缩至窗口）。
    /// </remarks>
    private void PresentGpuTextureViaShaderBgra(IGpuTextureResource gpu, int targetWidth, int targetHeight, AspectRatioMode mode)
    {
        IntPtr ptr = gpu.NativeTextureHandle;
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException("GPU 纹理句柄无效。");

        var srcTexture = new ID3D11Texture2D(ptr);
        try
        {
            _shaderPipeline!.PresentFromBgraGpuTexture(
                srcTexture, gpu.SubresourceIndex,
                gpu.Width, gpu.Height, gpu.Format,
                _renderTargetView!, targetWidth, targetHeight, mode);
        }
        finally
        {
            GC.SuppressFinalize(srcTexture);
        }
    }

    /// <summary>
    /// 确保暂存纹理存在且尺寸匹配，尺寸变化时重建。
    /// </summary>
    private void EnsureStagingTexture(uint width, uint height)
    {
        if (_stagingTexture is not null &&
            _stagingWidth == width &&
            _stagingHeight == height)
        {
            return;
        }

        _stagingTexture?.Dispose();

        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1u,
            ArraySize = 1u,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.None,      // 仅作 CopyResource 源，无需绑定到管线
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        _stagingTexture = _device.CreateTexture2D(desc);
        _stagingWidth = width;
        _stagingHeight = height;
    }
}

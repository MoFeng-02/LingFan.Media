using System.Buffers;

namespace LingFan.Media.Renderers.D3D11.Shaders;

// D3D11ShaderPipeline 的 GPU 纹理呈现职责段（部分类，按职责拆分，编译期等价）。
// 软帧 Present 与着色器/纹理管理在主文件及 Textures 段；本段为 GPU 源纹理两条呈现路径，
// 全部同步 GPU 提交（调用方持 D3D11Renderer._gate 锁），无 I/O。
internal sealed partial class D3D11ShaderPipeline
{
    /// <summary>
    /// 用 Shader 路径呈现 GPU 纹理到渲染目标。
    /// </summary>
    /// <remarks>
    /// <para>DXVA 纹理无 SRV 绑定，无法直接采样。路径：</para>
    /// <para>1. <c>CopySubresourceRegion</c> 从 DXVA 纹理拷贝到 staging（CPU 可读）</para>
    /// <para>2. <c>Map</c> 读取 Y + UV 数据</para>
    /// <para>3. <c>UpdateSubresource</c> 上传到现有 R8/R8G8 SRV 纹理</para>
    /// <para>4. 用现有 <c>PS_Nv12</c> 采样呈现（GPU 缩放 + YUV→RGB）</para>
    /// <para>非完全零拷贝（GPU→CPU→GPU），但硬件解码本身在 GPU 上完成。
    /// 可通过 plane-specific SRV 实现完全零拷贝。</para>
    /// <para>调用方持锁；本方法同步 GPU 提交，无 I/O。</para>
    /// </remarks>
    /// <param name="flipY">是否在采样时翻转 UV.y。共享纹理经 Avalonia Composition 导入时，
    /// 合成器按 OpenGL 风格（原点在左下）采样，需在写入共享纹理时预翻转；默认 false。</param>
    internal void PresentFromGpuTexture(
        ID3D11Texture2D srcTexture, int subresourceIndex,
        int srcWidth, int srcHeight,
        ID3D11RenderTargetView rtv, int targetWidth, int targetHeight, AspectRatioMode mode, bool flipY = false)
    {
        ArgumentNullException.ThrowIfNull(srcTexture);
        ArgumentNullException.ThrowIfNull(rtv);
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureShaders();
        EnsureFrameTextures(srcWidth, srcHeight, PixelFormat.NV12);
        EnsureGpuStagingTexture(srcWidth, srcHeight);

        // 1. CopySubresourceRegion 从 DXVA 纹理拷贝到 staging
        _context.CopySubresourceRegion(
            _gpuStagingTexture!, 0u, 0u, 0u, 0u,
            srcTexture, (uint)subresourceIndex, null);

        // 2. Map 读取 Y + UV 数据并上传到 R8/R8G8 SRV 纹理
        var mapped = _context.Map(_gpuStagingTexture!, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        byte[]? yData = null, uvData = null;
        try
        {
            int ySize = srcWidth * srcHeight;
            int uvW = (srcWidth + 1) / 2;
            int uvH = (srcHeight + 1) / 2;
            int uvSize = uvW * uvH * 2;
            int rowPitch = (int)mapped.RowPitch;

            // 使用 ArrayPool 租借内存，避免每帧 new byte[] 的 GC 压力（60fps）
            yData = ArrayPool<byte>.Shared.Rent(ySize);
            uvData = ArrayPool<byte>.Shared.Rent(uvSize);
            unsafe
            {
                byte* src = (byte*)mapped.DataPointer;
                for (int y = 0; y < srcHeight; y++)
                {
                    new ReadOnlySpan<byte>(src + y * rowPitch, srcWidth)
                        .CopyTo(new Span<byte>(yData, y * srcWidth, srcWidth));
                }
            }
            _context.UpdateSubresource<byte>(yData.AsSpan(0, ySize), _planeTextures[0]!, 0u, (uint)srcWidth, 0u, null);

            unsafe
            {
                byte* src = (byte*)mapped.DataPointer + srcHeight * rowPitch;
                int uvRowPitch = (int)mapped.RowPitch; // UV 行距与 Y 相同
                for (int y = 0; y < uvH; y++)
                {
                    new ReadOnlySpan<byte>(src + y * uvRowPitch, uvW * 2)
                        .CopyTo(new Span<byte>(uvData, y * uvW * 2, uvW * 2));
                }
            }
            _context.UpdateSubresource<byte>(uvData.AsSpan(0, uvSize), _planeTextures[1]!, 0u, (uint)(uvW * 2), 0u, null);
        }
        finally
        {
            _context.Unmap(_gpuStagingTexture!, 0u);
            if (yData is not null) ArrayPool<byte>.Shared.Return(yData);
            if (uvData is not null) ArrayPool<byte>.Shared.Return(uvData);
        }

        RegenerateMips(); // 重建 mip 链 → 硬件自动 LOD 三线性缩小，消摩尔纹

        // 3. 用 PS_Nv12 渲染（Composition 共享纹理路径使用 Flip 变体预翻转 Y）
        _context.OMSetRenderTargets(rtv, null!);
        // 按 ScaleMode 计算目标视口矩形（top-left 原点）；GPU 纹理路径同样受宽高比约束
        ComputeScaleRects(srcWidth, srcHeight, targetWidth, targetHeight, mode,
            out int vx, out int vy, out int vw, out int vh);
        _context.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
        _context.RSSetViewport(vx, vy, vw, vh, 0f, 1f);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null!);
        _context.VSSetShader(_vs!);
        _context.PSSetShader(flipY ? _psNv12Flip! : _psNv12!);
        _context.PSSetSampler(0, _sampler!);
        _context.PSSetShaderResource(0, _planeSrvs[0]!);
        _context.PSSetShaderResource(1, _planeSrvs[1]!);

        _context.Draw(3, 0);

        _context.PSSetShaderResource(0, null!);
        _context.PSSetShaderResource(1, null!);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null!);
    }

    /// <summary>
    /// 用 Shader 路径呈现 BGRA/RGBA GPU 纹理到渲染目标（尺寸不符窗口时 GPU 缩放）。
    /// </summary>
    /// <remarks>
    /// <para>与 NV12 路径不同：BGRA/RGBA 纹理可绑定 SRV 直接采样（NV12 硬解纹理不可绑 SRV，须 CPU 往返）。
    /// 路径：</para>
    /// <para>1. <c>CopySubresourceRegion</c> 从源 GPU 纹理拷贝到缓存的中间 BGRA/RGBA 纹理（GPU→GPU，零 CPU 往返）</para>
    /// <para>2. 复用 <c>PSRgb</c> + 平面0 SRV 采样呈现（双线性缩放 + 任意尺寸）</para>
    /// <para>用于 D3D11Renderer 在 <c>case IGpuTextureResource</c> 中 BGRA/RGBA 且尺寸 ≠ 渲染目标时，
    /// 替代原先抛 <see cref="NotSupportedException"/> 的行为——帧尺寸本应逐帧可变（源分辨率 ≠ 窗口尺寸属正常缩放场景）。</para>
    /// <para>调用方持锁；本方法同步 GPU 提交，无 I/O。</para>
    /// </remarks>
    /// <param name="flipY">是否在采样时翻转 UV.y。共享纹理经 Avalonia Composition 导入时，
    /// 合成器按 OpenGL 风格（原点在左下）采样，需在写入共享纹理时预翻转；默认 false。</param>
    internal void PresentFromBgraGpuTexture(
        ID3D11Texture2D srcTexture, int subresourceIndex,
        int srcWidth, int srcHeight, PixelFormat srcFormat,
        ID3D11RenderTargetView rtv, int targetWidth, int targetHeight, AspectRatioMode mode, bool flipY = false)
    {
        ArgumentNullException.ThrowIfNull(srcTexture);
        ArgumentNullException.ThrowIfNull(rtv);
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureShaders();
        // 缓存与源格式一致的中间纹理 + SRV（尺寸/格式变化才重建），复用软帧 BGRA/RGBA 已验证管线
        EnsureFrameTextures(srcWidth, srcHeight, srcFormat);

        // GPU→GPU 拷贝（确保格式一致：源 BGRA/RGBA → 缓存同格式纹理，CopySubresourceRegion 要求格式相同）
        _context.CopySubresourceRegion(
            _planeTextures[0]!, 0u, 0u, 0u, 0u,
            srcTexture, (uint)subresourceIndex, null);
        RegenerateMips(); // 重建 mip 链 → 硬件自动 LOD 三线性缩小，消摩尔纹

        // 复用 PSRgb + 平面0 SRV 采样缩放（Composition 共享纹理路径使用 Flip 变体预翻转 Y）
        _context.OMSetRenderTargets(rtv, null!);
        // 按 ScaleMode 计算目标视口矩形（top-left 原点）；GPU 纹理路径同样受宽高比约束
        ComputeScaleRects(srcWidth, srcHeight, targetWidth, targetHeight, mode,
            out int vx, out int vy, out int vw, out int vh);
        _context.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
        _context.RSSetViewport(vx, vy, vw, vh, 0f, 1f);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null!);
        _context.VSSetShader(_vs!);
        _context.PSSetShader(flipY ? _psRgbFlip! : _psRgb!);
        _context.PSSetSampler(0, _sampler!);
        _context.PSSetShaderResource(0, _planeSrvs[0]!);

        _context.Draw(3, 0);

        _context.PSSetShaderResource(0, null!);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null!);
    }
}
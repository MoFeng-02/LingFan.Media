namespace LingFan.Media.Renderers.D3D11.Shaders;

// D3D11ShaderPipeline 的帧纹理职责段（部分类，按职责拆分，编译期等价）。
// 平面纹理/SRV 的创建、mip 链重建、软帧平面上传与释放；GPU staging 纹理在主文件。
// 全部同步 GPU 调用（调用方持 D3D11Renderer._gate 锁）。
internal sealed partial class D3D11ShaderPipeline
{
    /// <summary>确保帧纹理与 SRV 匹配当前帧的尺寸/格式，不匹配则重建。</summary>
    private void EnsureFrameTextures(int width, int height, PixelFormat format)
    {
        if (_cachedWidth == width && _cachedHeight == height && _cachedFormat == format) return;

        ReleaseFrameTextures();

        switch (format)
        {
            case PixelFormat.BGRA32:
                CreatePlane(0, width, height, Format.B8G8R8A8_UNorm);
                break;

            case PixelFormat.RGBA32:
                // GPU 直接采样 RGBA 纹理——消除 CPU R/B 交换
                CreatePlane(0, width, height, Format.R8G8B8A8_UNorm);
                break;

            case PixelFormat.YUV420P:
                CreatePlane(0, width, height, Format.R8_UNorm);
                CreatePlane(1, (width + 1) / 2, (height + 1) / 2, Format.R8_UNorm);
                CreatePlane(2, (width + 1) / 2, (height + 1) / 2, Format.R8_UNorm);
                break;

            case PixelFormat.YUV422P:
                CreatePlane(0, width, height, Format.R8_UNorm);
                CreatePlane(1, (width + 1) / 2, height, Format.R8_UNorm);
                CreatePlane(2, (width + 1) / 2, height, Format.R8_UNorm);
                break;

            case PixelFormat.YUV444P:
                CreatePlane(0, width, height, Format.R8_UNorm);
                CreatePlane(1, width, height, Format.R8_UNorm);
                CreatePlane(2, width, height, Format.R8_UNorm);
                break;

            case PixelFormat.NV12:
            case PixelFormat.NV21:
                CreatePlane(0, width, height, Format.R8_UNorm);
                // UV 交错平面：R8G8，每纹素 2 字节（U+V 或 V+U）
                CreatePlane(1, (width + 1) / 2, (height + 1) / 2, Format.R8G8_UNorm);
                break;

            default:
                throw new NotSupportedException($"D3D11 Shader 管线不支持像素格式 {format}。");
        }

        _cachedWidth = width;
        _cachedHeight = height;
        _cachedFormat = format;
    }

    private void CreatePlane(int index, int width, int height, Format format)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            // 全 mip 链：配合 _sampler 的 MinMagMipLinear + 着色器 Sample 自动 LOD，
            // 硬件按屏幕空间导数选 LOD 做三线性过滤，消除 >2× 非整数缩小的摩尔纹
            // （见 D3D11Renderer [D3D11-SCALE]：1906x1080→640x480 x=2.98 多倍缩小，mipmap 三线性 LOD 已激活抑制摩尔纹）。
            MipLevels = 0u,
            ArraySize = 1u,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        };
        _planeTextures[index] = _device.CreateTexture2D(desc);
        // null desc ⇒ SRV 覆盖全部 mip 层级，供 GenerateMips + 自动 LOD 采样
        _planeSrvs[index] = _device.CreateShaderResourceView(_planeTextures[index]!, null);
    }

    /// <summary>
    /// 上传后重建平面纹理 mip 链，供硬件按屏幕空间导数自动选 LOD 做三线性缩小（消摩尔纹）。
    /// 平面纹理已带 <see cref="ResourceOptionFlags.GenerateMips"/> + RenderTarget 绑定；R8/R8G8 均为
    /// 可过滤且可渲染格式，<c>GenerateMips</c> 受支持。着色器用 <c>Sample</c>（自动 LOD），故无需改 PS。
    /// </summary>
    private void RegenerateMips()
    {
        for (int i = 0; i < _planeSrvs.Length; i++)
        {
            if (_planeSrvs[i] is not null)
                _context.GenerateMips(_planeSrvs[i]!);
        }
    }

    /// <summary>
    /// 将软件帧各平面上传到 GPU 纹理。
    /// 平面布局与 <c>av_image_copy_to_buffer align=1</c> 紧凑打包语义一致
    /// （与 SkiaVideoPresenter U11 的偏移推导同源）：Y 平面 w×h，色度平面按子采样推导，顺序 Y→U→V（或 Y→UV）。
    /// </summary>
    private void UploadPlanes(SoftwareFrameResource sw)
    {
        int w = sw.Width, h = sw.Height;
        var data = sw.Data.Span;

        switch (sw.Format)
        {
            case PixelFormat.BGRA32:
            case PixelFormat.RGBA32:
            {
                // 零拷贝原生帧可能带对齐 stride（Stride > w*4）——UpdateSubresource 的 rowPitch 直接用实际 stride
                uint rowPitch = (uint)(sw.Stride > 0 ? sw.Stride : w * 4);
                _context.UpdateSubresource(data, _planeTextures[0]!, 0u, rowPitch, 0u, null);
                break;
            }

            case PixelFormat.YUV420P:
            case PixelFormat.YUV422P:
            case PixelFormat.YUV444P:
            {
                (int cw, int ch) = sw.Format switch
                {
                    PixelFormat.YUV444P => (w, h),
                    PixelFormat.YUV422P => ((w + 1) / 2, h),
                    _ => ((w + 1) / 2, (h + 1) / 2), // YUV420P
                };
                int ySize = w * h;
                int cSize = cw * ch;
                _context.UpdateSubresource(data[..ySize], _planeTextures[0]!, 0u, (uint)w, 0u, null);
                _context.UpdateSubresource(data.Slice(ySize, cSize), _planeTextures[1]!, 0u, (uint)cw, 0u, null);
                _context.UpdateSubresource(data.Slice(ySize + cSize, cSize), _planeTextures[2]!, 0u, (uint)cw, 0u, null);
                break;
            }

            case PixelFormat.NV12:
            case PixelFormat.NV21:
            {
                int ySize = w * h;
                int uvW = (w + 1) / 2;
                int uvH = (h + 1) / 2;
                int uvSize = uvW * uvH * 2; // R8G8：2 字节/纹素
                _context.UpdateSubresource(data[..ySize], _planeTextures[0]!, 0u, (uint)w, 0u, null);
                _context.UpdateSubresource(data.Slice(ySize, uvSize), _planeTextures[1]!, 0u, (uint)(uvW * 2), 0u, null);
                break;
            }

            default:
                throw new NotSupportedException($"D3D11 Shader 管线不支持像素格式 {sw.Format}。");
        }
    }

    private void ReleaseFrameTextures()
    {
        for (int i = 0; i < _planeSrvs.Length; i++)
        {
            _planeSrvs[i]?.Dispose();
            _planeSrvs[i] = null;
            _planeTextures[i]?.Dispose();
            _planeTextures[i] = null;
        }
        _cachedWidth = 0;
        _cachedHeight = 0;
        _cachedFormat = (PixelFormat)(-1);
    }
}
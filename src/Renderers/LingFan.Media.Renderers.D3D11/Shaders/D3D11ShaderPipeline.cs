using System.Buffers;
using Vortice.D3DCompiler;

namespace LingFan.Media.Renderers.D3D11.Shaders;

/// <summary>
/// D3D11 Shader 渲染管线（V2-12 R4）：GPU 缩放 + YUV→RGB 转换。
/// </summary>
/// <remarks>
/// <para><b>职责</b>：帧尺寸 ≠ BackBuffer 尺寸（GPU 双线性缩放）或 YUV 像素格式
/// （YUV420P/YUV422P/YUV444P/NV12/NV21）时，走全屏三角形 + PixelShader 采样呈现；
/// 替代 V1 的 CopyResource 尺寸一致限制。</para>
/// <para><b>设计（相对任务示例的简化，均有依据）</b>：</para>
/// <list type="bullet">
/// <item>VS 用 <c>SV_VertexID</c> 生成全屏三角形 → 无需 InputLayout / VertexBuffer（少 2 个 COM 对象、零顶点上传）。</item>
/// <item>RGBA32 直接建 <c>R8G8B8A8_UNorm</c> 纹理由 GPU 采样 → 消除 V1 的 CPU R/B 通道交换循环。</item>
/// <item>YUV 每平面一个 <c>R8_UNorm</c>/<c>R8G8_UNorm</c> 纹理 + SRV，PS 内 BT.601 全范围矩阵
/// （系数 1.402 / -0.344136 / -0.714136 / 1.772，与 SkiaVideoPresenter U11 路径完全一致，双路径色彩不漂移）。</item>
/// <item>HLSL 运行时编译经 Vortice.D3DCompiler → 系统 <c>d3dcompiler_47.dll</c> 原生 P/Invoke：
/// 无反射、无 .NET 动态代码生成，NativeAOT 兼容（动态部分发生在原生编译器内）。</item>
/// </list>
/// <para><b>异步策略</b>：全部同步（native 分类）——Shader 编译与 GPU 提交均为同步原生调用，无 I/O await；
/// 包 <c>async</c>/<c>Task.Run</c> 即伪异步，禁止。</para>
/// <para><b>线程安全</b>：所有方法由 <see cref="D3D11Renderer"/> 在其 <c>_gate</c> 锁内调用，本类不再加锁。</para>
/// <para><b>资源所有权</b>：设备/上下文由工厂持有（不 Dispose）；Shader/Sampler/帧纹理由本类持有，<see cref="Dispose"/> 释放。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，pattern matching。</para>
/// </remarks>
internal sealed class D3D11ShaderPipeline : IDisposable
{
    // ── HLSL 源（内嵌常量；VS 全屏三角形 + 4 个 PS 入口）──
    private const string HlslSource = """
        // 全屏三角形：SV_VertexID ∈ {0,1,2} → 覆盖全屏的超大三角形（无顶点缓冲）
        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            o.uv = uv;
            return o;
        }

        SamplerState LinearSampler : register(s0);
        Texture2D TexRgb : register(t0);   // RGB 路径：BGRA/RGBA 纹理
        Texture2D TexY   : register(t0);   // YUV 路径：Y 平面（R8）
        Texture2D TexU   : register(t1);   // U 平面（R8）或 NV12/NV21 UV 平面（R8G8）
        Texture2D TexV   : register(t2);   // V 平面（R8，仅平面格式）

        float4 PSRgb(VSOut i) : SV_Target
        {
            return float4(TexRgb.Sample(LinearSampler, i.uv).rgb, 1.0);
        }

        // BT.601 全范围（JFIF）矩阵——与 SkiaVideoPresenter CPU 路径一致
        float3 YuvToRgb(float y, float u, float v)
        {
            float d = u - 0.5019608; // 128/255
            float e = v - 0.5019608;
            return saturate(float3(
                y + 1.402 * e,
                y - 0.344136 * d - 0.714136 * e,
                y + 1.772 * d));
        }

        float4 PSYuvPlanar(VSOut i) : SV_Target
        {
            float y = TexY.Sample(LinearSampler, i.uv).r;
            float u = TexU.Sample(LinearSampler, i.uv).r;
            float v = TexV.Sample(LinearSampler, i.uv).r;
            return float4(YuvToRgb(y, u, v), 1.0);
        }

        float4 PSNv12(VSOut i) : SV_Target
        {
            float y = TexY.Sample(LinearSampler, i.uv).r;
            float2 uv = TexU.Sample(LinearSampler, i.uv).rg; // R=U, G=V
            return float4(YuvToRgb(y, uv.x, uv.y), 1.0);
        }

        float4 PSNv21(VSOut i) : SV_Target
        {
            float y = TexY.Sample(LinearSampler, i.uv).r;
            float2 vu = TexU.Sample(LinearSampler, i.uv).rg; // R=V, G=U（NV21 交错序相反）
            return float4(YuvToRgb(y, vu.y, vu.x), 1.0);
        }
        """;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psRgb;
    private ID3D11PixelShader? _psYuvPlanar;
    private ID3D11PixelShader? _psNv12;
    private ID3D11PixelShader? _psNv21;
    private ID3D11SamplerState? _sampler;
    private bool _shadersReady;
    private bool _disposed;

    // ── 帧纹理缓存（尺寸/格式变化时重建；平面 0=Y/RGB，1=U/UV，2=V）──
    private readonly ID3D11Texture2D?[] _planeTextures = new ID3D11Texture2D?[3];
    private readonly ID3D11ShaderResourceView?[] _planeSrvs = new ID3D11ShaderResourceView?[3];
    private int _cachedWidth;
    private int _cachedHeight;
    private PixelFormat _cachedFormat = (PixelFormat)(-1);

    // ── GPU 纹理缓存（V2-15 R5：DXVA 硬解纹理 → staging → 现有 R8/R8G8 SRV 纹理 → Shader 路径）──
    private ID3D11Texture2D? _gpuStagingTexture;
    private int _gpuCachedWidth;
    private int _gpuCachedHeight;

    /// <summary>
    /// 初始化 <see cref="D3D11ShaderPipeline"/> 的新实例（Shader 延迟编译，首帧才付编译成本）。
    /// </summary>
    /// <param name="device">共享 D3D11 设备（不由本类释放）。</param>
    /// <param name="context">共享 D3D11 设备上下文（不由本类释放）。</param>
    internal D3D11ShaderPipeline(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>判断像素格式是否为本管线支持的 YUV 格式。</summary>
    internal static bool IsYuvFormat(PixelFormat format) => format is
        PixelFormat.YUV420P or PixelFormat.YUV422P or PixelFormat.YUV444P or
        PixelFormat.NV12 or PixelFormat.NV21;

    /// <summary>
    /// 用 Shader 路径呈现软件帧到渲染目标（GPU 缩放 + 可选 YUV→RGB）。
    /// </summary>
    /// <remarks>调用方（<see cref="D3D11Renderer"/>）持锁；本方法同步 GPU 提交，无 I/O。</remarks>
    /// <param name="sw">软件帧资源（BGRA32/RGBA32/YUV420P/YUV422P/YUV444P/NV12/NV21）。</param>
    /// <param name="rtv">渲染目标视图（BackBuffer RTV）。</param>
    /// <param name="targetWidth">渲染目标宽度（像素）。</param>
    /// <param name="targetHeight">渲染目标高度（像素）。</param>
    internal void Present(SoftwareFrameResource sw, ID3D11RenderTargetView rtv, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(sw);
        ArgumentNullException.ThrowIfNull(rtv);
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureShaders();
        EnsureFrameTextures(sw.Width, sw.Height, sw.Format);
        UploadPlanes(sw);

        // 选择 PS
        ID3D11PixelShader ps = sw.Format switch
        {
            PixelFormat.BGRA32 or PixelFormat.RGBA32 => _psRgb!,
            PixelFormat.YUV420P or PixelFormat.YUV422P or PixelFormat.YUV444P => _psYuvPlanar!,
            PixelFormat.NV12 => _psNv12!,
            PixelFormat.NV21 => _psNv21!,
            _ => throw new NotSupportedException(
                $"D3D11 Shader 管线不支持像素格式 {sw.Format}。支持 BGRA32/RGBA32/YUV420P/YUV422P/YUV444P/NV12/NV21。"),
        };

        // 绑定管线状态并绘制全屏三角形
        _context.OMSetRenderTargets(rtv, null!);
        _context.RSSetViewport(0, 0, targetWidth, targetHeight, 0f, 1f);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null!); // SV_VertexID 生成顶点，无输入布局
        _context.VSSetShader(_vs!);
        _context.PSSetShader(ps);
        _context.PSSetSampler(0, _sampler!);
        _context.PSSetShaderResource(0, _planeSrvs[0]!);
        if (_planeSrvs[1] is not null) _context.PSSetShaderResource(1, _planeSrvs[1]!);
        if (_planeSrvs[2] is not null) _context.PSSetShaderResource(2, _planeSrvs[2]!);

        _context.Draw(3, 0);

        // 解绑 SRV/RTV，避免下次 UpdateSubresource 时资源仍绑定在管线（D3D11 运行时会告警）
        _context.PSSetShaderResource(0, null!);
        _context.PSSetShaderResource(1, null!);
        _context.PSSetShaderResource(2, null!);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null!);
    }

    /// <summary>
    /// 用 Shader 路径呈现 GPU 纹理到渲染目标（V2-15 R5：DXVA 硬解路径）。
    /// </summary>
    /// <remarks>
    /// <para>DXVA 纹理无 SRV 绑定，无法直接采样。路径：</para>
    /// <para>1. <c>CopySubresourceRegion</c> 从 DXVA 纹理拷贝到 staging（CPU 可读）</para>
    /// <para>2. <c>Map</c> 读取 Y + UV 数据</para>
    /// <para>3. <c>UpdateSubresource</c> 上传到现有 R8/R8G8 SRV 纹理</para>
    /// <para>4. 用现有 <c>PS_Nv12</c> 采样呈现（GPU 缩放 + YUV→RGB）</para>
    /// <para>非完全零拷贝（GPU→CPU→GPU），但硬件解码本身在 GPU 上完成。
    /// V3 可通过 plane-specific SRV 实现完全零拷贝。</para>
    /// <para>调用方持锁；本方法同步 GPU 提交，无 I/O。</para>
    /// </remarks>
    internal void PresentFromGpuTexture(
        ID3D11Texture2D srcTexture, int subresourceIndex,
        int srcWidth, int srcHeight,
        ID3D11RenderTargetView rtv, int targetWidth, int targetHeight)
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

            // V2-15 审计修复：使用 ArrayPool 租借内存，避免每帧 new byte[] 的 GC 压力（60fps）
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

        // 3. 用 PS_Nv12 渲染
        _context.OMSetRenderTargets(rtv, null!);
        _context.RSSetViewport(0, 0, targetWidth, targetHeight, 0f, 1f);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null!);
        _context.VSSetShader(_vs!);
        _context.PSSetShader(_psNv12!);
        _context.PSSetSampler(0, _sampler!);
        _context.PSSetShaderResource(0, _planeSrvs[0]!);
        _context.PSSetShaderResource(1, _planeSrvs[1]!);

        _context.Draw(3, 0);

        _context.PSSetShaderResource(0, null!);
        _context.PSSetShaderResource(1, null!);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null!);
    }

    private void EnsureGpuStagingTexture(int width, int height)
    {
        if (_gpuCachedWidth == width && _gpuCachedHeight == height && _gpuStagingTexture is not null)
            return;

        _gpuStagingTexture?.Dispose();

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1u,
            ArraySize = 1u,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        _gpuStagingTexture = _device.CreateTexture2D(desc);
        _gpuCachedWidth = width;
        _gpuCachedHeight = height;
    }

    private void ReleaseGpuStagingTexture()
    {
        _gpuStagingTexture?.Dispose();
        _gpuStagingTexture = null;
        _gpuCachedWidth = 0;
        _gpuCachedHeight = 0;
    }

    /// <summary>释放 Shader / Sampler / 帧纹理（不释放共享设备与上下文）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseGpuStagingTexture();
        ReleaseFrameTextures();
        _sampler?.Dispose();
        _sampler = null;
        _psNv21?.Dispose();
        _psNv21 = null;
        _psNv12?.Dispose();
        _psNv12 = null;
        _psYuvPlanar?.Dispose();
        _psYuvPlanar = null;
        _psRgb?.Dispose();
        _psRgb = null;
        _vs?.Dispose();
        _vs = null;
        _shadersReady = false;
    }

    // ── 内部实现（均在渲染器锁内执行）──

    /// <summary>编译并创建全部 Shader 与采样器（懒加载，仅首次；同步原生编译）。</summary>
    private void EnsureShaders()
    {
        if (_shadersReady) return;

        // Vortice.D3DCompiler.Compiler.Compile → 系统 d3dcompiler_47.dll（原生编译，AOT 安全）
        ReadOnlyMemory<byte> vsBlob = Compiler.Compile(HlslSource, "VSMain", "LingFanMedia.VS", "vs_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        ReadOnlyMemory<byte> psRgbBlob = Compiler.Compile(HlslSource, "PSRgb", "LingFanMedia.PSRgb", "ps_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        ReadOnlyMemory<byte> psYuvBlob = Compiler.Compile(HlslSource, "PSYuvPlanar", "LingFanMedia.PSYuv", "ps_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        ReadOnlyMemory<byte> psNv12Blob = Compiler.Compile(HlslSource, "PSNv12", "LingFanMedia.PSNv12", "ps_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        ReadOnlyMemory<byte> psNv21Blob = Compiler.Compile(HlslSource, "PSNv21", "LingFanMedia.PSNv21", "ps_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);

        _vs = _device.CreateVertexShader(vsBlob.Span, null!);
        _psRgb = _device.CreatePixelShader(psRgbBlob.Span, null!);
        _psYuvPlanar = _device.CreatePixelShader(psYuvBlob.Span, null!);
        _psNv12 = _device.CreatePixelShader(psNv12Blob.Span, null!);
        _psNv21 = _device.CreatePixelShader(psNv21Blob.Span, null!);

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear, // 双线性缩放
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0f,
            MaxLOD = float.MaxValue,
        });

        _shadersReady = true;
    }

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
                // GPU 直接采样 RGBA 纹理——消除 V1 的 CPU R/B 交换
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
            MipLevels = 1u,
            ArraySize = 1u,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _planeTextures[index] = _device.CreateTexture2D(desc);
        _planeSrvs[index] = _device.CreateShaderResourceView(_planeTextures[index]!, null);
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

using System.Runtime.InteropServices;
using System.Text;
using LingFan.Media.Abstractions;
using LingFan.Media.Apple.Shared;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.Metal;

/// <summary>
/// Metal Shader 渲染管线：GPU 缩放（信箱/裁剪）+ YUV→RGB（BT.601 全范围）转换。
/// </summary>
/// <remarks>
/// <para><b>职责</b>：与 D3D11/Vulkan/桌面 OpenGL/GLES 同源 Shader 管线一致——帧尺寸 ≠ 渲染目标（GPU 缩放）或 YUV 像素格式
/// （YUV420P/YUV422P/YUV444P/NV12/NV21）时，走全屏 quad + 片元着色器采样呈现。</para>
/// <para><b>与 OpenGL ES（<see cref="LingFan.Media.Renderers.OpenGLES.Core.GlesShaderPipeline"/>）的差异</b>：</para>
/// <list type="bullet">
/// <item>着色器语言为 Metal Shading Language（MSL），而非 GLSL；YUV→RGB 系数（BT.601 全范围）完全同源，色彩不漂移。</item>
/// <item>MSL 经 <c>newLibraryWithSource:options:error:</c> <b>运行期编译</b>——避免引入 .metal 编译工具链依赖（对本机仅 Windows 构建环境尤其关键）。</li>
/// <item>顶点缩放（信箱/裁剪）在顶点着色器内以 scale/offset 实现，规避 setViewport/setScissorRect 的结构体按值传参（跨架构 ABI 风险）。</item>
/// <item>源纹理上传走 <c>replaceRegion:mipmapLevel:withBytes:bytesPerRow:</c>；逐行上传以支持非零 stride 的零拷贝帧。</item>
/// </list>
/// <para><b>支持格式</b>：与 GLES 同源——BGRA32 / RGBA32 / RGB24 / YUV420P / YUV422P / YUV444P / NV12 / NV21。
/// 10-bit（P010 / YUV420P10）由解码侧解包为 BGRA32 后流入，渲染器仅消费 BGRA32（单一收敛点，与总记忆一致）。</para>
/// <para><b>异步策略</b>：全部同步（native 分类）——MSL 编译与 GPU 提交均为同步原生调用，无 I/O await；包 async/Task.Run 即伪异步，禁止。</para>
/// <para><b>线程安全</b>：所有方法由 <see cref="MetalRenderer"/> 在其 <c>_gate</c> 锁内调用，本类不再加锁。</para>
/// <para><b>资源所有权</b>：设备由渲染器持有（不 Dispose）；库/管线状态/顶点缓冲由本类持有，<see cref="Dispose"/> 释放；
/// 每帧源纹理（+1）在 Present 内 <see cref="AppleRuntime.objc_release"/>，自动释放对象由渲染器的 autorelease 池回收。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；Metal 调用经 <see cref="MetalNative"/>（零反射 [LibraryImport]）。</para>
/// </remarks>
internal sealed unsafe class MetalShaderPipeline : IDisposable
{
    // ── Metal Shading Language 源码（运行期编译；顶点缩放 + YUV→RGB BT.601 全范围）──
    private const string MslSource = """
        #include <metal_stdlib>
        using namespace metal;

        struct VOut {
            float4 pos [[position]];
            float2 uv;
        };

        // 顶点：全屏 quad；uv 自然映射（Metal 纹理原点左上，图像顶 → uv.y = 0）。
        // 信箱/裁剪经 scale/offset 在裁剪空间内实现（避免视口/裁剪矩形结构体传参）。
        vertex VOut vmain(uint vid [[vertex_id]],
                          const device float* verts [[buffer(0)]],
                          const device float* su [[buffer(1)]]) {
            int i = (int)vid * 4;
            float2 p = float2(verts[i], verts[i + 1]);
            float2 uv = float2(verts[i + 2], verts[i + 3]);
            float2 scale = float2(su[0], su[1]);
            float2 off = float2(su[2], su[3]);
            VOut o;
            o.pos = float4(p * scale + off, 0.0, 1.0);
            o.uv = uv;
            return o;
        }

        // YUV 三平面（BT.601 全范围，系数与 D3D11/GLES 一致）
        fragment float4 fyuv(VOut i [[stage_in]],
                             texture2d<float> ty [[texture(0)]],
                             texture2d<float> tu [[texture(1)]],
                             texture2d<float> tv [[texture(2)]]) {
            constexpr sampler s(coord::normalized, address::clamp_to_edge, filter::linear);
            float y = ty.sample(s, i.uv).r;
            float u = tu.sample(s, i.uv).r - 0.5;
            float v = tv.sample(s, i.uv).r - 0.5;
            float r = y + 1.402 * v;
            float g = y - 0.344136 * u - 0.714136 * v;
            float b = y + 1.772 * u;
            return float4(clamp(r, 0.0, 1.0), clamp(g, 0.0, 1.0), clamp(b, 0.0, 1.0), 1.0);
        }

        // NV12/NV21 半平面（UV 交错；swap 区分 NV12(U=R,V=G) 与 NV21(V=R,U=G)）
        fragment float4 fnv(VOut i [[stage_in]],
                            texture2d<float> ty [[texture(0)]],
                            texture2d<float> tuv [[texture(1)]],
                            constant int& swap [[buffer(0)]]) {
            constexpr sampler s(coord::normalized, address::clamp_to_edge, filter::linear);
            float y = ty.sample(s, i.uv).r;
            float2 uv = tuv.sample(s, i.uv).rg;
            float u = (swap == 1) ? uv.g - 0.5 : uv.r - 0.5;
            float v = (swap == 1) ? uv.r - 0.5 : uv.g - 0.5;
            float r = y + 1.402 * v;
            float g = y - 0.344136 * u - 0.714136 * v;
            float b = y + 1.772 * u;
            return float4(clamp(r, 0.0, 1.0), clamp(g, 0.0, 1.0), clamp(b, 0.0, 1.0), 1.0);
        }

        // RGBA/BGRA 直通：Metal 采样 bgra8Unorm 已归一化为 RGBA 语义（.r=红、.b=蓝），
        // 与 D3D11（.r=蓝，需 .bgra 交换通道）不同——故此处直接输出 c，无需交换通道。
        // 引用：Metal Shading Language 规范 5.10.1；bgra8Unorm 像素 [B,G,R,A] 采样得 [R,G,B,A]。
        fragment float4 frgb(VOut i [[stage_in]],
                             texture2d<float> t [[texture(0)]]) {
            constexpr sampler s(coord::normalized, address::clamp_to_edge, filter::linear);
            float4 c = t.sample(s, i.uv);
            return c;
        }
        """;

    private readonly ILogger? _logger;
    private readonly nint _device;
    private readonly nint _queue;
    private bool _disposed;
    private bool _initialized;

    private nint _library;
    private nint _vbo;
    private nint _rgbPipeline;
    private nint _yuvPipeline;
    private nint _nvPipeline;

    /// <summary>初始化 <see cref="MetalShaderPipeline"/>（仅保存设备/队列与日志器；Metal 资源延迟到首次 <see cref="Present"/>，
    /// 彼时可绘制层已就绪、处于 autorelease 池内）。</summary>
    /// <param name="device">MTLDevice*（来自 <see cref="MetalContext"/>，本类不释放）。</param>
    /// <param name="queue">MTLCommandQueue*（来自 <see cref="MetalContext"/>，本类不释放，持久复用）。</param>
    /// <param name="logger">日志器（可为 null）。</param>
    internal MetalShaderPipeline(nint device, nint queue, ILogger? logger = null)
    {
        _device = device;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>判断像素格式是否为本管线支持的 YUV 格式（与 GLES 同源）。</summary>
    internal static bool IsYuvFormat(PixelFormat format) => format is
        PixelFormat.YUV420P or PixelFormat.YUV422P or PixelFormat.YUV444P or
        PixelFormat.NV12 or PixelFormat.NV21;

    // ── 初始化 ──

    /// <summary>延迟初始化 Metal 资源（MSL 库 + 3 套渲染管线 + 顶点缓冲）。
    /// 必须在 autorelease 池内、可绘制层就绪时调用——由 <see cref="MetalRenderer.Present"/> 首次触发，仅执行一次。</summary>
    private void EnsureInitialized()
    {
        if (_initialized) return;
        CompileLibraryAndPipelines();
        CreateVertexBuffer();
        _initialized = true;
        _logger?.LogDebug("Metal Shader 管线初始化完成（MSL 库 + 3 套渲染管线 + 顶点缓冲）。");
    }

    private void CompileLibraryAndPipelines()
    {
        nint source = AppleRuntime.MakeNSString(MslSource);
        nint error = nint.Zero;
        nint lib = AppleRuntime.objc_msgSend(_device, AppleRuntime.Sel("newLibraryWithSource:options:error:"), source, nint.Zero, &error);
        if (lib == nint.Zero)
            throw new InvalidOperationException("Metal：MSL 库编译失败：" + ErrorString(error));

        nint vs = AppleRuntime.objc_msgSend(lib, AppleRuntime.Sel("newFunctionWithName:"), AppleRuntime.MakeNSString("vmain"));
        nint fsRgb = AppleRuntime.objc_msgSend(lib, AppleRuntime.Sel("newFunctionWithName:"), AppleRuntime.MakeNSString("frgb"));
        nint fsYuv = AppleRuntime.objc_msgSend(lib, AppleRuntime.Sel("newFunctionWithName:"), AppleRuntime.MakeNSString("fyuv"));
        nint fsNv = AppleRuntime.objc_msgSend(lib, AppleRuntime.Sel("newFunctionWithName:"), AppleRuntime.MakeNSString("fnv"));

        if (vs == nint.Zero || fsRgb == nint.Zero || fsYuv == nint.Zero || fsNv == nint.Zero)
            throw new InvalidOperationException("Metal：MSL 函数查找失败（vmain/frgb/fyuv/fnv 之一不存在）。");

        _rgbPipeline = NewPipeline(vs, fsRgb);
        _yuvPipeline = NewPipeline(vs, fsYuv);
        _nvPipeline = NewPipeline(vs, fsNv);

        // 临时函数对象（newFunctionWithName: 返回 +1）由渲染管线状态内部强引用，此处释放我们的 +1 引用。
        AppleRuntime.objc_release(vs);
        AppleRuntime.objc_release(fsRgb);
        AppleRuntime.objc_release(fsYuv);
        AppleRuntime.objc_release(fsNv);

        // 库引用（newLibraryWithSource: 返回 +1，本类持有，Dispose 释放一次即平衡）。
        _library = lib;
    }

    private nint NewPipeline(nint vs, nint fs)
    {
        nint pdesc = AppleRuntime.AllocInit(AppleRuntime.Class("MTLRenderPipelineDescriptor"));
        AppleRuntime.objc_msgSend(pdesc, AppleRuntime.Sel("setVertexFunction:"), vs);
        AppleRuntime.objc_msgSend(pdesc, AppleRuntime.Sel("setFragmentFunction:"), fs);
        nint caArr = AppleRuntime.objc_msgSend(pdesc, AppleRuntime.Sel("colorAttachments"));
        nint ca0 = AppleRuntime.objc_msgSend(caArr, AppleRuntime.Sel("objectAtIndexedSubscript:"), (nuint)0);
        AppleRuntime.objc_msgSend(ca0, AppleRuntime.Sel("setPixelFormat:"), MetalConstants.BGRA8Unorm);

        nint error = nint.Zero;
        nint state = AppleRuntime.objc_msgSend(_device, AppleRuntime.Sel("newRenderPipelineStateWithDescriptor:error:"), pdesc, &error);
        if (state == nint.Zero)
            throw new InvalidOperationException("Metal：创建渲染管线失败：" + ErrorString(error));
        AppleRuntime.objc_release(pdesc); // 释放 MTLRenderPipelineDescriptor（AllocInit 的 +1）；管线状态已内部持有配置
        return state; // newRenderPipelineStateWithDescriptor:error: 已返回 +1（本类所有），Dispose 释放一次即平衡，切勿再 retain
    }

    private void CreateVertexBuffer()
    {
        // 全屏 quad（triangle strip）：pos.xy + uv.xy；uv 自然映射（Metal 纹理原点左上）。
        float[] quad =
        {
            -1f, -1f, 0f, 1f,
             1f, -1f, 1f, 1f,
            -1f,  1f, 0f, 0f,
             1f,  1f, 1f, 0f,
        };
        fixed (float* p = quad)
            _vbo = AppleRuntime.objc_msgSend(_device, AppleRuntime.Sel("newBufferWithBytes:length:options:"),
                (nint)p, (nuint)(quad.Length * sizeof(float)), MetalConstants.ResourceStorageModeShared);
        // newBufferWithBytes: 返回 +1（本类所有），Dispose 释放一次即平衡，无需额外 retain。
    }

    // ── 纹理管理 ──

    private nint CreatePlaneTexture(int w, int h, nuint pixelFormat)
    {
        nint td = AppleRuntime.objc_msgSend(AppleRuntime.Class("MTLTextureDescriptor"),
            AppleRuntime.Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            pixelFormat, (nuint)w, (nuint)h, (byte)0);
        nint tex = AppleRuntime.objc_msgSend(_device, AppleRuntime.Sel("newTextureWithDescriptor:"), td);
        // newTextureWithDescriptor: 返回 +1（本方法所有），调用方 Present 内 objc_release 一次即平衡。
        return tex;
    }

    private static void UploadPlane(nint tex, int w, int h, int bpp, int stride, PixelFormat format, byte* basePtr)
    {
        AppleRuntime.MTLRegion region = new() { X = 0, Y = 0, Z = 0, Width = (nuint)w, Height = 1, Depth = 1 };
        if (format == PixelFormat.RGB24)
        {
            // 扩展 RGB24（3 字节）→ RGBA8Unorm（4 字节）逐行上传
            byte[] rgba = new byte[w * 4];
            for (int row = 0; row < h; row++)
            {
                byte* srcRow = basePtr + row * stride;
                for (int x = 0, di = 0; x < w; x++, di += 4)
                {
                    rgba[di] = srcRow[x * 3];
                    rgba[di + 1] = srcRow[x * 3 + 1];
                    rgba[di + 2] = srcRow[x * 3 + 2];
                    rgba[di + 3] = 255;
                }
                region.Y = (nuint)row;
                fixed (byte* dp = rgba)
                    AppleRuntime.objc_msgSendReplaceRegion(tex, AppleRuntime.Sel("replaceRegion:mipmapLevel:withBytes:bytesPerRow:"),
                        ref region, (nuint)0, (nint)dp, (nuint)(w * 4));
            }
        }
        else
        {
            for (int row = 0; row < h; row++)
            {
                region.Y = (nuint)row;
                AppleRuntime.objc_msgSendReplaceRegion(tex, AppleRuntime.Sel("replaceRegion:mipmapLevel:withBytes:bytesPerRow:"),
                    ref region, (nuint)0, (nint)(basePtr + row * stride), (nuint)stride);
            }
        }
    }

    // ── 对外呈现 ──

    /// <summary>用 Shader 路径将软件帧渲染到指定可绘制纹理（不提交——由调用方 presentDrawable + commit）。</summary>
    /// <param name="sw">软件帧。</param>
    /// <param name="dstW">渲染目标像素宽（可绘制纹理宽）。</param>
    /// <param name="dstH">渲染目标像素高（可绘制纹理高）。</param>
    /// <param name="mode">宽高比缩放模式。</param>
    /// <param name="drawable">当前 CAMetalDrawable*（present 目标）。</param>
    /// <param name="targetTexture">可绘制层纹理（渲染目标）。</param>
    internal void Present(SoftwareFrameResource sw, int dstW, int dstH, AspectRatioMode mode,
        nint drawable, nint targetTexture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sw);

        EnsureInitialized();

        // 渲染 Pass（自动释放对象，处于渲染器包装的 autorelease 池内）
        nint rpDesc = AppleRuntime.objc_msgSend(AppleRuntime.Class("MTLRenderPassDescriptor"), AppleRuntime.Sel("renderPassDescriptor"));
        nint caArr = AppleRuntime.objc_msgSend(rpDesc, AppleRuntime.Sel("colorAttachments"));
        nint ca0 = AppleRuntime.objc_msgSend(caArr, AppleRuntime.Sel("objectAtIndexedSubscript:"), (nuint)0);
        AppleRuntime.objc_msgSend(ca0, AppleRuntime.Sel("setTexture:"), targetTexture);
        AppleRuntime.objc_msgSend(ca0, AppleRuntime.Sel("setLoadAction:"), MetalConstants.LoadActionClear);
        AppleRuntime.MTLClearColor cc = new() { Red = 0, Green = 0, Blue = 0, Alpha = 1 };
        AppleRuntime.objc_msgSend(ca0, AppleRuntime.Sel("setClearColor:"), ref cc);
        AppleRuntime.objc_msgSend(ca0, AppleRuntime.Sel("setStoreAction:"), MetalConstants.StoreActionStore);

        nint cb = AppleRuntime.objc_msgSend(_queue, AppleRuntime.Sel("newCommandBuffer"));
        nint enc = AppleRuntime.objc_msgSend(cb, AppleRuntime.Sel("renderCommandEncoderWithDescriptor:"), rpDesc);

        // 顶点缓冲（持久）+ 缩放/居中（信箱/裁剪）
        AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setVertexBuffer:offset:atIndex:"), _vbo, (nuint)0, (nuint)0);
        ComputeScale(sw.Width, sw.Height, dstW, dstH, mode, out float sx, out float sy);
        float[] su = { sx, sy, 0f, 0f };
        fixed (float* sup = su)
            AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setVertexBytes:length:atIndex:"), (nint)sup, (nuint)16, (nuint)1);

        var span = sw.Data.Span;
        fixed (byte* basePtr = span)
        {
            int w = sw.Width, h = sw.Height;
            switch (sw.Format)
            {
                case PixelFormat.BGRA32:
                case PixelFormat.RGBA32:
                {
                    int bpp = 4;
                    int stride = sw.Stride > 0 ? sw.Stride : w * bpp;
                    nuint pf = sw.Format == PixelFormat.BGRA32 ? MetalConstants.BGRA8Unorm : MetalConstants.RGBA8Unorm;
                    nint tex = CreatePlaneTexture(w, h, pf);
                    UploadPlane(tex, w, h, bpp, stride, sw.Format, basePtr);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setRenderPipelineState:"), _rgbPipeline);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setFragmentTexture:atIndex:"), tex, (nuint)0);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("drawPrimitives:vertexStart:vertexCount:"), MetalConstants.PrimitiveTypeTriangleStrip, (nuint)0, (nuint)4);
                    AppleRuntime.objc_release(tex);
                    break;
                }

                case PixelFormat.RGB24:
                {
                    int stride = sw.Stride > 0 ? sw.Stride : w * 3;
                    nint tex = CreatePlaneTexture(w, h, MetalConstants.RGBA8Unorm);
                    UploadPlane(tex, w, h, 3, stride, PixelFormat.RGB24, basePtr);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setRenderPipelineState:"), _rgbPipeline);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setFragmentTexture:atIndex:"), tex, (nuint)0);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("drawPrimitives:vertexStart:vertexCount:"), MetalConstants.PrimitiveTypeTriangleStrip, (nuint)0, (nuint)4);
                    AppleRuntime.objc_release(tex);
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
                        _ => ((w + 1) / 2, (h + 1) / 2),
                    };
                    int ySize = w * h;
                    int cSize = cw * ch;
                    nint ty = CreatePlaneTexture(w, h, MetalConstants.R8Unorm);
                    nint tu = CreatePlaneTexture(cw, ch, MetalConstants.R8Unorm);
                    nint tv = CreatePlaneTexture(cw, ch, MetalConstants.R8Unorm);
                    UploadPlane(ty, w, h, 1, w, sw.Format, basePtr);
                    UploadPlane(tu, cw, ch, 1, cw, sw.Format, basePtr + ySize);
                    UploadPlane(tv, cw, ch, 1, cw, sw.Format, basePtr + ySize + cSize);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setRenderPipelineState:"), _yuvPipeline);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setFragmentTexture:atIndex:"), ty, (nuint)0);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setFragmentTexture:atIndex:"), tu, (nuint)1);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setFragmentTexture:atIndex:"), tv, (nuint)2);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("drawPrimitives:vertexStart:vertexCount:"), MetalConstants.PrimitiveTypeTriangleStrip, (nuint)0, (nuint)4);
                    AppleRuntime.objc_release(ty);
                    AppleRuntime.objc_release(tu);
                    AppleRuntime.objc_release(tv);
                    break;
                }

                case PixelFormat.NV12:
                case PixelFormat.NV21:
                {
                    int uvW = (w + 1) / 2;
                    int uvH = (h + 1) / 2;
                    int ySize = w * h;
                    nint ty = CreatePlaneTexture(w, h, MetalConstants.R8Unorm);
                    nint tuv = CreatePlaneTexture(uvW, uvH, MetalConstants.RG8Unorm);
                    UploadPlane(ty, w, h, 1, w, sw.Format, basePtr);
                    UploadPlane(tuv, uvW, uvH, 2, uvW * 2, sw.Format, basePtr + ySize);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setRenderPipelineState:"), _nvPipeline);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setFragmentTexture:atIndex:"), ty, (nuint)0);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setFragmentTexture:atIndex:"), tuv, (nuint)1);
                    int swap = sw.Format == PixelFormat.NV21 ? 1 : 0;
                    int* fp = &swap;
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("setFragmentBytes:length:atIndex:"), (nint)fp, (nuint)4, (nuint)0);
                    AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("drawPrimitives:vertexStart:vertexCount:"), MetalConstants.PrimitiveTypeTriangleStrip, (nuint)0, (nuint)4);
                    AppleRuntime.objc_release(ty);
                    AppleRuntime.objc_release(tuv);
                    break;
                }

                default:
                    throw new NotSupportedException(
                        $"Metal 渲染器不支持像素格式 {sw.Format}。支持 BGRA32/RGBA32/RGB24/YUV420P/YUV422P/YUV444P/NV12/NV21。");
            }
        }

        AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("endEncoding"));
        AppleRuntime.objc_msgSend(cb, AppleRuntime.Sel("presentDrawable:"), drawable);
        AppleRuntime.objc_msgSend(cb, AppleRuntime.Sel("commit"));
        AppleRuntime.objc_release(cb); // newCommandBuffer 返回 +1；commit 后由命令队列接管直至 GPU 完成，释放我们的 +1
    }

    /// <summary>按 <see cref="AspectRatioMode"/> 计算软帧→目标（裁剪空间中心）的缩放因子（offset 恒为 0，居中）。
    /// 纯视口数学，无需改动着色器（与 GLES/Vulkan/Skia 语义一致）。</summary>
    private static void ComputeScale(int srcW, int srcH, int dstW, int dstH, AspectRatioMode mode,
        out float sx, out float sy)
    {
        sx = 1f; sy = 1f;
        if (srcW <= 0 || srcH <= 0) return;

        switch (mode)
        {
            case AspectRatioMode.Uniform:
            {
                double fit = Math.Min((double)dstW / srcW, (double)dstH / srcH);
                sx = (float)(srcW * fit / dstW);
                sy = (float)(srcH * fit / dstH);
                break;
            }
            case AspectRatioMode.UniformToFill:
            {
                double cover = Math.Max((double)dstW / srcW, (double)dstH / srcH);
                sx = (float)(srcW * cover / dstW);
                sy = (float)(srcH * cover / dstH);
                break;
            }
            case AspectRatioMode.Fill:
            default:
                break;
        }
    }

    private static string ErrorString(nint error)
    {
        if (error == nint.Zero) return "（无错误信息）";
        nint desc = AppleRuntime.objc_msgSend(error, AppleRuntime.Sel("localizedDescription"));
        if (desc == nint.Zero) return "（无法读取错误描述）";
        nint utf8 = AppleRuntime.objc_msgSend(desc, AppleRuntime.Sel("UTF8String"));
        if (utf8 == nint.Zero) return "（UTF8String 为空）";
        return Marshal.PtrToStringUTF8(utf8) ?? "（解码失败）";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_vbo != nint.Zero) { AppleRuntime.objc_release(_vbo); _vbo = nint.Zero; }
        if (_rgbPipeline != nint.Zero) { AppleRuntime.objc_release(_rgbPipeline); _rgbPipeline = nint.Zero; }
        if (_yuvPipeline != nint.Zero) { AppleRuntime.objc_release(_yuvPipeline); _yuvPipeline = nint.Zero; }
        if (_nvPipeline != nint.Zero) { AppleRuntime.objc_release(_nvPipeline); _nvPipeline = nint.Zero; }
        if (_library != nint.Zero) { AppleRuntime.objc_release(_library); _library = nint.Zero; }
        _logger?.LogDebug("Metal Shader 管线已释放");
    }
}

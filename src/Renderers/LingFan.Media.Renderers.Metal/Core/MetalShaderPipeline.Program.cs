using LingFan.Media.Abstractions;
using LingFan.Media.Apple.Shared;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.Metal;

/// <summary>
/// 管线构建分部：MSL 源码、运行期编译、渲染管线状态与顶点缓冲创建。
/// </summary>
internal sealed unsafe partial class MetalShaderPipeline
{
    // Metal Shading Language 源码（运行期编译；顶点缩放 + YUV→RGB BT.601 全范围）
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

    private nint _library;
    private nint _vbo;
    private nint _rgbPipeline;
    private nint _yuvPipeline;
    private nint _nvPipeline;

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

        // 临时函数对象（newFunctionWithName: 返回 +1）由渲染管线状态内部强引用，此处释放本类持有的 +1 引用。
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

    // 纹理管理

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
}
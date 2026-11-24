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
/// <para><b>与 OpenGL ES（<c>LingFan.Media.Renderers.OpenGLES.Core.GlesShaderPipeline</c>）的差异</b>：</para>
/// <list type="bullet">
/// <item>着色器语言为 Metal Shading Language（MSL），而非 GLSL；YUV→RGB 系数（BT.601 全范围）完全同源，色彩不漂移。</item>
/// <item>MSL 经 <c>newLibraryWithSource:options:error:</c> <b>运行期编译</b>——避免引入 .metal 编译工具链依赖（对本机仅 Windows 构建环境尤其关键）。</item>
/// <item>顶点缩放（信箱/裁剪）在顶点着色器内以 scale/offset 实现，规避 setViewport/setScissorRect 的结构体按值传参（跨架构 ABI 风险）。</item>
/// <item>源纹理上传走 <c>replaceRegion:mipmapLevel:withBytes:bytesPerRow:</c>；逐行上传以支持非零 stride 的零拷贝帧。</item>
/// </list>
/// <para><b>支持格式</b>：与 GLES 同源——BGRA32 / RGBA32 / RGB24 / YUV420P / YUV422P / YUV444P / NV12 / NV21。
/// 10-bit（P010 / YUV420P10）由解码侧解包为 BGRA32 后流入，渲染器仅消费 BGRA32（单一收敛点）。</para>
/// <para><b>异步策略</b>：全部同步（native 分类）——MSL 编译与 GPU 提交均为同步原生调用，无 I/O await；包 async/Task.Run 即伪异步，禁止。</para>
/// <para><b>线程安全</b>：所有方法由 <see cref="MetalRenderer"/> 在其 <c>_gate</c> 锁内调用，本类不再加锁。</para>
/// <para><b>资源所有权</b>：设备由渲染器持有（不 Dispose）；库/管线状态/顶点缓冲由本类持有，<see cref="Dispose"/> 释放；
/// 每帧源纹理（+1）在 Present 内 <see cref="AppleRuntime.objc_release"/>，自动释放对象由渲染器的 autorelease 池回收。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；Metal 调用经 <c>Apple.Shared</c> 互操作层（零反射 [LibraryImport]）。</para>
/// <para><b>分部划分</b>：Program 分部管 MSL 源码、管线编译与纹理上传。本文件为呈现主流程与生命周期。</para>
/// </remarks>
internal sealed unsafe partial class MetalShaderPipeline : IDisposable
{
    private readonly ILogger? _logger;
    private readonly nint _device;
    private readonly nint _queue;
    private bool _disposed;
    private bool _initialized;

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

    // 对外呈现

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
        AppleRuntime.objc_release(cb); // newCommandBuffer 返回 +1；commit 后由命令队列接管直至 GPU 完成，释放本类持有的 +1
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

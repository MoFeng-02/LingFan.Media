using System.Runtime.InteropServices;
using System.Text;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL Shader 渲染管线：GPU 双线性缩放 + YUV→RGB（BT.601 全范围）转换。
/// </summary>
/// <remarks>
/// <para><b>职责</b>：与 D3D11/Vulkan 同源 Shader 管线一致——帧尺寸 ≠ 渲染目标（GPU 缩放）或 YUV 像素格式
/// （YUV420P/YUV422P/YUV444P/NV12/NV21）时，走全屏 quad + 片元着色器采样呈现，替代纯纹理拷贝的路径限制。</para>
/// <para><b>平面布局</b>：与 <see cref="D3D11ShaderPipeline.UploadPlanes"/> 完全同源（FFmpeg 紧凑打包语义）：
/// Y 平面 w×h，色度平面按子采样推导，顺序 Y→U→V（或 Y→UV）；BGRA32/RGBA32 考虑 <see cref="SoftwareFrameResource.Stride"/>。</para>
/// <para><b>异步策略</b>：全部同步（native 分类）——Shader 编译与 GPU 提交均为同步原生调用，无 I/O await；
/// 包 async/Task.Run 即伪异步，禁止。</para>
/// <para><b>线程安全</b>：所有方法由 <see cref="OpenGLRenderer"/> 在其 <c>_gate</c> 锁内调用，本类不再加锁。</para>
/// <para><b>资源所有权</b>：GL 上下文由渲染器持有（不 Dispose）；Shader/VAO/帧纹理由本类持有，<see cref="Dispose"/> 释放。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；GL 调用经 <see cref="GLNative"/>（零反射 [LibraryImport]）。</para>
/// </remarks>
internal sealed unsafe class OpenGLShaderPipeline : IDisposable
{
    // ── GL 常量（桌面 GL 3.3 core）──
    private const int GlTexture2D = 0x0DE1;
    private const int GlRgba8 = 0x8058;
    private const int GlRgb8 = 0x8051;
    private const int GlR8 = 0x8229;
    private const int GlRg8 = 0x822B;
    private const int GlRed = 0x1903;
    private const int GlRg = 0x8227;
    private const int GlRgb = 0x1907;
    private const int GlRgba = 0x1908;
    private const int GlUnsignedByte = 0x1401;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlLinear = 0x2601;
    private const int GlClampToEdge = 0x812F;
    private const int GlTextureWrapS = 0x2802;
    private const int GlTextureWrapT = 0x2803;
    private const int GlTriangleStrip = 0x0005;
    private const int GlColorBufferBit = 0x00004000;
    private const int GlUnpackAlignment = 0x0CF5;
    private const int GlTexture0 = 0x84C0;
    private const int GlVertexShader = 0x8B31;
    private const int GlFragmentShader = 0x8B30;
    private const int GlCompileStatus = 0x8B81;
    private const int GlLinkStatus = 0x8B82;
    private const int GlFloat = 0x1406;
    private const int GlArrayBuffer = 0x8892;
    private const int GlStaticDraw = 0x88E4;

    private const string VertexShaderSource = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUV;
        out vec2 vUV;
        void main() {
            vUV = aUV;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    // RGB 路径：BGRA 数据经 .bgra 还原为显示顺序（与 D3D11 B8G8R8A8 直通语义一致）
    private const string RgbFragmentSource = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform sampler2D uTex;
        uniform int uIsBgra;
        void main() {
            vec4 c = texture(uTex, vUV);
            fragColor = (uIsBgra == 1) ? c.bgra : c;
        }
        """;

    // YUV 三平面（BT.601 全范围，系数与 D3D11 YuvToRgb 一致，色彩不漂移）
    private const string YuvFragmentSource = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform sampler2D uY;
        uniform sampler2D uU;
        uniform sampler2D uV;
        void main() {
            float y = texture(uY, vUV).r;
            float u = texture(uU, vUV).r;
            float v = texture(uV, vUV).r;
            float r = y + 1.402 * (v - 0.5);
            float g = y - 0.344136 * (u - 0.5) - 0.714136 * (v - 0.5);
            float b = y + 1.772 * (u - 0.5);
            fragColor = vec4(clamp(r, 0.0, 1.0), clamp(g, 0.0, 1.0), clamp(b, 0.0, 1.0), 1.0);
        }
        """;

    // NV12/NV21 半平面（UV 交错；uSwap 区分 NV12(U=R,V=G) 与 NV21(V=R,U=G)）
    private const string NvFragmentSource = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform sampler2D uY;
        uniform sampler2D uUV;
        uniform int uSwap;
        void main() {
            float y = texture(uY, vUV).r;
            vec2 uv = texture(uUV, vUV).rg;
            float u = (uSwap == 1) ? uv.g : uv.r;
            float v = (uSwap == 1) ? uv.r : uv.g;
            float r = y + 1.402 * (v - 0.5);
            float g = y - 0.344136 * (u - 0.5) - 0.714136 * (v - 0.5);
            float b = y + 1.772 * (u - 0.5);
            fragColor = vec4(clamp(r, 0.0, 1.0), clamp(g, 0.0, 1.0), clamp(b, 0.0, 1.0), 1.0);
        }
        """;

    private readonly ILogger? _logger;
    private bool _disposed;
    private bool _initialized;

    // 全屏 quad（triangle strip）：pos.xy + uv.xy；uv 已翻转使「图像顶 → 屏幕顶」（GL 纹理原点在左下）
    private static readonly float[] Quad =
    {
        -1f, -1f, 0f, 1f,
         1f, -1f, 1f, 1f,
        -1f,  1f, 0f, 0f,
         1f,  1f, 1f, 0f,
    };

    private uint _vao;
    private uint _vbo;

    private uint _rgbProgram;
    private int _rgbUTex;
    private int _rgbUIsBgra;
    private uint _yuvProgram;
    private int _yuvUY;
    private int _yuvUU;
    private int _yuvUV;
    private uint _nvProgram;
    private int _nvUY;
    private int _nvUUV;
    private int _nvUSwap;

    // ── 帧纹理缓存（0=Y/RGB，1=U/UV，2=V）──
    private readonly uint[] _planeTextures = new uint[3];
    private int _cachedWidth;
    private int _cachedHeight;
    private PixelFormat _cachedFormat = (PixelFormat)(-1);

    /// <summary>初始化 <see cref="OpenGLShaderPipeline"/>（仅保存日志器；GL 资源延迟到首次 <see cref="Present"/>，
    /// 彼时 GL 上下文已在渲染线程 current）。</summary>
    /// <remarks>构造函数不触碰任何 GL 调用——创建发生于 <see cref="OpenGLRenderer.Attach"/> 线程，
    /// 而该线程在 <see cref="WglContext"/> / <see cref="EglContext"/> 创建后已解绑上下文（线程亲和），
    /// 若此处编译 Shader 会因无 current 上下文而静默失败。</remarks>
    internal OpenGLShaderPipeline(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>判断像素格式是否为本管线支持的 YUV 格式。</summary>
    internal static bool IsYuvFormat(PixelFormat format) => format is
        PixelFormat.YUV420P or PixelFormat.YUV422P or PixelFormat.YUV444P or
        PixelFormat.NV12 or PixelFormat.NV21;

    // ── 初始化 ──

    /// <summary>延迟初始化 GL 资源（VAO / VBO / Shader 程序）。
    /// 必须在 GL 上下文 current 时调用——由 <see cref="OpenGLRenderer.Present"/> 在渲染线程绑定上下文后触发，
    /// 仅首次执行。</summary>
    private void EnsureInitialized()
    {
        if (_initialized) return;
        InitializeQuad();
        CompilePrograms();
        _initialized = true;
        _logger?.LogDebug("OpenGL Shader 管线 GL 资源初始化完成（VAO/VBO/3 套 Shader 程序）。");
    }

    private void InitializeQuad()
    {
        fixed (uint* vao = &_vao)
            GLNative.GenVertexArrays(1, vao);
        fixed (uint* vbo = &_vbo)
            GLNative.GenBuffers(1, vbo);

        GLNative.BindVertexArray(_vao);
        GLNative.BindBuffer(GlArrayBuffer, _vbo);

        fixed (float* p = Quad)
            GLNative.BufferData(GlArrayBuffer, (nuint)(Quad.Length * sizeof(float)), p, GlStaticDraw);

        // pos: location 0, 2 float, stride 16, offset 0
        GLNative.VertexAttribPointer(0, 2, GlFloat, false, 16, (void*)0);
        GLNative.EnableVertexAttribArray(0);
        // uv: location 1, 2 float, stride 16, offset 8
        GLNative.VertexAttribPointer(1, 2, GlFloat, false, 16, (void*)8);
        GLNative.EnableVertexAttribArray(1);

        GLNative.BindVertexArray(0);
    }

    private void CompilePrograms()
    {
        uint vs = CompileShader(GlVertexShader, VertexShaderSource);
        try
        {
            uint fsRgb = CompileShader(GlFragmentShader, RgbFragmentSource);
            uint fsYuv = CompileShader(GlFragmentShader, YuvFragmentSource);
            uint fsNv = CompileShader(GlFragmentShader, NvFragmentSource);

            _rgbProgram = LinkProgram(vs, fsRgb, out _rgbUTex, out _rgbUIsBgra, "uTex", "uIsBgra");
            _yuvProgram = LinkProgram(vs, fsYuv, out _yuvUY, out _yuvUU, "uY", "uU");
            // YUV 第三个 uniform 单取，避免 LinkProgram 多返回值爆炸
            _yuvUV = GLNative.GetUniformLocation(_yuvProgram, "uV");
            _nvProgram = LinkProgram(vs, fsNv, out _nvUY, out _nvUUV, "uY", "uUV");
            _nvUSwap = GLNative.GetUniformLocation(_nvProgram, "uSwap");

            GLNative.DeleteShader(fsRgb);
            GLNative.DeleteShader(fsYuv);
            GLNative.DeleteShader(fsNv);
        }
        finally
        {
            GLNative.DeleteShader(vs);
        }
    }

    private static uint CompileShader(int type, string src)
    {
        uint shader = GLNative.CreateShader((uint)type);
        byte[] bytes = Encoding.UTF8.GetBytes(src);
        byte[] withNull = new byte[bytes.Length + 1];
        global::System.Buffer.BlockCopy(bytes, 0, withNull, 0, bytes.Length);
        fixed (byte* p = withNull)
            GLNative.ShaderSource(shader, p);
        GLNative.CompileShader(shader);

        GLNative.GetShaderiv(shader, (uint)GlCompileStatus, out int status);
        if (status == 0)
        {
            byte[] logBuf = new byte[512];
            fixed (byte* lp = logBuf)
            {
                GLNative.GetShaderInfoLog(shader, 512, out int len, lp);
                string msg = Encoding.UTF8.GetString(logBuf, 0, Math.Max(0, len));
                GLNative.DeleteShader(shader);
                throw new InvalidOperationException($"OpenGL Shader 编译失败：{msg}");
            }
        }
        return shader;
    }

    private static uint LinkProgram(uint vs, uint fs, out int u0, out int u1, string u0Name, string u1Name)
    {
        uint program = GLNative.CreateProgram();
        GLNative.AttachShader(program, vs);
        GLNative.AttachShader(program, fs);
        GLNative.LinkProgram(program);
        GLNative.GetProgramiv(program, (uint)GlLinkStatus, out int status);
        if (status == 0)
        {
            byte[] logBuf = new byte[512];
            fixed (byte* lp = logBuf)
            {
                GLNative.GetProgramInfoLog(program, 512, out int len, lp);
                string msg = Encoding.UTF8.GetString(logBuf, 0, Math.Max(0, len));
                GLNative.DeleteProgram(program);
                throw new InvalidOperationException($"OpenGL Program 链接失败：{msg}");
            }
        }
        u0 = GLNative.GetUniformLocation(program, u0Name);
        u1 = GLNative.GetUniformLocation(program, u1Name);
        return program;
    }

    // ── 纹理管理 ──

    private void EnsureTextures(int width, int height, PixelFormat format)
    {
        if (_cachedWidth == width && _cachedHeight == height &&
            _cachedFormat == format && _planeTextures[0] != 0)
            return;

        ReleaseTextures();

        switch (format)
        {
            case PixelFormat.BGRA32:
            case PixelFormat.RGBA32:
                CreatePlane(0, width, height, GlRgba8, GlRgba);
                break;

            case PixelFormat.RGB24:
                CreatePlane(0, width, height, GlRgb8, GlRgb);
                break;

            case PixelFormat.YUV420P:
            case PixelFormat.YUV422P:
            case PixelFormat.YUV444P:
            {
                (int cw, int ch) = format switch
                {
                    PixelFormat.YUV444P => (width, height),
                    PixelFormat.YUV422P => ((width + 1) / 2, height),
                    _ => ((width + 1) / 2, (height + 1) / 2),
                };
                CreatePlane(0, width, height, GlR8, GlRed);
                CreatePlane(1, cw, ch, GlR8, GlRed);
                CreatePlane(2, cw, ch, GlR8, GlRed);
                break;
            }

            case PixelFormat.NV12:
            case PixelFormat.NV21:
            {
                int uvW = (width + 1) / 2;
                int uvH = (height + 1) / 2;
                CreatePlane(0, width, height, GlR8, GlRed);
                CreatePlane(1, uvW, uvH, GlRg8, GlRg);
                break;
            }

            default:
                throw new NotSupportedException($"OpenGL Shader 管线不支持像素格式 {format}。");
        }

        _cachedWidth = width;
        _cachedHeight = height;
        _cachedFormat = format;
    }

    private void CreatePlane(int index, int width, int height, int internalFormat, int format)
    {
        fixed (uint* p = &_planeTextures[index])
            GLNative.glGenTextures(1, p);
        uint tex = _planeTextures[index];
        GLNative.glBindTexture(GlTexture2D, tex);
        GLNative.glTexParameteri(GlTexture2D, GlTextureMinFilter, GlLinear);
        GLNative.glTexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
        GLNative.glTexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);
        GLNative.glTexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);
        // 仅分配（数据由 UploadPlanes 经 TexSubImage 填充）
        GLNative.glTexImage2D(GlTexture2D, 0, internalFormat, width, height, 0, (uint)format, GlUnsignedByte, null);
    }

    private static int BppToFormat(int bpp) => bpp switch
    {
        1 => GlRed,
        2 => GlRg,
        3 => GlRgb,
        _ => GlRgba,
    };

    private void UploadPlanes(SoftwareFrameResource sw)
    {
        GLNative.glPixelStorei(GlUnpackAlignment, 1); // 单通道平面逐行按实际宽度读取，避免 4 字节对齐错位
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
                    UploadPlane(_planeTextures[0], w, h, bpp, stride, GlRgba, basePtr);
                    break;
                }

                case PixelFormat.RGB24:
                {
                    int bpp = 3;
                    int stride = sw.Stride > 0 ? sw.Stride : w * bpp;
                    UploadPlane(_planeTextures[0], w, h, bpp, stride, GlRgb, basePtr);
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
                    UploadPlane(_planeTextures[0], w, h, 1, w, GlRed, basePtr);
                    UploadPlane(_planeTextures[1], cw, ch, 1, cw, GlRed, basePtr + ySize);
                    UploadPlane(_planeTextures[2], cw, ch, 1, cw, GlRed, basePtr + ySize + cSize);
                    break;
                }

                case PixelFormat.NV12:
                case PixelFormat.NV21:
                {
                    int ySize = w * h;
                    int uvW = (w + 1) / 2;
                    int uvH = (h + 1) / 2;
                    UploadPlane(_planeTextures[0], w, h, 1, w, GlRed, basePtr);
                    UploadPlane(_planeTextures[1], uvW, uvH, 2, uvW * 2, GlRg, basePtr + ySize);
                    break;
                }

                default:
                    throw new NotSupportedException($"OpenGL Shader 管线不支持像素格式 {sw.Format}。");
            }
        }
    }

    private static void UploadPlane(uint tex, int w, int h, int bpp, int stride, int format, byte* data)
    {
        GLNative.glBindTexture(GlTexture2D, tex);
        int glFormat = BppToFormat(bpp);
        if (stride == w * bpp || stride == 0)
        {
            GLNative.glTexSubImage2D(GlTexture2D, 0, 0, 0, w, h, (uint)glFormat, GlUnsignedByte, data);
        }
        else
        {
            // 零拷贝对齐 stride（Stride > w*bpp）：逐行上传
            for (int y = 0; y < h; y++)
                GLNative.glTexSubImage2D(GlTexture2D, 0, 0, y, w, 1, (uint)glFormat, GlUnsignedByte, data + y * stride);
        }
    }

    // ── 对外呈现 ──

    /// <summary>用 Shader 路径将软件帧呈现到当前 GL 帧缓冲（不交换缓冲，由调用方 SwapBuffers）。</summary>
    internal void Present(SoftwareFrameResource sw, int dstWidth, int dstHeight, AspectRatioMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sw);

        EnsureInitialized();
        EnsureTextures(sw.Width, sw.Height, sw.Format);
        UploadPlanes(sw);

        // 按 ScaleMode 计算目标视口矩形（top-left 原点）：Uniform 留黑边、UniformToFill 居中溢出裁剪
        ComputeScaleRects(sw.Width, sw.Height, dstWidth, dstHeight, mode,
            out int vx, out int vy, out int vw, out int vh);

        // 先清空整帧缓冲为黑（letterbox 黑边靠此；Fill/UniformToFill 全幅覆盖，清屏无害）
        GLNative.glViewport(0, 0, dstWidth, dstHeight);
        GLNative.glClearColor(0f, 0f, 0f, 1f);
        GLNative.glClear(GlColorBufferBit);

        // GL 视口原点在左下 → 翻转 Y 使 top-left 矩形映射到正确位置
        GLNative.glViewport(vx, dstHeight - vy - vh, vw, vh);
        GLNative.BindVertexArray(_vao);

        switch (sw.Format)
        {
            case PixelFormat.BGRA32:
            case PixelFormat.RGBA32:
            case PixelFormat.RGB24:
                GLNative.UseProgram(_rgbProgram);
                GLNative.ActiveTexture((uint)(GlTexture0 + 0));
                GLNative.glBindTexture(GlTexture2D, _planeTextures[0]);
                GLNative.Uniform1i(_rgbUTex, 0);
                // RGB24 为直序 RGB（无需 BGR 交换）；仅 BGRA32 经 .bgra 还原显示顺序。
                GLNative.Uniform1i(_rgbUIsBgra, sw.Format == PixelFormat.BGRA32 ? 1 : 0);
                GLNative.glDrawArrays(GlTriangleStrip, 0, 4);
                break;

            case PixelFormat.YUV420P:
            case PixelFormat.YUV422P:
            case PixelFormat.YUV444P:
                GLNative.UseProgram(_yuvProgram);
                BindPlane(0, _planeTextures[0], _yuvUY);
                BindPlane(1, _planeTextures[1], _yuvUU);
                BindPlane(2, _planeTextures[2], _yuvUV);
                GLNative.glDrawArrays(GlTriangleStrip, 0, 4);
                break;

            case PixelFormat.NV12:
            case PixelFormat.NV21:
                GLNative.UseProgram(_nvProgram);
                BindPlane(0, _planeTextures[0], _nvUY);
                BindPlane(1, _planeTextures[1], _nvUUV);
                GLNative.Uniform1i(_nvUSwap, sw.Format == PixelFormat.NV21 ? 1 : 0);
                GLNative.glDrawArrays(GlTriangleStrip, 0, 4);
                break;

            default:
                throw new NotSupportedException(
                    $"OpenGL Shader 管线不支持像素格式 {sw.Format}。支持 BGRA32/RGBA32/RGB24/YUV420P/YUV422P/YUV444P/NV12/NV21。");
        }
    }

    private static void BindPlane(int unit, uint tex, int uniformLoc)
    {
        GLNative.ActiveTexture((uint)(GlTexture0 + unit));
        GLNative.glBindTexture(GlTexture2D, tex);
        GLNative.Uniform1i(uniformLoc, unit);
    }

    /// <summary>
    /// 按 <see cref="AspectRatioMode"/> 计算软帧→目标（top-left 原点）的目标视口矩形。
    /// <list type="bullet">
    /// <item><see cref="AspectRatioMode.Fill"/>：拉伸填满（整目标）。</item>
    /// <item><see cref="AspectRatioMode.Uniform"/>：保持比例、居中、留黑边（子矩形）。</item>
    /// <item><see cref="AspectRatioMode.UniformToFill"/>：保持比例、居中、溢出裁剪（大于目标的矩形，仅中心可见）。</item>
    /// </list>
    /// 纯视口数学，无需改动 Shader（Full→均匀覆盖、溢出→天然裁中心），与 Vulkan/Skia 语义一致。
    /// </summary>
    private static void ComputeScaleRects(
        int srcW, int srcH, int dstW, int dstH, AspectRatioMode mode,
        out int x, out int y, out int w, out int h)
    {
        x = 0; y = 0; w = dstW; h = dstH;
        if (srcW <= 0 || srcH <= 0) return;

        switch (mode)
        {
            case AspectRatioMode.Uniform:
            {
                double fit = Math.Min((double)dstW / srcW, (double)dstH / srcH);
                w = Math.Max(1, (int)(srcW * fit + 0.5));
                h = Math.Max(1, (int)(srcH * fit + 0.5));
                x = (dstW - w) / 2;
                y = (dstH - h) / 2;
                break;
            }
            case AspectRatioMode.UniformToFill:
            {
                double cover = Math.Max((double)dstW / srcW, (double)dstH / srcH);
                w = Math.Max(1, (int)(srcW * cover + 0.5));
                h = Math.Max(1, (int)(srcH * cover + 0.5));
                x = (dstW - w) / 2;
                y = (dstH - h) / 2;
                break;
            }
            case AspectRatioMode.Fill:
            default:
                break;
        }
    }

    /// <summary>清除当前 GL 帧缓冲为黑色（不交换缓冲，由调用方 SwapBuffers）。</summary>
    internal void Clear(int dstWidth, int dstHeight)
    {
        GLNative.glViewport(0, 0, dstWidth, dstHeight);
        GLNative.glClearColor(0f, 0f, 0f, 1f);
        GLNative.glClear(GlColorBufferBit);
    }

    private void ReleaseTextures()
    {
        for (int i = 0; i < _planeTextures.Length; i++)
        {
            if (_planeTextures[i] != 0)
            {
                fixed (uint* p = &_planeTextures[i])
                    GLNative.glDeleteTextures(1, p);
                _planeTextures[i] = 0;
            }
        }
        _cachedWidth = 0;
        _cachedHeight = 0;
        _cachedFormat = (PixelFormat)(-1);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseTextures();
        if (_rgbProgram != 0) GLNative.DeleteProgram(_rgbProgram);
        if (_yuvProgram != 0) GLNative.DeleteProgram(_yuvProgram);
        if (_nvProgram != 0) GLNative.DeleteProgram(_nvProgram);
        _rgbProgram = _yuvProgram = _nvProgram = 0;
        if (_vao != 0) { fixed (uint* p = &_vao) GLNative.DeleteVertexArrays(1, p); }
        if (_vbo != 0) { fixed (uint* p = &_vbo) GLNative.DeleteBuffers(1, p); }
        _vao = _vbo = 0;
    }
}

using System.Runtime.InteropServices;
using System.Text;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL Shader 渲染管线：GPU 双线性缩放 + YUV→RGB（BT.601 全范围）转换。
/// </summary>
/// <remarks>
/// <para><b>职责</b>：与 D3D11/Vulkan 同源 Shader 管线一致——帧尺寸 ≠ 渲染目标（GPU 缩放）或 YUV 像素格式
/// （YUV420P/YUV422P/YUV444P/NV12/NV21）时，走全屏 quad + 片元着色器采样呈现，替代纯纹理拷贝的路径限制。</para>
/// <para><b>平面布局</b>：与 <c>D3D11ShaderPipeline.UploadPlanes</c> 完全同源（FFmpeg 紧凑打包语义）：
/// Y 平面 w×h，色度平面按子采样推导，顺序 Y→U→V（或 Y→UV）；BGRA32/RGBA32 考虑 <see cref="SoftwareFrameResource.Stride"/>。</para>
/// <para><b>异步策略</b>：全部同步——Shader 编译与 GPU 提交均为同步原生调用，无 I/O await；
/// 包 async/Task.Run 即伪异步，禁止。</para>
/// <para><b>线程安全</b>：所有方法由 <see cref="OpenGLRenderer"/> 在其 <c>_gate</c> 锁内调用，本类不再加锁。</para>
/// <para><b>资源所有权</b>：GL 上下文由渲染器持有（不 Dispose）；Shader/VAO/帧纹理由本类持有，<see cref="Dispose"/> 释放。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；GL 调用经 <see cref="GLNative"/>（零反射 [LibraryImport]）。</para>
/// </remarks>
internal sealed unsafe partial class OpenGLShaderPipeline : IDisposable
{
    // GL 常量（桌面 GL 3.3 core）
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
    private const uint GlVersion = 0x1F02;
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

    // 帧纹理缓存（0=Y/RGB，1=U/UV，2=V）
    private readonly uint[] _planeTextures = new uint[3];
    private int _cachedWidth;
    private int _cachedHeight;
    private PixelFormat _cachedFormat = (PixelFormat)(-1);

    // WGL_NV_DX_interop2 每绘制上下文缓存
    // 由 on-screen GL 上下文打开，与该上下文同生同死；绘制时现场 register/lock/draw/unlock/unregister，
    // 避免 owner 离屏上下文与 on-screen 上下文对同一 WGL 互操作对象的跨上下文歧义。
    private ID3D11Device? _wglBridgeDevice;
    private nint _wglInteropDevice;

    /// <summary>初始化 <see cref="OpenGLShaderPipeline"/>（仅保存日志器；GL 资源延迟到首次 <see cref="Present"/>，
    /// 彼时 GL 上下文已在渲染线程 current）。</summary>
    /// <remarks>构造函数不触碰任何 GL 调用——创建发生于 <see cref="OpenGLRenderer.Attach"/> 线程，
    /// 而该线程在 <see cref="Context.WglContext"/> / <see cref="Context.EglContext"/> 创建后已解绑上下文（线程亲和），
    /// 若此处编译 Shader 会因无 current 上下文而静默失败。</remarks>
    internal OpenGLShaderPipeline(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>判断像素格式是否为本管线支持的 YUV 格式。</summary>
    internal static bool IsYuvFormat(PixelFormat format) => format is
        PixelFormat.YUV420P or PixelFormat.YUV422P or PixelFormat.YUV444P or
        PixelFormat.NV12 or PixelFormat.NV21;

    // 初始化

    // 主时钟外的上下文类型：GLES 上下文（GL_VERSION 以 "OpenGL ES" 开头）使用 GLSL ES 3.00 着色器变体。
    // Mesa GLES-only 栈（Xvfb/部分板卡 GPU）不提供桌面 GLSL 330，写死 330 core 会直接编译失败。
    private bool _isGles;

    /// <summary>延迟初始化 GL 资源（VAO / VBO / Shader 程序）。
    /// 必须在 GL 上下文 current 时调用——由 <see cref="OpenGLRenderer.Present"/> 在渲染线程绑定上下文后触发，
    /// 仅首次执行。</summary>
    private void EnsureInitialized()
    {
        if (_initialized) return;
        nint verPtr = GLNative.glGetString(GlVersion);
        string glVersion = Marshal.PtrToStringUTF8(verPtr) ?? string.Empty;
        _isGles = glVersion.StartsWith("OpenGL ES", StringComparison.OrdinalIgnoreCase);
        if (_isGles)
            _logger?.LogInformation(
                "OpenGL ES 上下文检测到（{Version}），Shader 使用 GLSL ES 3.00 变体。", glVersion);
        InitializeQuad();
        CompilePrograms();
        _initialized = true;
        _logger?.LogDebug("OpenGL Shader 管线 GL 资源初始化完成（VAO/VBO/3 套 Shader 程序）。");
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

        if (_wglInteropDevice != nint.Zero)
        {
            GLNative.WglDXCloseDeviceNV(_wglInteropDevice);
            _wglInteropDevice = nint.Zero;
            _wglBridgeDevice = null;
        }
    }
}

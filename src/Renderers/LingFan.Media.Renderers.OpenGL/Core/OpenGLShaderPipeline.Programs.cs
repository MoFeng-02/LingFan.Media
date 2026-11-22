using System.Runtime.InteropServices;
using System.Text;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.Renderers.OpenGL;

// OpenGLShaderPipeline 的 Programs 职责段（部分类，由 OpenGLShaderPipeline.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/EnsureInitialized/Dispose）在主文件；全部同步原生调用。
internal sealed unsafe partial class OpenGLShaderPipeline
{
    /// <summary>按上下文类型改写着色器源：桌面 GL 原样；GLES 上下文换用 GLSL ES 3.00
    /// （版本行替换 + fragment 阶段补 <c>precision highp float;</c>——GLSL ES 3.00 的 fragment
    /// 必须显式声明 float 精度）。其余语法（in/out、layout location、texture()、swizzle）两方言同构。</summary>
    private string AdaptShaderSource(string src, bool fragment)
    {
        if (!_isGles) return src;
        string es = src.Replace("#version 330 core", "#version 300 es");
        if (fragment)
        {
            int versionLineEnd = es.IndexOf('\n');
            es = es.Insert(versionLineEnd + 1, "precision highp float;\n");
        }
        return es;
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
        uint vs = CompileShader(GlVertexShader, AdaptShaderSource(VertexShaderSource, fragment: false));
        try
        {
            uint fsRgb = CompileShader(GlFragmentShader, AdaptShaderSource(RgbFragmentSource, fragment: true));
            uint fsYuv = CompileShader(GlFragmentShader, AdaptShaderSource(YuvFragmentSource, fragment: true));
            uint fsNv = CompileShader(GlFragmentShader, AdaptShaderSource(NvFragmentSource, fragment: true));

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

    // 纹理管理
}

using System.Text;

namespace LingFan.Media.Renderers.OpenGLES;

// GlesShaderPipeline 的着色器程序职责段（部分类，按职责拆分，编译期等价）。
// 全屏 quad 初始化与三套 Shader 程序（RGB/YUV/NV12）编译链接；字段与呈现编排在主文件。
// 全部同步 GLES 原生调用（调用方持渲染器锁）。
internal sealed unsafe partial class GlesShaderPipeline
{
    private void InitializeQuad()
    {
        fixed (uint* vao = &_vao)
            GlesNative.glGenVertexArrays(1, vao);
        fixed (uint* vbo = &_vbo)
            GlesNative.glGenBuffers(1, vbo);

        GlesNative.glBindVertexArray(_vao);
        GlesNative.glBindBuffer(GlArrayBuffer, _vbo);

        fixed (float* p = Quad)
            GlesNative.glBufferData(GlArrayBuffer, (nuint)(Quad.Length * sizeof(float)), p, GlStaticDraw);

        // pos: location 0, 2 float, stride 16, offset 0
        GlesNative.glVertexAttribPointer(0, 2, GlFloat, (byte)0, 16, (void*)0);
        GlesNative.glEnableVertexAttribArray(0);
        // uv: location 1, 2 float, stride 16, offset 8
        GlesNative.glVertexAttribPointer(1, 2, GlFloat, (byte)0, 16, (void*)8);
        GlesNative.glEnableVertexAttribArray(1);

        GlesNative.glBindVertexArray(0);
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
            _yuvUV = GetUniformLoc(_yuvProgram, "uV");
            _nvProgram = LinkProgram(vs, fsNv, out _nvUY, out _nvUUV, "uY", "uUV");
            _nvUSwap = GetUniformLoc(_nvProgram, "uSwap");

            GlesNative.glDeleteShader(fsRgb);
            GlesNative.glDeleteShader(fsYuv);
            GlesNative.glDeleteShader(fsNv);
        }
        finally
        {
            GlesNative.glDeleteShader(vs);
        }
    }

    private static byte[] StringToUtf8(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        byte[] withNull = new byte[bytes.Length + 1];
        global::System.Buffer.BlockCopy(bytes, 0, withNull, 0, bytes.Length);
        return withNull;
    }

    private static int GetUniformLoc(uint program, string name)
    {
        byte[] bytes = StringToUtf8(name);
        fixed (byte* p = bytes)
            return GlesNative.glGetUniformLocation(program, p);
    }

    private static uint CompileShader(int type, string src)
    {
        uint shader = GlesNative.glCreateShader((uint)type);
        byte[] withNull = StringToUtf8(src);
        fixed (byte* p = withNull)
        {
            int len = -1; // 源码以 NUL 终止，GLES 自行判定长度
            GlesNative.glShaderSource(shader, 1, &p, &len);
        }
        GlesNative.glCompileShader(shader);

        int status;
        GlesNative.glGetShaderiv(shader, (uint)GlCompileStatus, &status);
        if (status == 0)
        {
            byte[] logBuf = new byte[512];
            int logLen;
            fixed (byte* lp = logBuf)
            {
                GlesNative.glGetShaderInfoLog(shader, 512, &logLen, lp);
                string msg = Encoding.UTF8.GetString(logBuf, 0, Math.Max(0, logLen));
                GlesNative.glDeleteShader(shader);
                throw new InvalidOperationException($"OpenGLES Shader 编译失败：{msg}");
            }
        }
        return shader;
    }

    private static uint LinkProgram(uint vs, uint fs, out int u0, out int u1, string u0Name, string u1Name)
    {
        uint program = GlesNative.glCreateProgram();
        GlesNative.glAttachShader(program, vs);
        GlesNative.glAttachShader(program, fs);
        GlesNative.glLinkProgram(program);
        int status;
        GlesNative.glGetProgramiv(program, (uint)GlLinkStatus, &status);
        if (status == 0)
        {
            byte[] logBuf = new byte[512];
            int logLen;
            fixed (byte* lp = logBuf)
            {
                GlesNative.glGetProgramInfoLog(program, 512, &logLen, lp);
                string msg = Encoding.UTF8.GetString(logBuf, 0, Math.Max(0, logLen));
                GlesNative.glDeleteProgram(program);
                throw new InvalidOperationException($"OpenGLES Program 链接失败：{msg}");
            }
        }
        u0 = GetUniformLoc(program, u0Name);
        u1 = GetUniformLoc(program, u1Name);
        return program;
    }
}
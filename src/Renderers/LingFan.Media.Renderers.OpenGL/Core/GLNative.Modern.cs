using System.Text;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// <see cref="GLNative"/> 的 GL 1.2+ 现代函数封装（着色器 / VBO / VAO 渲染路径）。
/// </summary>
/// <remarks>
/// <para>每个封装方法在调用前经 <see cref="EnsureModern"/> 校验 <see cref="GLNative.LoadModern"/> 已执行，
/// 防止在 GL 上下文建立前误用现代函数（届时函数指针为 <see langword="null"/>）。</para>
/// <para>字符串参数（<see cref="GetAttribLocation"/> / <see cref="GetUniformLocation"/>）在托管侧编码为
/// NUL 终止 UTF-8 字节后传入，不依赖运行期 marshaller，AOT 安全。</para>
/// </remarks>
internal static unsafe partial class GLNative
{
    private static void EnsureModern()
    {
        if (!_modernLoaded)
            throw new InvalidOperationException("GLNative 现代函数尚未解析。请先在 GL 上下文建立后调用 LoadModern()。");
    }

    public static void ActiveTexture(uint texture)
    {
        EnsureModern();
        _glActiveTexture(texture);
    }

    public static unsafe void GenBuffers(int n, uint* buffers)
    {
        EnsureModern();
        _glGenBuffers(n, buffers);
    }

    public static void BindBuffer(uint target, uint buffer)
    {
        EnsureModern();
        _glBindBuffer(target, buffer);
    }

    public static unsafe void BufferData(uint target, nuint size, void* data, uint usage)
    {
        EnsureModern();
        _glBufferData(target, size, data, usage);
    }

    public static unsafe void BufferSubData(uint target, nint offset, nuint size, void* data)
    {
        EnsureModern();
        _glBufferSubData(target, offset, size, data);
    }

    public static unsafe void DeleteBuffers(int n, uint* buffers)
    {
        EnsureModern();
        _glDeleteBuffers(n, buffers);
    }

    public static uint CreateShader(uint type)
    {
        EnsureModern();
        return _glCreateShader(type);
    }

    public static unsafe void ShaderSource(uint shader, byte* source)
    {
        EnsureModern();
        int count = 1;
        int length = -1; // 源码以 NUL 终止，GL 自行判定长度
        _glShaderSource(shader, count, &source, &length);
    }

    public static void CompileShader(uint shader)
    {
        EnsureModern();
        _glCompileShader(shader);
    }

    public static unsafe void GetShaderiv(uint shader, uint pname, out int param)
    {
        EnsureModern();
        int tmp;
        _glGetShaderiv(shader, pname, &tmp);
        param = tmp;
    }

    public static unsafe void GetShaderInfoLog(uint shader, int bufSize, out int length, byte* infoLog)
    {
        EnsureModern();
        int len;
        _glGetShaderInfoLog(shader, bufSize, &len, infoLog);
        length = len;
    }

    public static void DeleteShader(uint shader)
    {
        EnsureModern();
        _glDeleteShader(shader);
    }

    public static uint CreateProgram()
    {
        EnsureModern();
        return _glCreateProgram();
    }

    public static void AttachShader(uint program, uint shader)
    {
        EnsureModern();
        _glAttachShader(program, shader);
    }

    public static void LinkProgram(uint program)
    {
        EnsureModern();
        _glLinkProgram(program);
    }

    public static unsafe void GetProgramiv(uint program, uint pname, out int param)
    {
        EnsureModern();
        int tmp;
        _glGetProgramiv(program, pname, &tmp);
        param = tmp;
    }

    public static unsafe void GetProgramInfoLog(uint program, int bufSize, out int length, byte* infoLog)
    {
        EnsureModern();
        int len;
        _glGetProgramInfoLog(program, bufSize, &len, infoLog);
        length = len;
    }

    public static void DeleteProgram(uint program)
    {
        EnsureModern();
        _glDeleteProgram(program);
    }

    public static void UseProgram(uint program)
    {
        EnsureModern();
        _glUseProgram(program);
    }

    public static unsafe int GetAttribLocation(uint program, string name)
    {
        EnsureModern();
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        byte[] withNull = new byte[bytes.Length + 1];
        global::System.Buffer.BlockCopy(bytes, 0, withNull, 0, bytes.Length);
        withNull[bytes.Length] = 0;
        fixed (byte* p = withNull)
            return _glGetAttribLocation(program, p);
    }

    public static unsafe int GetUniformLocation(uint program, string name)
    {
        EnsureModern();
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        byte[] withNull = new byte[bytes.Length + 1];
        global::System.Buffer.BlockCopy(bytes, 0, withNull, 0, bytes.Length);
        withNull[bytes.Length] = 0;
        fixed (byte* p = withNull)
            return _glGetUniformLocation(program, p);
    }

    public static void EnableVertexAttribArray(uint index)
    {
        EnsureModern();
        _glEnableVertexAttribArray(index);
    }

    public static unsafe void VertexAttribPointer(uint index, int size, uint type, bool normalized, int stride, void* ptr)
    {
        EnsureModern();
        _glVertexAttribPointer(index, size, type, (byte)(normalized ? 1 : 0), stride, ptr);
    }

    public static void Uniform1i(int location, int v)
    {
        EnsureModern();
        _glUniform1i(location, v);
    }

    public static unsafe void UniformMatrix4fv(int location, int count, bool transpose, float* value)
    {
        EnsureModern();
        _glUniformMatrix4fv(location, count, (byte)(transpose ? 1 : 0), value);
    }

    public static unsafe void GenVertexArrays(int n, uint* arrays)
    {
        EnsureModern();
        _glGenVertexArrays(n, arrays);
    }

    public static void BindVertexArray(uint array)
    {
        EnsureModern();
        _glBindVertexArray(array);
    }

    public static unsafe void DeleteVertexArrays(int n, uint* arrays)
    {
        EnsureModern();
        _glDeleteVertexArrays(n, arrays);
    }
}

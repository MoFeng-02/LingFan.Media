using System.Reflection;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.OpenGLES;

/// <summary>
/// 零反射 OpenGL ES 原生绑定层（替代 Silk.NET.OpenGL 的运行期包装）。
/// </summary>
/// <remarks>
/// <para><b>设计目标</b>：彻底消除 Silk.NET.OpenGL 绑定层的反射——不使用 Silk.NET 运行期 marshaller、
/// 不依赖 SharpGen 运行时 vtable 包装。NativeAOT 下零 IL2xxx。</para>
/// <para><b>与桌面 GL（<see cref="LingFan.Media.Renderers.OpenGL"/> 的 GLNative）的本质差异</b>：
/// 桌面 GL 1.1 的 thunk（opengl32.dll / libGL.so.1）导出表<b>不含</b>着色器/VBO/VAO 等现代函数，
/// 必须经 <c>wglGetProcAddress</c> / <c>eglGetProcAddress</c> 运行时解析（GLNative 的 LoadModern 三段式）。
/// 而 OpenGL ES（libGLESv2.so）的<b>导出表直接包含</b> <c>glCreateShader</c> / <c>glGenVertexArrays</c> 等
/// GLES 2.0+/3.0 core 全部符号——无分层，故本绑定<b>全部</b>以 <c>[LibraryImport]</c> 在加载期解析，
/// 无 GetProcAddress 中转、无 delegate* 字段缓存，更简洁且 AOT 更干净。</para>
/// <para><b>跨平台库名</b>：经 <see cref="NativeLibrary.SetDllImportResolver"/> 把中性名重定向——
/// <c>"GLES"</c> → Android <c>libGLESv2.so</c>（GLES 库名，桌面 GL 不叫这个）；
/// <c>"EGL"</c> → Linux <c>libEGL.so.1</c> / Android 裸 <c>libEGL.so</c>（EGL 引导符号）。
/// 本绑定仅覆盖 Android（GLES 上屏）；桌面 GL 由 <see cref="LingFan.Media.Renderers.OpenGL"/> 的 GLNative 覆盖、
/// Apple 由 Metal 后端覆盖。非 Android 调用方以 <see cref="OperatingSystem.IsAndroid"/> 守卫，永不被触发。</para>
/// <para><b>调用约定</b>：EGL / GLES 在 Android 为 C 默认（cdecl）；<c>[LibraryImport]</c> 默认 <c>Winapi</c> 在
/// ARM64（Android 主流）上 ABI 单一、等价于 cdecl。函数指针统一 <c>delegate* unmanaged</c>（默认 Winapi），
/// 不指定 <c>[Stdcall]</c>（与 GLNative 的 GL 基线同构）。</para>
/// </remarks>
internal static unsafe partial class GlesNative
{
    // ── 中性库名重定向：GLES(Android) / EGL(Linux+Android) ──
    static GlesNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(GlesNative).Assembly, ResolveGlesLoader);
    }

    private static nint ResolveGlesLoader(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // 中性名 "GLES"（OpenGL ES）：仅 Android 上 libGLESv2.so 提供；桌面 GL 不叫此名、Apple 不用 GL。
        // 非 Android 调用方以 OperatingSystem.IsAndroid() 守卫，永不被触发；返回 Zero 交回默认解析（必失败，fail-fast）。
        if (string.Equals(libraryName, "GLES", StringComparison.Ordinal))
        {
            if (OperatingSystem.IsAndroid())
                return NativeLibrary.TryLoad("libGLESv2.so", assembly, searchPath, out nint h) ? h : nint.Zero;
            return nint.Zero;
        }

        // 中性名 "EGL"：Linux 桌面 EGL(libEGL.so.1) / Android 裸 libEGL.so（供 Android GLES 上下文路径）。
        // 仅在这些平台被构造（调用方以对应 OS 守卫）。Windows/macOS/iOS 上交回默认解析（EGL 绑定永不被调用）。
        if (string.Equals(libraryName, "EGL", StringComparison.Ordinal))
        {
            if (OperatingSystem.IsLinux())
                return NativeLibrary.TryLoad("libEGL.so.1", assembly, searchPath, out nint h) ? h : nint.Zero;
            if (OperatingSystem.IsAndroid())
                return NativeLibrary.TryLoad("libEGL.so", assembly, searchPath, out nint h) ? h : nint.Zero;
            return nint.Zero;
        }

        return nint.Zero;
    }

    // ── EGL 引导符号（仅 Android 调用；EGL 句柄类型按 ABI 映射为 nint，EGLint 用 int）──

    [LibraryImport("EGL", EntryPoint = "eglBindAPI")]
    public static partial int eglBindAPI(uint api);

    [LibraryImport("EGL", EntryPoint = "eglGetDisplay")]
    public static partial nint eglGetDisplay(nint displayId);

    [LibraryImport("EGL", EntryPoint = "eglInitialize")]
    public static partial int eglInitialize(nint display, int* major, int* minor);

    [LibraryImport("EGL", EntryPoint = "eglChooseConfig")]
    public static partial int eglChooseConfig(nint display, int* attribList, nint* configs, int configSize, int* numConfig);

    [LibraryImport("EGL", EntryPoint = "eglCreateWindowSurface")]
    public static partial nint eglCreateWindowSurface(nint display, nint config, nint window, int* attribList);

    [LibraryImport("EGL", EntryPoint = "eglCreateContext")]
    public static partial nint eglCreateContext(nint display, nint config, nint shareContext, int* attribList);

    [LibraryImport("EGL", EntryPoint = "eglMakeCurrent")]
    public static partial int eglMakeCurrent(nint display, nint draw, nint read, nint context);

    [LibraryImport("EGL", EntryPoint = "eglSwapBuffers")]
    public static partial int eglSwapBuffers(nint display, nint surface);

    [LibraryImport("EGL", EntryPoint = "eglGetError")]
    public static partial int eglGetError();

    [LibraryImport("EGL", EntryPoint = "eglDestroyContext")]
    public static partial int eglDestroyContext(nint display, nint context);

    [LibraryImport("EGL", EntryPoint = "eglDestroySurface")]
    public static partial int eglDestroySurface(nint display, nint surface);

    [LibraryImport("EGL", EntryPoint = "eglTerminate")]
    public static partial int eglTerminate(nint display);

    // ── GLES 1.1 基线 + 现代函数（glCreateShader / glGenVertexArrays 等 GLES 2.0+/3.0 core 直接由 libGLESv2 导出，加载期解析）──

    [LibraryImport("GLES", EntryPoint = "glClear")]
    public static partial void glClear(uint mask);

    [LibraryImport("GLES", EntryPoint = "glClearColor")]
    public static partial void glClearColor(float red, float green, float blue, float alpha);

    [LibraryImport("GLES", EntryPoint = "glViewport")]
    public static partial void glViewport(int x, int y, int width, int height);

    [LibraryImport("GLES", EntryPoint = "glEnable")]
    public static partial void glEnable(uint cap);

    [LibraryImport("GLES", EntryPoint = "glDisable")]
    public static partial void glDisable(uint cap);

    [LibraryImport("GLES", EntryPoint = "glGetError")]
    public static partial uint glGetError();

    [LibraryImport("GLES", EntryPoint = "glFlush")]
    public static partial void glFlush();

    [LibraryImport("GLES", EntryPoint = "glFinish")]
    public static partial void glFinish();

    [LibraryImport("GLES", EntryPoint = "glGetString")]
    public static partial nint glGetString(uint name);

    [LibraryImport("GLES", EntryPoint = "glGetIntegerv")]
    public static partial void glGetIntegerv(uint pname, int* data);

    [LibraryImport("GLES", EntryPoint = "glPixelStorei")]
    public static partial void glPixelStorei(uint pname, int param);

    [LibraryImport("GLES", EntryPoint = "glGenTextures")]
    public static partial void glGenTextures(int n, uint* textures);

    [LibraryImport("GLES", EntryPoint = "glBindTexture")]
    public static partial void glBindTexture(uint target, uint texture);

    [LibraryImport("GLES", EntryPoint = "glTexImage2D")]
    public static partial void glTexImage2D(uint target, int level, int internalformat, int width, int height, int border, uint format, uint type, void* pixels);

    [LibraryImport("GLES", EntryPoint = "glTexSubImage2D")]
    public static partial void glTexSubImage2D(uint target, int level, int xoffset, int yoffset, int width, int height, uint format, uint type, void* pixels);

    [LibraryImport("GLES", EntryPoint = "glTexParameteri")]
    public static partial void glTexParameteri(uint target, uint pname, int param);

    [LibraryImport("GLES", EntryPoint = "glDeleteTextures")]
    public static partial void glDeleteTextures(int n, uint* textures);

    [LibraryImport("GLES", EntryPoint = "glDrawArrays")]
    public static partial void glDrawArrays(uint mode, int first, int count);

    // ── GLES 现代函数（着色器 / VBO / VAO；GLES 2.0+/3.0 core 直接导出，加载期解析）──

    [LibraryImport("GLES", EntryPoint = "glActiveTexture")]
    public static partial void glActiveTexture(uint texture);

    [LibraryImport("GLES", EntryPoint = "glGenBuffers")]
    public static partial void glGenBuffers(int n, uint* buffers);

    [LibraryImport("GLES", EntryPoint = "glBindBuffer")]
    public static partial void glBindBuffer(uint target, uint buffer);

    [LibraryImport("GLES", EntryPoint = "glBufferData")]
    public static partial void glBufferData(uint target, nuint size, void* data, uint usage);

    [LibraryImport("GLES", EntryPoint = "glBufferSubData")]
    public static partial void glBufferSubData(uint target, nint offset, nuint size, void* data);

    [LibraryImport("GLES", EntryPoint = "glDeleteBuffers")]
    public static partial void glDeleteBuffers(int n, uint* buffers);

    [LibraryImport("GLES", EntryPoint = "glCreateShader")]
    public static partial uint glCreateShader(uint type);

    [LibraryImport("GLES", EntryPoint = "glShaderSource")]
    public static partial void glShaderSource(uint shader, int count, byte** @string, int* length);

    [LibraryImport("GLES", EntryPoint = "glCompileShader")]
    public static partial void glCompileShader(uint shader);

    [LibraryImport("GLES", EntryPoint = "glGetShaderiv")]
    public static partial void glGetShaderiv(uint shader, uint pname, int* param);

    [LibraryImport("GLES", EntryPoint = "glGetShaderInfoLog")]
    public static partial void glGetShaderInfoLog(uint shader, int bufSize, int* length, byte* infoLog);

    [LibraryImport("GLES", EntryPoint = "glDeleteShader")]
    public static partial void glDeleteShader(uint shader);

    [LibraryImport("GLES", EntryPoint = "glCreateProgram")]
    public static partial uint glCreateProgram();

    [LibraryImport("GLES", EntryPoint = "glAttachShader")]
    public static partial void glAttachShader(uint program, uint shader);

    [LibraryImport("GLES", EntryPoint = "glLinkProgram")]
    public static partial void glLinkProgram(uint program);

    [LibraryImport("GLES", EntryPoint = "glGetProgramiv")]
    public static partial void glGetProgramiv(uint program, uint pname, int* param);

    [LibraryImport("GLES", EntryPoint = "glGetProgramInfoLog")]
    public static partial void glGetProgramInfoLog(uint program, int bufSize, int* length, byte* infoLog);

    [LibraryImport("GLES", EntryPoint = "glDeleteProgram")]
    public static partial void glDeleteProgram(uint program);

    [LibraryImport("GLES", EntryPoint = "glUseProgram")]
    public static partial void glUseProgram(uint program);

    [LibraryImport("GLES", EntryPoint = "glGetAttribLocation")]
    public static partial int glGetAttribLocation(uint program, byte* name);

    [LibraryImport("GLES", EntryPoint = "glGetUniformLocation")]
    public static partial int glGetUniformLocation(uint program, byte* name);

    [LibraryImport("GLES", EntryPoint = "glEnableVertexAttribArray")]
    public static partial void glEnableVertexAttribArray(uint index);

    [LibraryImport("GLES", EntryPoint = "glVertexAttribPointer")]
    public static partial void glVertexAttribPointer(uint index, int size, uint type, byte normalized, int stride, void* ptr);

    [LibraryImport("GLES", EntryPoint = "glUniform1i")]
    public static partial void glUniform1i(int location, int v);

    [LibraryImport("GLES", EntryPoint = "glUniformMatrix4fv")]
    public static partial void glUniformMatrix4fv(int location, int count, byte transpose, float* value);

    [LibraryImport("GLES", EntryPoint = "glGenVertexArrays")]
    public static partial void glGenVertexArrays(int n, uint* arrays);

    [LibraryImport("GLES", EntryPoint = "glBindVertexArray")]
    public static partial void glBindVertexArray(uint array);

    [LibraryImport("GLES", EntryPoint = "glDeleteVertexArrays")]
    public static partial void glDeleteVertexArrays(int n, uint* arrays);
}

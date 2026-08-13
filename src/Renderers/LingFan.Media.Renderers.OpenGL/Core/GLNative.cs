using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// 零反射 OpenGL 原生绑定层（替代 Silk.NET.OpenGL 的运行期包装）。
/// </summary>
/// <remarks>
/// <para><b>设计目标</b>：彻底消除 Silk.NET.OpenGL 绑定层的反射——不使用 Silk.NET 运行期 marshaller、
/// 不依赖 SharpGen 运行时 vtable 包装。NativeAOT 下零 IL2xxx。</para>
/// <para><b>跨平台库名</b>：经 <see cref="NativeLibrary.SetDllImportResolver"/> 把中性名 <c>"GL"</c> 重定向——
/// Windows 解析为 <c>opengl32.dll</c>（系统 OpenGL 1.1 thunk，运行期派发到 GPU 厂商 ICD）；
/// Linux（EGL 桌面 GL）解析为 <c>libGL.so.1</c>（Mesa 派发）。
/// WGL 引导符号（<c>wglGetProcAddress</c> 等）直接走 <c>[LibraryImport("opengl32")]</c>（Windows 默认解析）；
/// EGL 引导符号走中性名 <c>"EGL"</c> → Linux <c>libEGL.so.1</c> / Android 裸 <c>libEGL.so</c>（供 Android GLES 上下文路径）。
/// 本绑定仅覆盖 Windows/Linux（桌面 GL）与 Android（GLES EGL），不含任何 Apple 平台——Apple 不使用 OpenGL，由 Metal 后端覆盖。</para>
/// <para><b>调用约定</b>：GL 使用平台默认 ABI——Windows 上 <c>WINAPI</c>（__stdcall），
/// Linux 上 C 默认（cdecl）。故函数指针统一 <c>delegate* unmanaged&lt;...&gt;</c>（即 Winapi 默认），
/// 不指定 <c>[Stdcall]</c>：x86 下 Windows stdcall 与 Linux cdecl 栈清理语义不同，
/// 默认 Winapi 在两侧各自映射为正确约定（与 Vulkan 的显式 <c>[Stdcall]</c> 不同，GL 跨平台须用默认）。</para>
/// <para><b>基线 vs 扩展</b>：GL 1.1 核心函数（<c>glClear</c>/<c>glTexImage2D</c> 等）由平台库直接导出，
/// 经 <c>[LibraryImport("GL")]</c> 在首次调用时由 OS 加载器解析（加载期确定）。
/// GL 1.2+ 核心/扩展函数（着色器、VBO、VAO 等）在 Windows 上 <c>opengl32.dll</c> 导出表不含，
/// 必须经 <see cref="GetProcAddress"/>（Windows: <c>wglGetProcAddress</c>；Linux: <c>eglGetProcAddress</c>）
/// 在 GL 上下文 current 后运行时解析——见 <see cref="LoadModern"/>。</para>
/// <para>此类型仅承载原生绑定；GL 上下文生命周期（<see cref="GLNative.Wgl"/> / <see cref="GLNative.Egl"/> 引导符号的使用）由渲染器负责。</para>
/// </remarks>
internal static unsafe partial class GLNative
{
    // ── 中性库名重定向：GL(跨平台) / EGL(Linux) ──
    static GLNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(GLNative).Assembly, ResolveGlLoader);
    }

    private static nint ResolveGlLoader(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // 中性名 "GL"（桌面 GL）：Windows WGL(opengl32) / Linux Mesa(libGL.so.1)。
        // 不含 Apple 平台——Apple 不使用 OpenGL，由 Metal 后端覆盖，故 macOS/iOS 不走此分支（返回 Zero）。
        if (string.Equals(libraryName, "GL", StringComparison.Ordinal))
        {
            if (OperatingSystem.IsWindows())
                return NativeLibrary.TryLoad("opengl32.dll", assembly, searchPath, out nint h) ? h : nint.Zero;
            if (OperatingSystem.IsLinux())
                return NativeLibrary.TryLoad("libGL.so.1", assembly, searchPath, out nint h) ? h : nint.Zero;
            return nint.Zero;
        }

        // 中性名 "EGL"：Linux 桌面 EGL(libEGL.so.1) / Android 裸 libEGL.so（供 Android GLES 上下文路径）。
        // 仅在这些平台被构造（调用方以对应 OS 守卫，对应上下文类存在）。不含 Apple 平台（Metal 覆盖）。
        // Windows 上交回默认解析（EGL 绑定永不被调用）。
        if (string.Equals(libraryName, "EGL", StringComparison.Ordinal))
        {
            if (OperatingSystem.IsLinux())
                return NativeLibrary.TryLoad("libEGL.so.1", assembly, searchPath, out nint h) ? h : nint.Zero;
            if (OperatingSystem.IsAndroid())
                return NativeLibrary.TryLoad("libEGL.so", assembly, searchPath, out nint h) ? h : nint.Zero;
            return nint.Zero;
        }

        // opengl32 / user32 / gdi32 等 Windows 专属库名交回默认解析（仅 Windows 调用）。
        return nint.Zero;
    }

    // ── GL 1.1 基线：平台库直接导出，加载期解析（OS 加载器在首次调用时绑定）──

    [LibraryImport("GL")]
    public static partial void glClear(uint mask);

    [LibraryImport("GL")]
    public static partial void glClearColor(float red, float green, float blue, float alpha);

    [LibraryImport("GL")]
    public static partial void glViewport(int x, int y, int width, int height);

    [LibraryImport("GL")]
    public static partial void glEnable(uint cap);

    [LibraryImport("GL")]
    public static partial void glDisable(uint cap);

    [LibraryImport("GL")]
    public static partial uint glGetError();

    [LibraryImport("GL")]
    public static partial void glFlush();

    [LibraryImport("GL")]
    public static partial void glFinish();

    [LibraryImport("GL")]
    public static partial nint glGetString(uint name);

    [LibraryImport("GL")]
    public static partial void glPixelStorei(uint pname, int param);

    [LibraryImport("GL")]
    public static partial void glGenTextures(int n, uint* textures);

    [LibraryImport("GL")]
    public static partial void glBindTexture(uint target, uint texture);

    [LibraryImport("GL")]
    public static partial void glTexImage2D(uint target, int level, int internalformat, int width, int height, int border, uint format, uint type, void* pixels);

    [LibraryImport("GL")]
    public static partial void glTexSubImage2D(uint target, int level, int xoffset, int yoffset, int width, int height, uint format, uint type, void* pixels);

    [LibraryImport("GL")]
    public static partial void glTexParameteri(uint target, uint pname, int param);

    [LibraryImport("GL")]
    public static partial void glDeleteTextures(int n, uint* textures);

    [LibraryImport("GL")]
    public static partial void glDrawArrays(uint mode, int first, int count);

    // 即时模式（legacy）：仅作最小测试路径备用，真实渲染走着色器管线。
    [LibraryImport("GL")]
    public static partial void glBegin(uint mode);

    [LibraryImport("GL")]
    public static partial void glEnd();

    [LibraryImport("GL")]
    public static partial void glVertex2f(float x, float y);

    [LibraryImport("GL")]
    public static partial void glTexCoord2f(float s, float t);

    // ── GL 1.2+ 现代函数：运行时经 GetProcAddress 解析（需当前上下文）──

    private static bool _modernLoaded;

    private static unsafe delegate* unmanaged<uint, uint> _glActiveTexture;
    private static unsafe delegate* unmanaged<int, uint*, void> _glGenBuffers;
    private static unsafe delegate* unmanaged<uint, uint, void> _glBindBuffer;
    private static unsafe delegate* unmanaged<uint, nuint, void*, uint, void> _glBufferData;
    private static unsafe delegate* unmanaged<uint, nint, nuint, void*, void> _glBufferSubData;
    private static unsafe delegate* unmanaged<int, uint*, void> _glDeleteBuffers;
    private static unsafe delegate* unmanaged<uint, uint> _glCreateShader;
    private static unsafe delegate* unmanaged<uint, int, byte**, int*, void> _glShaderSource;
    private static unsafe delegate* unmanaged<uint, void> _glCompileShader;
    private static unsafe delegate* unmanaged<uint, uint, int*, void> _glGetShaderiv;
    private static unsafe delegate* unmanaged<uint, int, int*, byte*, void> _glGetShaderInfoLog;
    private static unsafe delegate* unmanaged<uint, void> _glDeleteShader;
    private static unsafe delegate* unmanaged<uint> _glCreateProgram;
    private static unsafe delegate* unmanaged<uint, uint, void> _glAttachShader;
    private static unsafe delegate* unmanaged<uint, void> _glLinkProgram;
    private static unsafe delegate* unmanaged<uint, uint, int*, void> _glGetProgramiv;
    private static unsafe delegate* unmanaged<uint, int, int*, byte*, void> _glGetProgramInfoLog;
    private static unsafe delegate* unmanaged<uint, void> _glDeleteProgram;
    private static unsafe delegate* unmanaged<uint, void> _glUseProgram;
    private static unsafe delegate* unmanaged<uint, byte*, int> _glGetAttribLocation;
    private static unsafe delegate* unmanaged<uint, byte*, int> _glGetUniformLocation;
    private static unsafe delegate* unmanaged<uint, void> _glEnableVertexAttribArray;
    private static unsafe delegate* unmanaged<uint, int, uint, byte, int, void*, void> _glVertexAttribPointer;
    private static unsafe delegate* unmanaged<int, int, void> _glUniform1i;
    private static unsafe delegate* unmanaged<int, int, byte, float*, void> _glUniformMatrix4fv;
    private static unsafe delegate* unmanaged<int, uint*, void> _glGenVertexArrays;
    private static unsafe delegate* unmanaged<uint, void> _glBindVertexArray;
    private static unsafe delegate* unmanaged<int, uint*, void> _glDeleteVertexArrays;

    /// <summary>
    /// 经平台引导符号解析 GL 1.2+ 函数指针。
    /// Windows 走 <c>wglGetProcAddress</c>，Linux 走 <c>eglGetProcAddress</c>（见 <see cref="GLNative.Wgl"/> / <see cref="GLNative.Egl"/>）。
    /// <para><b>须在 GL 上下文已 current 后调用</b>：无当前上下文时两平台均静默返回 <see langword="null"/>。</para>
    /// </summary>
    public static unsafe nint GetProcAddress(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        byte[] withNull = new byte[bytes.Length + 1];
        global::System.Buffer.BlockCopy(bytes, 0, withNull, 0, bytes.Length);
        withNull[bytes.Length] = 0;

        fixed (byte* p = withNull)
        {
            if (OperatingSystem.IsWindows())
                return wglGetProcAddress(p);
            return eglGetProcAddress(p);
        }
    }

    /// <summary>
    /// 解析全部 GL 现代函数指针（须在当前 GL 上下文建立后调用一次）。
    /// </summary>
    /// <remarks>幂等：重复调用直接返回。</remarks>
    public static unsafe void LoadModern()
    {
        if (_modernLoaded) return;

        _glActiveTexture = (delegate* unmanaged<uint, uint>)GetProcAddress("glActiveTexture");
        _glGenBuffers = (delegate* unmanaged<int, uint*, void>)GetProcAddress("glGenBuffers");
        _glBindBuffer = (delegate* unmanaged<uint, uint, void>)GetProcAddress("glBindBuffer");
        _glBufferData = (delegate* unmanaged<uint, nuint, void*, uint, void>)GetProcAddress("glBufferData");
        _glBufferSubData = (delegate* unmanaged<uint, nint, nuint, void*, void>)GetProcAddress("glBufferSubData");
        _glDeleteBuffers = (delegate* unmanaged<int, uint*, void>)GetProcAddress("glDeleteBuffers");
        _glCreateShader = (delegate* unmanaged<uint, uint>)GetProcAddress("glCreateShader");
        _glShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)GetProcAddress("glShaderSource");
        _glCompileShader = (delegate* unmanaged<uint, void>)GetProcAddress("glCompileShader");
        _glGetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)GetProcAddress("glGetShaderiv");
        _glGetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)GetProcAddress("glGetShaderInfoLog");
        _glDeleteShader = (delegate* unmanaged<uint, void>)GetProcAddress("glDeleteShader");
        _glCreateProgram = (delegate* unmanaged<uint>)GetProcAddress("glCreateProgram");
        _glAttachShader = (delegate* unmanaged<uint, uint, void>)GetProcAddress("glAttachShader");
        _glLinkProgram = (delegate* unmanaged<uint, void>)GetProcAddress("glLinkProgram");
        _glGetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)GetProcAddress("glGetProgramiv");
        _glGetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)GetProcAddress("glGetProgramInfoLog");
        _glDeleteProgram = (delegate* unmanaged<uint, void>)GetProcAddress("glDeleteProgram");
        _glUseProgram = (delegate* unmanaged<uint, void>)GetProcAddress("glUseProgram");
        _glGetAttribLocation = (delegate* unmanaged<uint, byte*, int>)GetProcAddress("glGetAttribLocation");
        _glGetUniformLocation = (delegate* unmanaged<uint, byte*, int>)GetProcAddress("glGetUniformLocation");
        _glEnableVertexAttribArray = (delegate* unmanaged<uint, void>)GetProcAddress("glEnableVertexAttribArray");
        _glVertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)GetProcAddress("glVertexAttribPointer");
        _glUniform1i = (delegate* unmanaged<int, int, void>)GetProcAddress("glUniform1i");
        _glUniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)GetProcAddress("glUniformMatrix4fv");
        _glGenVertexArrays = (delegate* unmanaged<int, uint*, void>)GetProcAddress("glGenVertexArrays");
        _glBindVertexArray = (delegate* unmanaged<uint, void>)GetProcAddress("glBindVertexArray");
        _glDeleteVertexArrays = (delegate* unmanaged<int, uint*, void>)GetProcAddress("glDeleteVertexArrays");

        AssertModern();
        _modernLoaded = true;
    }

    private static unsafe void AssertModern()
    {
        static void Check(string name, nint ptr)
        {
            if (ptr == 0)
                throw new InvalidOperationException(
                    $"GLNative 现代函数解析失败：{name}（GL 上下文可能未建立，或当前 GL 版本过低不支持该函数）。");
        }

        Check("glActiveTexture", (nint)_glActiveTexture);
        Check("glGenBuffers", (nint)_glGenBuffers);
        Check("glBindBuffer", (nint)_glBindBuffer);
        Check("glBufferData", (nint)_glBufferData);
        Check("glCreateShader", (nint)_glCreateShader);
        Check("glShaderSource", (nint)_glShaderSource);
        Check("glCompileShader", (nint)_glCompileShader);
        Check("glGetShaderiv", (nint)_glGetShaderiv);
        Check("glGetShaderInfoLog", (nint)_glGetShaderInfoLog);
        Check("glDeleteShader", (nint)_glDeleteShader);
        Check("glCreateProgram", (nint)_glCreateProgram);
        Check("glAttachShader", (nint)_glAttachShader);
        Check("glLinkProgram", (nint)_glLinkProgram);
        Check("glGetProgramiv", (nint)_glGetProgramiv);
        Check("glGetProgramInfoLog", (nint)_glGetProgramInfoLog);
        Check("glDeleteProgram", (nint)_glDeleteProgram);
        Check("glUseProgram", (nint)_glUseProgram);
        Check("glGetAttribLocation", (nint)_glGetAttribLocation);
        Check("glGetUniformLocation", (nint)_glGetUniformLocation);
        Check("glEnableVertexAttribArray", (nint)_glEnableVertexAttribArray);
        Check("glVertexAttribPointer", (nint)_glVertexAttribPointer);
        Check("glUniform1i", (nint)_glUniform1i);
        Check("glUniformMatrix4fv", (nint)_glUniformMatrix4fv);
        Check("glGenVertexArrays", (nint)_glGenVertexArrays);
        Check("glBindVertexArray", (nint)_glBindVertexArray);
        Check("glDeleteVertexArrays", (nint)_glDeleteVertexArrays);
    }
}

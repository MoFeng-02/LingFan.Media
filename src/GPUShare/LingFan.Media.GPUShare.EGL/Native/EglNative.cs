using System.Text;

namespace LingFan.Media.GPUShare.EGL;

/// <summary>
/// EGL 原生绑定层（中性互操作底座，EGL 符号唯一真源）。
/// </summary>
/// <remarks>
/// <para><b>归属</b>：GPU 胶水层（GPUShare.EGL）——EGL 引导/上下文/查询符号在此统一声明，
/// 渲染器（Renderers.OpenGL 等）作为消费方引用；同一套常量与绑定禁止在其他工程复刻。</para>
/// <para><b>跨平台库名</b>：经 <see cref="NativeLibrary.SetDllImportResolver"/> 把中性名 <c>"EGL"</c> 重定向——
/// Linux 解析为 <c>libEGL.so.1</c>，Android 解析为裸 <c>libEGL.so</c>；
/// Windows 上 EGL 绑定永不被调用（调用方以 <see cref="OperatingSystem.IsLinux"/> 守卫），交回默认解析。</para>
/// <para>EGL 句柄类型（EGLDisplay / EGLConfig / EGLSurface / EGLContext）按 ABI 统一映射为 <c>nint</c>；
/// EGLint 为 32 位整数，用 <c>int</c>；属性表（EGLint*）以 <c>int*</c> 传递。</para>
/// <para><b>AOT 兼容</b>：全部 <c>[LibraryImport]</c> 源生成 + <c>delegate* unmanaged</c> 函数指针承载扩展入口，零反射。</para>
/// </remarks>
public static unsafe partial class EglNative
{
    static EglNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(EglNative).Assembly, ResolveEglLoader);
    }

    private static nint ResolveEglLoader(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // 中性名 "EGL"：Linux 桌面 EGL(libEGL.so.1) / Android 裸 libEGL.so（供 GLES 上下文路径）。
        // 不含 Apple 平台——Apple 不使用 OpenGL/EGL，由 Metal 后端覆盖。Windows 交回默认解析（绑定永不被调用）。
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

    /// <summary>
    /// 经 <c>eglGetProcAddress</c> 解析 EGL/GL 扩展函数地址（无当前上下文亦可调用；
    /// 个别实现要求 EGL 上下文 current 后才返回非空，调用方须容忍 null 并允许重试）。
    /// </summary>
    public static unsafe nint ResolveProc(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        byte[] withNull = new byte[bytes.Length + 1];
        global::System.Buffer.BlockCopy(bytes, 0, withNull, 0, bytes.Length);
        withNull[bytes.Length] = 0;
        fixed (byte* p = withNull)
            return eglGetProcAddress(p);
    }

    [LibraryImport("EGL", EntryPoint = "eglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint eglGetProcAddress(byte* name);

    [LibraryImport("EGL", EntryPoint = "eglBindAPI")]
    public static partial int eglBindAPI(uint api);

    [LibraryImport("EGL", EntryPoint = "eglGetDisplay")]
    public static partial nint eglGetDisplay(nint displayId);

    // EGL_EXTENSIONS = 0x3055：对 EGL_NO_DISPLAY 查询返回客户端扩展串（设备枚举/平台设备能力自报）。
    [LibraryImport("EGL", EntryPoint = "eglQueryString")]
    public static partial nint eglQueryString(nint display, int name);

    // 设备枚举 / 平台显示（EGL_EXT_device_enumeration / EGL_EXT_platform_device）：
    // Mesa/GLVND 的 libEGL.so.1 不直接导出这些扩展符号（EntryPointNotFoundException 实测），须经
    // eglGetProcAddress 运行期解析（EGL 规范对扩展函数的 canonical 途径；无当前上下文亦可调用）。
    // 解析失败按"枚举失败"返回 0，调用方回落默认显示。
    private static bool _eglDeviceFunctionsResolved;

    private static unsafe delegate* unmanaged<int, nint*, int*, int> _pfnEglQueryDevicesEXT;
    private static unsafe delegate* unmanaged<nint, int, nint> _pfnEglQueryDeviceStringEXT;
    private static unsafe delegate* unmanaged<uint, nint, int*, nint> _pfnEglGetPlatformDisplayEXT;

    private static unsafe void EnsureEglDeviceFunctions()
    {
        if (_eglDeviceFunctionsResolved) return;
        _pfnEglQueryDevicesEXT = (delegate* unmanaged<int, nint*, int*, int>)ResolveProc("eglQueryDevicesEXT");
        _pfnEglQueryDeviceStringEXT = (delegate* unmanaged<nint, int, nint>)ResolveProc("eglQueryDeviceStringEXT");
        _pfnEglGetPlatformDisplayEXT = (delegate* unmanaged<uint, nint, int*, nint>)ResolveProc("eglGetPlatformDisplayEXT");
        _eglDeviceFunctionsResolved = true;
    }

    public static unsafe int eglQueryDevicesEXT(int maxDevices, nint* devices, int* numDevices)
    {
        EnsureEglDeviceFunctions();
        if (_pfnEglQueryDevicesEXT == null) return 0;
        return _pfnEglQueryDevicesEXT(maxDevices, devices, numDevices);
    }

    public static unsafe nint eglQueryDeviceStringEXT(nint device, int name)
    {
        EnsureEglDeviceFunctions();
        if (_pfnEglQueryDeviceStringEXT == null) return nint.Zero;
        return _pfnEglQueryDeviceStringEXT(device, name);
    }

    // 第三参按 EGL 规范为 const EGLint*（32 位 EGLint），非 EGLAttrib*——属性表须以 int* 传递。
    public static unsafe nint eglGetPlatformDisplayEXT(uint platform, nint nativeDisplay, int* attribList)
    {
        EnsureEglDeviceFunctions();
        if (_pfnEglGetPlatformDisplayEXT == null) return nint.Zero;
        return _pfnEglGetPlatformDisplayEXT(platform, nativeDisplay, attribList);
    }

    public const uint EglPlatformDeviceExt = 0x313F;   // EGL_PLATFORM_DEVICE_EXT
    public const int EglDeviceExtensions = 0x3055;     // EGL_EXTENSIONS

    [LibraryImport("EGL", EntryPoint = "eglInitialize")]
    public static partial int eglInitialize(nint display, int* major, int* minor);

    [LibraryImport("EGL", EntryPoint = "eglGetConfigs")]
    public static partial int eglGetConfigs(nint display, nint* configs, int configSize, int* numConfig);

    [LibraryImport("EGL", EntryPoint = "eglChooseConfig")]
    public static partial int eglChooseConfig(nint display, int* attribList, nint* configs, int configSize, int* numConfig);

    [LibraryImport("EGL", EntryPoint = "eglGetConfigAttrib")]
    public static partial int eglGetConfigAttrib(nint display, nint config, int attribute, int* value);

    [LibraryImport("EGL", EntryPoint = "eglCreateWindowSurface")]
    public static partial nint eglCreateWindowSurface(nint display, nint config, nint window, int* attribList);

    [LibraryImport("EGL", EntryPoint = "eglCreatePbufferSurface")]
    public static partial nint eglCreatePbufferSurface(nint display, nint config, int* attribList);

    [LibraryImport("EGL", EntryPoint = "eglCreateContext")]
    public static partial nint eglCreateContext(nint display, nint config, nint shareContext, int* attribList);

    [LibraryImport("EGL", EntryPoint = "eglMakeCurrent")]
    public static partial int eglMakeCurrent(nint display, nint draw, nint read, nint context);

    [LibraryImport("EGL", EntryPoint = "eglSwapBuffers")]
    public static partial int eglSwapBuffers(nint display, nint surface);

    [LibraryImport("EGL", EntryPoint = "eglSwapInterval")]
    public static partial int eglSwapInterval(nint display, int interval);

    [LibraryImport("EGL", EntryPoint = "eglGetError")]
    public static partial int eglGetError();

    [LibraryImport("EGL", EntryPoint = "eglDestroyContext")]
    public static partial int eglDestroyContext(nint display, nint context);

    [LibraryImport("EGL", EntryPoint = "eglDestroySurface")]
    public static partial int eglDestroySurface(nint display, nint surface);

    [LibraryImport("EGL", EntryPoint = "eglTerminate")]
    public static partial int eglTerminate(nint display);
}

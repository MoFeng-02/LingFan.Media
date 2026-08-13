using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL 跨 API 零拷贝互操作系统（仅由 <see cref="OpenGLGpuFrameProducer"/> 在对应平台调用）。
/// </summary>
/// <remarks>
/// <para><b>Windows：WGL_NV_DX_interop2</b>——<c>wglDXOpenDeviceNV</c> / <c>wglDXRegisterObjectNV</c> /
/// <c>wglDXUnregisterObjectNV</c> / <c>wglDXCloseDeviceNV</c>，把 D3D11 共享纹理注册为 GL 纹理（零拷贝）。
/// 这些 WGL 扩展函数经 <see cref="GetProcAddress"/>（内部 <c>wglGetProcAddress</c>）运行时解析，调用方以
/// <see cref="OperatingSystem.IsWindows"/> 守卫；GL 上下文建立前 <c>wglGetProcAddress</c> 返回 <see langword="null"/>，调用方判空回落软件解码。</para>
/// <para><b>Linux：EGL_EXT_image_dma_buf_import</b>——<c>eglCreateImageKHR</c> / <c>eglDestroyImageKHR</c> +
/// <c>glEGLImageTargetTexture2DOES</c>，把 VAAPI dma_buf 导入为 GL 纹理（零拷贝）。函数经
/// <c>eglGetProcAddress</c> / <c>glGetProcAddress</c>（均经 <see cref="GetProcAddress"/> 路由）运行时解析。</para>
    /// <para><b>调用约定</b>：本库目标 x64/arm64（AOT），原生 ABI 在 Windows 上即 WINAPI/__stdcall，
    /// 函数指针统一用 <c>delegate* unmanaged</c>（x64 下 stdcall 与平台默认 ABI 等同，无需 [Winapi] 调用约定后缀）；
    /// EGL / GL 扩展同为平台默认 ABI。</para>
/// <para><b>AOT</b>：零反射——函数指针经 <see cref="GetProcAddress"/> 取 <see cref="nint"/> 后直接转
/// <c>delegate* unmanaged</c>，不依赖 <c>Marshal.GetDelegateForFunctionPointer</c> 的反射路径。</para>
/// <para><b>跨平台无 #if</b>：解析按 <see cref="OperatingSystem"/> 运行时分发，扩展字段始终为 null 于非对应平台（调用方据可用性探测回落）。</para>
/// </remarks>
internal static unsafe partial class GLNative
{
    // ── WGL_NV_DX_interop2 常量 ──
    internal const int WglAccessReadOnlyNV = 0x0000;     // WGL_ACCESS_READ_ONLY_NV
    internal const int WglAccessReadWriteNV = 0x0001;    // WGL_ACCESS_READ_WRITE_NV
    internal const int WglAccessWriteDiscardNV = 0x0002; // WGL_ACCESS_WRITE_DISCARD_NV

    // ── EGL_EXT_image_dma_buf_import 常量 ──
    internal const int EglImageTarget = 0x30D1;          // EGL_IMAGE_TARGET (OES 目标枚举)
    internal const int EglLinuxDmaBufExt = 0x3272;       // EGL_LINUX_DMA_BUF_EXT
    internal const int EglWidth = 0x3057;                // EGL_WIDTH
    internal const int EglHeight = 0x3056;              // EGL_HEIGHT
    internal const int EglDmaBufPlane0FdExt = 0x3273;    // EGL_DMA_BUF_PLANE0_FD_EXT
    internal const int EglDmaBufPlane0OffsetExt = 0x3274; // EGL_DMA_BUF_PLANE0_OFFSET_EXT
    internal const int EglDmaBufPlane0PitchExt = 0x3275; // EGL_DMA_BUF_PLANE0_PITCH_EXT
    internal const int EglDmaBufPlane0ModifierLoExt = 0x3276; // EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT
    internal const int EglDmaBufPlane0ModifierHiExt = 0x3277; // EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT
    internal const int EglDmaBufPlaneCountExt = 0x3279;  // EGL_DMA_BUF_PLANE_COUNT_EXT
    internal const int EglLinuxDrmFourccExt = 0x3271;    // EGL_LINUX_DRM_FOURCC_EXT
    internal const int EglNone = 0x3038;                 // EGL_NONE

    // ── WGL_NV_DX_interop2 函数指针（Windows 调用；x64/arm64 下原生 ABI 即 WINAPI，无需 [Winapi] 调用约定后缀）──
    private static unsafe delegate* unmanaged<void*, nint> _wglDXOpenDeviceNV;
    private static unsafe delegate* unmanaged<nint, void*, uint, uint, uint, nint> _wglDXRegisterObjectNV;
    private static unsafe delegate* unmanaged<nint, nint, int> _wglDXUnregisterObjectNV;
    private static unsafe delegate* unmanaged<nint, int> _wglDXCloseDeviceNV;
    // 栅栏：采样前 Acquire / 采样后 Release，防止 D3D11 生产者写入与 GL 读取竞态（WGL_NV_DX_interop2 强制要求）
    private static unsafe delegate* unmanaged<nint, int, void*, int> _wglDXLockObjectsNV;
    private static unsafe delegate* unmanaged<nint, int, void*, int> _wglDXUnlockObjectsNV;

    // ── EGL dma_buf / OES 函数指针（Linux 调用，平台默认 ABI）──
    private static unsafe delegate* unmanaged<nint, nint, uint, int*, nint> _eglCreateImageKHR;
    private static unsafe delegate* unmanaged<nint, nint, int> _eglDestroyImageKHR;
    private static unsafe delegate* unmanaged<uint, nint, void> _glEGLImageTargetTexture2DOES;

    private static bool _interopResolved;

    /// <summary>运行时解析互扩展函数指针（幂等；GL 上下文须已建立并 current 于对应平台）。</summary>
    private static void ResolveInterop()
    {
        if (_interopResolved) return;

        if (OperatingSystem.IsWindows())
        {
            _wglDXOpenDeviceNV = (delegate* unmanaged<void*, nint>)GetProcAddress("wglDXOpenDeviceNV");
            _wglDXRegisterObjectNV = (delegate* unmanaged<nint, void*, uint, uint, uint, nint>)GetProcAddress("wglDXRegisterObjectNV");
            _wglDXUnregisterObjectNV = (delegate* unmanaged<nint, nint, int>)GetProcAddress("wglDXUnregisterObjectNV");
            _wglDXCloseDeviceNV = (delegate* unmanaged<nint, int>)GetProcAddress("wglDXCloseDeviceNV");
            _wglDXLockObjectsNV = (delegate* unmanaged<nint, int, void*, int>)GetProcAddress("wglDXLockObjectsNV");
            _wglDXUnlockObjectsNV = (delegate* unmanaged<nint, int, void*, int>)GetProcAddress("wglDXUnlockObjectsNV");
        }
        else if (OperatingSystem.IsLinux())
        {
            _eglCreateImageKHR = (delegate* unmanaged<nint, nint, uint, int*, nint>)GetProcAddress("eglCreateImageKHR");
            _eglDestroyImageKHR = (delegate* unmanaged<nint, nint, int>)GetProcAddress("eglDestroyImageKHR");
            _glEGLImageTargetTexture2DOES = (delegate* unmanaged<uint, nint, void>)GetProcAddress("glEGLImageTargetTexture2DOES");
        }

        // 仅当确有指针解析成功才置"已解析"：wglGetProcAddress / eglGetProcAddress 在无当前 GL/EGL 上下文时静默返 null。
        // 若不缓存此负结果，下次（上下文已 current）可重试解析，避免零拷贝路径被一次性误判永久禁用。
        bool resolvedAny = OperatingSystem.IsWindows()
            ? _wglDXOpenDeviceNV != null
            : _eglCreateImageKHR != null;
        _interopResolved = resolvedAny;
    }

    /// <summary>WGL_NV_DX_interop2 是否可用（Windows；GL 上下文须已建立）。</summary>
    internal static bool IsWglDxInteropAvailable()
    {
        ResolveInterop();
        return _wglDXOpenDeviceNV != null;
    }

    /// <summary>EGL_EXT_image_dma_buf_import + glEGLImageTargetTexture2DOES 是否可用（Linux；EGL 上下文须已建立）。</summary>
    internal static bool IsEglDmaBufImportAvailable()
    {
        ResolveInterop();
        return _eglCreateImageKHR != null && _glEGLImageTargetTexture2DOES != null;
    }

    // ── WGL_NV_DX_interop2 包装（调用前须 MakeCurrent GL 上下文；GL 上下文须为离屏共享组所有者）──

    internal static unsafe nint WglDXOpenDeviceNV(void* dxDevice)
        => _wglDXOpenDeviceNV != null ? _wglDXOpenDeviceNV(dxDevice) : nint.Zero;

    internal static unsafe nint WglDXRegisterObjectNV(nint hDevice, void* dxResource, uint name, uint type, uint access)
        => _wglDXRegisterObjectNV != null ? _wglDXRegisterObjectNV(hDevice, dxResource, name, type, access) : nint.Zero;

    internal static unsafe int WglDXUnregisterObjectNV(nint hDevice, nint glObject)
        => _wglDXUnregisterObjectNV != null ? _wglDXUnregisterObjectNV(hDevice, glObject) : 0;

    internal static unsafe int WglDXCloseDeviceNV(nint hDevice)
        => _wglDXCloseDeviceNV != null ? _wglDXCloseDeviceNV(hDevice) : 0;

    // 栅栏：object 为 wglDXRegisterObjectNV 返回的对象句柄（非 GL 纹理 ID）。count=1，objects=对象句柄数组。
    internal static unsafe int WglDXLockObjectsNV(nint hDevice, int count, void* objects)
        => _wglDXLockObjectsNV != null ? _wglDXLockObjectsNV(hDevice, count, objects) : 0;

    internal static unsafe int WglDXUnlockObjectsNV(nint hDevice, int count, void* objects)
        => _wglDXUnlockObjectsNV != null ? _wglDXUnlockObjectsNV(hDevice, count, objects) : 0;

    // ── EGL dma_buf / OES 包装 ──

    internal static unsafe nint EglCreateImageKHR(nint dpy, nint ctx, uint target, int* attribList)
        => _eglCreateImageKHR != null ? _eglCreateImageKHR(dpy, ctx, target, attribList) : nint.Zero;

    internal static unsafe int EglDestroyImageKHR(nint dpy, nint image)
        => _eglDestroyImageKHR != null ? _eglDestroyImageKHR(dpy, image) : 0;

    internal static unsafe void GlEGLImageTargetTexture2DOES(uint target, nint image)
    {
        if (_glEGLImageTargetTexture2DOES != null)
            _glEGLImageTargetTexture2DOES(target, image);
    }
}

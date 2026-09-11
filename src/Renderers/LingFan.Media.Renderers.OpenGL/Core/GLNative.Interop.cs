using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL 跨 API 零拷贝互操作（WGL_NV_DX_interop2，Windows 路径）。
/// </summary>
/// <remarks>
/// <para><b>Windows：WGL_NV_DX_interop2</b>——<c>wglDXOpenDeviceNV</c> / <c>wglDXRegisterObjectNV</c> /
/// <c>wglDXUnregisterObjectNV</c> / <c>wglDXCloseDeviceNV</c>，把 D3D11 共享纹理注册为 GL 纹理（零拷贝）。
/// 这些 WGL 扩展函数经 <see cref="GLNative.GetProcAddress"/>（内部 <c>wglGetProcAddress</c>）运行时解析，调用方以
/// <see cref="OperatingSystem.IsWindows"/> 守卫；GL 上下文建立前 <c>wglGetProcAddress</c> 返回 <see langword="null"/>，调用方判空回落软件解码。</para>
/// <para><b>Linux：EGL_EXT_image_dma_buf_import</b>——已收敛至 <c>LingFan.Media.GPUShare.EGL.EglDmaBufImport</c>
/// （GPUShare.EGL，EGL 常量与扩展符号唯一真源），本类型不再承载 EGL 互操作。</para>
/// <para><b>调用约定</b>：本库目标 x64/arm64（AOT），原生 ABI 在 Windows 上即 WINAPI/__stdcall，
/// 函数指针统一用 <c>delegate* unmanaged</c>（x64 下 stdcall 与平台默认 ABI 等同，无需 [Winapi] 调用约定后缀）。</para>
/// <para><b>AOT</b>：零反射——函数指针经 <see cref="GLNative.GetProcAddress"/> 取 <see cref="nint"/> 后直接转
/// <c>delegate* unmanaged</c>，不依赖 <c>Marshal.GetDelegateForFunctionPointer</c> 的反射路径。</para>
/// </remarks>
internal static unsafe partial class GLNative
{
    // WGL_NV_DX_interop2 常量
    internal const int WglAccessReadOnlyNV = 0x0000;     // WGL_ACCESS_READ_ONLY_NV
    internal const int WglAccessReadWriteNV = 0x0001;    // WGL_ACCESS_READ_WRITE_NV
    internal const int WglAccessWriteDiscardNV = 0x0002; // WGL_ACCESS_WRITE_DISCARD_NV

    // WGL_NV_DX_interop2 函数指针（Windows 调用；x64/arm64 下原生 ABI 即 WINAPI，无需 [Winapi] 调用约定后缀）
    private static unsafe delegate* unmanaged<void*, nint> _wglDXOpenDeviceNV;
    private static unsafe delegate* unmanaged<nint, void*, uint, uint, uint, nint> _wglDXRegisterObjectNV;
    private static unsafe delegate* unmanaged<nint, nint, int> _wglDXUnregisterObjectNV;
    private static unsafe delegate* unmanaged<nint, int> _wglDXCloseDeviceNV;
    // 栅栏：采样前 Acquire / 采样后 Release，防止 D3D11 生产者写入与 GL 读取竞态（WGL_NV_DX_interop2 强制要求）
    private static unsafe delegate* unmanaged<nint, int, void*, int> _wglDXLockObjectsNV;
    private static unsafe delegate* unmanaged<nint, int, void*, int> _wglDXUnlockObjectsNV;

    private static bool _interopResolved;

    /// <summary>运行时解析互扩展函数指针（幂等；GL 上下文须已建立并 current 于 Windows）。</summary>
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

        // 仅当确有指针解析成功才置"已解析"：wglGetProcAddress 在无当前 GL 上下文时静默返 null。
        // 若不缓存此负结果，下次（上下文已 current）可重试解析，避免零拷贝路径被一次性误判永久禁用。
        _interopResolved = _wglDXOpenDeviceNV != null;
    }

    /// <summary>WGL_NV_DX_interop2 是否可用（Windows；GL 上下文须已建立）。</summary>
    internal static bool IsWglDxInteropAvailable()
    {
        ResolveInterop();
        return _wglDXOpenDeviceNV != null;
    }

    // WGL_NV_DX_interop2 包装（调用前须 MakeCurrent GL 上下文；GL 上下文须为离屏共享组所有者）

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
}

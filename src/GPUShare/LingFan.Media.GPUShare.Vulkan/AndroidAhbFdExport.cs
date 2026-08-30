using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// Android AHB → dma_buf 文件描述符导出辅助。
/// </summary>
/// <remarks>
/// <para><b>动机</b>：Adreno 等移动 GPU 拒绝把「普通 Vulkan 外部图像」经
/// <c>VK_KHR_external_memory_fd</c>（OPAQUE_FD）导出（<c>vkBindImageMemory</c> 报
/// <c>ErrorInvalidExternalHandle</c>），但 AHB 外部内存（<c>VK_ANDROID_external_memory_android_hardware_buffer</c>）
/// 是原生支持的。因此共享离屏面改为 AHB 承载，再用本类把 AHB 底层的 dma_buf fd 抽出来，
/// 作为 OPAQUE_FD 交给 Avalonia 合成器（其 Android <c>ImportImage</c> 仅接受 OPAQUE_FD）。</para>
/// <para><b>取 fd 路径</b>：<c>AHardwareBuffer_getNativeHandle</c> 不在公共 NDK <c>libandroid.so</c>，
/// 而是 VNDK 符号，位于 <c>libnativewindow.so</c>（<c>libandroid.so</c> 的依赖，运行时必已加载）。
/// 故经 <c>dlopen</c> + <c>dlsym</c> 解析，再读 <c>native_handle_t.data[0]</c>（gralloc buffer 的 dma_buf fd）
/// 并 <c>dup</c> 出独立 fd——这是 gunbark / rutabaga / Chromium 同款做法，稳定且 AOT 安全。</para>
/// <para><b>零侵入</b>：纯 Android 图形底层原语，不引用任何 UI 框架或契约层；仅被
/// <see cref="VulkanSharedSurfaceSource"/> 的 Android 分支调用，Windows / Linux / macOS / iOS 路径完全不受影响。</para>
/// </remarks>
public static unsafe partial class AndroidAhbFdExport
{
    // bionic dlopen 标志：RTLD_NOW = 2（RTLD_LAZY = 1）。
    private const int RtldNow = 2;

    // AHardwareBuffer_getNativeHandle 原型：const native_handle_t* (*)(const AHardwareBuffer* buffer)。
    // 调用约定在 ARM64 上与标准 C ABI 一致（与 VulkanNative 的 unmanaged[Stdcall] 同处理）。
    private static delegate* unmanaged[Stdcall]<nint, nint> _getNativeHandle;
    private static bool _resolved;

    [LibraryImport("libdl.so", EntryPoint = "dlopen")]
    private static partial nint DlOpen([MarshalAs(UnmanagedType.LPStr)] string filename, int flags);

    [LibraryImport("libdl.so", EntryPoint = "dlsym")]
    private static partial nint DlSym(nint handle, [MarshalAs(UnmanagedType.LPStr)] string symbol);

    [LibraryImport("libc.so", EntryPoint = "dup")]
    private static partial int Dup(int fd);

    [LibraryImport("libandroid.so", EntryPoint = "AHardwareBuffer_release")]
    private static partial void AhbRelease(nint buffer);

    private static bool TryResolve()
    {
        if (_resolved)
            return _getNativeHandle != null;
        _resolved = true;

        // libnativewindow.so 是 libandroid.so 的依赖，正常已在进程内加载，dlopen 取其句柄。
        nint lib = DlOpen("libnativewindow.so", RtldNow);
        if (lib == nint.Zero)
            lib = DlOpen("libandroid.so", RtldNow); // 兜底：符号实际不在 libandroid，但保证句柄非空以便 dlsym 不崩
        if (lib == nint.Zero)
            return false;

        nint sym = DlSym(lib, "AHardwareBuffer_getNativeHandle");
        if (sym == nint.Zero)
            return false;

        _getNativeHandle = (delegate* unmanaged[Stdcall]<nint, nint>)sym;
        return true;
    }

    /// <summary>释放一个 AHardwareBuffer 引用（+1 由调用方持有）。空指针安全。</summary>
    public static void Release(nint buffer)
    {
        if (buffer != nint.Zero)
            AhbRelease(buffer);
    }

    /// <summary>
    /// 从 Vulkan 导出的 AHardwareBuffer 抽取底层 dma_buf 文件描述符。
    /// </summary>
    /// <param name="ahb">Vulkan 经 <c>vkGetMemoryAndroidHardwareBufferANDROID</c> 导出的 AHardwareBuffer 引用。</param>
    /// <param name="fd">成功时返回 <c>dup</c> 后的 dma_buf fd（调用方拥有，须自行 close）；失败时为 -1。</param>
    /// <returns>是否成功取到 fd。</returns>
    /// <remarks>
    /// 失败时返回 false（调用方应回落既有 Skia 保底路径，不回归）。
    /// native_handle_t 布局：int version; int numFds; int numInts; int data[0]; →
    /// dma_buf fd 在 <c>data[0]</c>（偏移 12 字节）。取 fd 后立即 dup，使本 fd 独立于 AHB 生命周期。
    /// </remarks>
    public static bool TryGetDmaBufFd(nint ahb, out int fd)
    {
        fd = -1;
        if (ahb == nint.Zero || !TryResolve() || _getNativeHandle == null)
            return false;

        nint nh = _getNativeHandle(ahb);
        if (nh == nint.Zero)
            return false;

        // native_handle_t：version(0) / numFds(4) / numInts(8) / data[](12)
        int numFds = Marshal.ReadInt32(nh, 4);
        if (numFds < 1)
            return false;
        int rawFd = Marshal.ReadInt32(nh, 12);
        if (rawFd < 0)
            return false;

        fd = Dup(rawFd);
        return fd >= 0;
    }
}

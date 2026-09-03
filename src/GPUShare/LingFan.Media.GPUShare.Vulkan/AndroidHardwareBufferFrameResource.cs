using System;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;

namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// Android 硬件缓冲（AHardwareBuffer）帧资源——由 MediaCodec 后端经 ImageReader(Surface) 产出，
/// 持有 <c>AHardwareBuffer*</c> 裸指针（跨工程传递用 IntPtr，避免 GPUShare.Vulkan 引入 net-android 特定类型）。
/// 供 Vulkan 渲染器经 <see cref="VulkanSharedSurfaceSource"/> 在 GPU 内导入
/// （VK_ANDROID_external_memory_android_hardware_buffer）并由 <see cref="VulkanYcbcrToRgbaConverter"/>
/// 完成 YUV→RGB，实现端到端零 CPU 拷贝上屏。
/// </summary>
/// <remarks>
/// <para><b>归属</b>：平台特定原生帧（AHardwareBuffer）按 <see cref="IFrameResource"/> 契约约定（L11）放对应平台模块，
/// 不进 Abstractions 契约层（AHB 为 Android 平台特定物）。本类与 <see cref="VulkanVideoFrameResource"/> 同处中性
/// GPU 互操作层 GPUShare.Vulkan，由 MediaCodec 后端（产出方）与 Renderers.Vulkan（消费方）引用；
/// GPUShare 不反向引用任何 Backend/Renderer，依赖倒置（DIP）合规。</para>
/// <para><b>生命周期</b>：解码器把 ImageReader 产出的 AHardwareBuffer 引用所有权移交本类（不提前 image.close / buffer.close）；
/// <see cref="Dispose"/> 经 NDK <c>AHardwareBuffer_release</c> 释放该引用。Vulkan 导入期间驱动持有独立引用
/// （至 vkFreeMemory 释放），与本类引用计数互不干扰，无悬挂。</para>
/// <para><b>异步策略</b>：<see cref="Dispose"/> 同步（native 分类），无 I/O await，补 async 即伪异步。</para>
/// <para><b>AOT 兼容</b>：sealed 类，[LibraryImport] 零反射 P/Invoke，无反射、无 [DllImport]。</para>
/// </remarks>
public sealed unsafe partial class AndroidHardwareBufferFrameResource : IFrameResource
{
    private readonly IntPtr _ahbHandle;
    private bool _disposed;

    // AHB 引用对账（泄漏定位）：构造 +1 / Dispose -1。Live 持续增长 = Dispose 链断
    // （真机实证：Graphics 内存每遍播放 +51MB、AHB 地址零复用 = release 未达 gralloc）。
    internal static long LiveCount => System.Threading.Interlocked.Read(ref _liveCount);
    private static long _liveCount;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>AHardwareBuffer* 裸指针（跨工程 IntPtr 传递，不引入 net-android 特定类型）。</summary>
    public IntPtr AhbHandle => _ahbHandle;

    /// <summary>
    /// 初始化 <see cref="AndroidHardwareBufferFrameResource"/> 的新实例。
    /// </summary>
    /// <param name="ahbHandle">AHardwareBuffer* 指针（引用所有权由调用方移交本类，<see cref="Dispose"/> 时释放）。</param>
    /// <param name="width">帧宽度（像素）。</param>
    /// <param name="height">帧高度（像素）。</param>
    /// <param name="format">像素格式（YUV420 系列语义，如 NV12 / YUV420P）。</param>
    public AndroidHardwareBufferFrameResource(IntPtr ahbHandle, int width, int height, PixelFormat format)
    {
        _ahbHandle = ahbHandle;
        Width = width;
        Height = height;
        Format = format;
        System.Threading.Interlocked.Increment(ref _liveCount);
        // 泄漏对账日志（每 64 帧一条）：Live 应稳定在 in-flight 峰值（≤~24）；持续上涨即泄漏。
        if (LiveCount % 64 == 1)
            Console.WriteLine($"[AHB-LEAK] live={LiveCount}");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        System.Threading.Interlocked.Decrement(ref _liveCount);
        if ((LiveCount % 64) == 0)
            Console.WriteLine($"[AHB-LEAK] disposed live={LiveCount}");
        if (_ahbHandle != IntPtr.Zero)
            AHardwareBufferRelease(_ahbHandle);
    }

    // NDK AHardwareBuffer_release：释放一个 AHardwareBuffer 引用（+1 由本类持有）。
    // 源生成 P/Invoke（AOT 友好），EntryPoint 显式带下划线（与 VulkanSharedSurfaceSource 同款声明一致）。
    [LibraryImport("libandroid.so", EntryPoint = "AHardwareBuffer_release")]
    private static partial void AHardwareBufferRelease(IntPtr buffer);
}

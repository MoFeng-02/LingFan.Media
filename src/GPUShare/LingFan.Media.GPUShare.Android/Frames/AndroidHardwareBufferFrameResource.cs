using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Android;

/// <summary>
/// Android HardwareBuffer 帧资源（中立承载）：持有解码侧产出的 <c>AHardwareBuffer</c> 句柄，
/// 供任意 GPU API 胶水（Vulkan 外部内存 / EGLImage）导入采样。只承载句柄与尺寸元数据，
/// 不含任何 GPU API 绑定。
/// </summary>
/// <remarks>
/// <para><b>生命周期</b>：构造即持有一次 AHB 引用（+1），<see cref="Dispose"/> 时
/// <c>AHardwareBuffer_release</c> 释放；导入方（EGLImage / VkImage）持有各自的独立引用，
/// 与本资源互不干扰。</para>
/// <para><b>AHB 引用对账</b>：构造 +1 / Dispose -1。Live 持续增长 = Dispose 链断
/// （真机实证：Graphics 内存每遍播放 +51MB、AHB 地址零复用 = release 未达 gralloc）。</para>
/// <para><b>AOT 兼容</b>：裸 P/Invoke（<c>[LibraryImport]</c>），零反射。</para>
/// </remarks>
public sealed unsafe partial class AndroidHardwareBufferFrameResource : IFrameResource
{
    private readonly IntPtr _ahbHandle;
    private bool _disposed;

    // AHB 引用对账（泄漏定位）：构造 +1 / Dispose -1。Live 持续增长 = Dispose 链断。
    internal static long LiveCount => System.Threading.Interlocked.Read(ref _liveCount);
    private static long _liveCount;

    /// <summary>帧宽（像素）。</summary>
    public int Width { get; }

    /// <summary>帧高（像素）。</summary>
    public int Height { get; }

    /// <summary>像素格式（CPU 侧语义）。</summary>
    public PixelFormat Format { get; }

    /// <summary>原生 <c>AHardwareBuffer</c> 句柄。</summary>
    public IntPtr AhbHandle => _ahbHandle;

    /// <summary>初始化帧资源：接管一次 AHB 引用（调用方出让所有权）。</summary>
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
    // 源生成 P/Invoke（AOT 友好），EntryPoint 显式带下划线（与 GL 桥/共享表面源同款声明一致）。
    [LibraryImport("libandroid.so", EntryPoint = "AHardwareBuffer_release")]
    private static partial void AHardwareBufferRelease(IntPtr buffer);
}

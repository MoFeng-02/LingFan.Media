using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Platforms.Android;

/// <summary>
/// Android SurfaceView 无空域合成互操作（Phase 2）。
/// </summary>
/// <remarks>
/// <para>职责：持有由 Android 宿主（net10.0-android TFM 的 App/Demo 层，经 JNI 把
/// <c>SurfaceHolder.Surface → ANativeWindow</c> 得到的指针）传入的 <c>ANativeWindow*</c>，
/// 封装 NDK 原生窗口操作（acquire / release / setBuffersGeometry），适用于全屏视频渲染，
/// 由 SurfaceFlinger 合成到 View 树（无独立窗口，无空域）。</para>
/// <para><b>范围说明</b>：本库为 net10.0，不能引用 Android Java 类型（Surface/SurfaceHolder），
/// 故「Java Surface → ANativeWindow」的提取由宿主层完成并传入指针；本类只做 NDK 侧互操作。</para>
/// <para><b>平台边界</b>：仅 Android 有效；非 Android 调用抛 <see cref="PlatformNotSupportedException"/>。编译期跨平台可编译。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——NDK 调用是同步原生边界，无 I/O await；
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步，故保持同步。</para>
/// <para><b>AOT 兼容</b>：sealed + <see cref="LibraryImportAttribute"/> 源生成 P/Invoke，零反射。</para>
/// </remarks>
[SupportedOSPlatform("Android")]
public sealed unsafe partial class SurfaceViewInterop : IDisposable
{
    private IntPtr _window;
    private bool _disposed;

    /// <summary>已绑定的 <c>ANativeWindow*</c>（未绑定为 <see cref="IntPtr.Zero"/>）。</summary>
    public IntPtr Handle => _window;

    /// <summary>是否已绑定原生窗口。</summary>
    public bool IsAttached => _window != IntPtr.Zero;

    /// <summary>
    /// 绑定宿主传入的 <c>ANativeWindow*</c>（acquire 增加引用计数）。重复绑定会先解绑旧窗口。
    /// </summary>
    /// <param name="window">宿主经 JNI 提取的 <c>ANativeWindow*</c>，必须非空。</param>
    public void Attach(IntPtr window)
    {
        ThrowIfNotAndroid();
        if (window == IntPtr.Zero)
            throw new ArgumentNullException(nameof(window), "ANativeWindow 指针不能为空。");

        if (_window != IntPtr.Zero)
            Detach();

        _window = window;
        ANativeWindowAcquire(_window);
    }

    /// <summary>解绑并释放 <c>ANativeWindow</c> 引用（release 减少引用计数）。</summary>
    public void Detach()
    {
        if (_window == IntPtr.Zero)
            return;
        ANativeWindowRelease(_window);
        _window = IntPtr.Zero;
    }

    /// <summary>
    /// 设置缓冲几何（宽 / 高 / 像素格式），供 SwapChain 匹配。
    /// </summary>
    /// <param name="width">缓冲宽度（像素）。</param>
    /// <param name="height">缓冲高度（像素）。</param>
    /// <param name="pixelFormat">ANativeWindow 像素格式（如 <c>WINDOW_FORMAT_RGBA_8888 = 1</c>）。</param>
    public void SetBuffersGeometry(int width, int height, int pixelFormat)
    {
        ThrowIfNotAndroid();
        if (_window == IntPtr.Zero)
            throw new InvalidOperationException("尚未绑定 ANativeWindow，无法设置缓冲几何。");
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "缓冲尺寸必须为正数。");

        int ret = ANativeWindowSetBuffersGeometry(_window, width, height, pixelFormat);
        if (ret != 0)
            throw new InvalidOperationException($"ANativeWindow_setBuffersGeometry 失败，code={ret}。");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detach();
    }

    [LibraryImport("libandroid.so", EntryPoint = "ANativeWindow_acquire")]
    private static partial void ANativeWindowAcquire(IntPtr window);

    [LibraryImport("libandroid.so", EntryPoint = "ANativeWindow_release")]
    private static partial void ANativeWindowRelease(IntPtr window);

    [LibraryImport("libandroid.so", EntryPoint = "ANativeWindow_setBuffersGeometry")]
    private static partial int ANativeWindowSetBuffersGeometry(IntPtr window, int width, int height, int format);

    private static void ThrowIfNotAndroid()
    {
        if (!OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException("SurfaceView 互操作仅支持 Android。");
    }
}

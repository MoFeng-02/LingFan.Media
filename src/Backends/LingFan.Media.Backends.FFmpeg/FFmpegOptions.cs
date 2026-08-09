namespace LingFan.Media.Backends.FFmpeg;

/// <summary>
/// FFmpeg 后端配置选项。
/// </summary>
public sealed class FFmpegOptions
{
    /// <summary>是否启用硬件解码（默认 true）。</summary>
    public bool HardwareAcceleration { get; set; } = true;

    /// <summary>
    /// FFmpeg 原生库路径（自定义路径时设置，null 表示使用系统默认搜索路径）。
    /// </summary>
    public string? FFmpegLibraryPath { get; set; }

    /// <summary>
    /// FFmpeg 内部日志级别（默认 AV_LOG_ERROR = 16）。
    /// </summary>
    public int LogLevel { get; set; } = 16;

    /// <summary>是否启用多线程解码（默认 true）。</summary>
    public bool EnableMultiThread { get; set; } = true;

    /// <summary>解码线程数（0 = 自动选择，默认 0）。</summary>
    public int ThreadCount { get; set; } = 0;

    // ── Android MediaCodec 硬解注入点（宿主提供，库自身无法获取）──

    /// <summary>
    /// Android MediaCodec 表面直渲染的 <c>android/view/Surface</c> JNI 全局引用（jobject）。
    /// 默认 <see cref="IntPtr.Zero"/> = 缓冲模式（ByteBuffer 输出 NV12 软件帧，仍为硬解）。
    /// </summary>
    /// <remarks>
    /// <para>宿主（net10.0-android）在打开媒体前设置：从 DI 解析本 Options 单例并赋值
    /// （<c>JNIEnv.NewGlobalRef(surface.Handle)</c>）。库为 net10.0 无法引用 Java 类型（依赖倒置）。</para>
    /// <para>与 <see cref="MediaCodecNativeWindow"/> 二选一，Surface 优先。</para>
    /// </remarks>
    public IntPtr MediaCodecSurface { get; set; }

    /// <summary>
    /// Android MediaCodec 表面直渲染的 <c>ANativeWindow*</c>（NDK 指针）。
    /// 默认 <see cref="IntPtr.Zero"/>。宿主可经 <c>ANativeWindow_fromSurface</c> 取得
    /// （或用 Platforms 层 <c>TextureViewInterop</c>/<c>SurfaceViewInterop</c> 持有的指针）。
    /// </summary>
    public IntPtr MediaCodecNativeWindow { get; set; }
}

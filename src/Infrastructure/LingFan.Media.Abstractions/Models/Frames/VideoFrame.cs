namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频帧。实现 <see cref="IDisposableFrame"/>，级联释放 <see cref="Resource"/>。
/// </summary>
/// <remarks>
/// <para>帧所有权转移语义：Decoder → FrameQueue → Renderer。</para>
/// <para>Dispose 时必须级联释放 IFrameResource（GPU 纹理/CPU 内存），防止资源泄漏。</para>
/// <para>属性使用 internal set，<see cref="Reset"/> 方法供解码器复用帧实例（帧池化）。</para>
/// </remarks>
public sealed class VideoFrame : IDisposableFrame
{
    /// <summary>帧宽度（像素）。</summary>
    public int Width { get; internal set; }

    /// <summary>帧高度（像素）。</summary>
    public int Height { get; internal set; }

    /// <summary>像素格式。</summary>
    public PixelFormat Format { get; internal set; }

    /// <summary>帧资源（<see cref="SoftwareFrameResource"/>=CPU 内存，或 GPU 纹理=零拷贝句柄）。</summary>
    /// <remarks>
    /// 可为 null（池中未填充的空壳），<see cref="Reset"/> 填充实际资源。
    /// 消费方通过 pattern matching 访问，null 不会导致 NRE。
    /// </remarks>
    public IFrameResource? Resource { get; internal set; }

    /// <summary>显示时间戳（PTS）。</summary>
    public TimeSpan Timestamp { get; internal set; }

    /// <summary>帧持续时间。</summary>
    public TimeSpan Duration { get; internal set; }

    /// <summary>是否关键帧。</summary>
    public bool KeyFrame { get; internal set; }

    /// <summary>帧的色彩空间描述（可空；渲染端据此选 YUV→RGB 矩阵，null 时用默认。</summary>
    public VideoColorInfo? ColorInfo { get; set; }

    /// <inheritdoc/>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// 初始化 <see cref="VideoFrame"/> 的新实例。
    /// </summary>
    public VideoFrame(int width, int height, PixelFormat format, IFrameResource resource,
        TimeSpan timestamp, TimeSpan duration, bool keyFrame, VideoColorInfo? colorInfo = null)
    {
        Width = width;
        Height = height;
        Format = format;
        Resource = resource;
        Timestamp = timestamp;
        Duration = duration;
        KeyFrame = keyFrame;
        ColorInfo = colorInfo;
    }

    /// <summary>
    /// 无参构造函数（供 FramePool 工厂创建空壳）。
    /// </summary>
    /// <remarks>仅供帧对象池使用。调用方须通过 <see cref="Reset"/> 填充实际数据。</remarks>
    public VideoFrame()
    {
        Width = 0;
        Height = 0;
        Format = PixelFormat.YUV420P;
        Resource = null;
        Timestamp = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        KeyFrame = false;
    }

    /// <summary>
    /// 重置帧状态，供 FramePool 复用。
    /// 释放旧 Resource，设置新属性值，重置 IsDisposed。
    /// </summary>
    /// <param name="width">帧宽度。</param>
    /// <param name="height">帧高度。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="resource">帧资源。</param>
    /// <param name="timestamp">显示时间戳。</param>
    /// <param name="duration">帧持续时间。</param>
    /// <param name="keyFrame">是否关键帧。</param>
    public void Reset(int width, int height, PixelFormat format, IFrameResource? resource,
        TimeSpan timestamp, TimeSpan duration, bool keyFrame, VideoColorInfo? colorInfo = null)
    {
        // 释放旧 Resource（安全：SoftwareFrameResource.Dispose 检查 _disposed）
        Resource?.Dispose();

        Width = width;
        Height = height;
        Format = format;
        Resource = resource;
        Timestamp = timestamp;
        Duration = duration;
        KeyFrame = keyFrame;
        ColorInfo = colorInfo;
        IsDisposed = false;
    }

    /// <summary>
    /// 释放帧资源，级联释放 <see cref="Resource"/>。
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Resource?.Dispose();
    }
}

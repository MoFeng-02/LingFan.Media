namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频帧。实现 <see cref="IDisposableFrame"/>，级联释放 <see cref="Resource"/>。
/// </summary>
/// <remarks>
/// 帧所有权转移语义：Decoder → FrameQueue → Renderer。
/// Dispose 时必须级联释放 IFrameResource（GPU 纹理/CPU 内存），防止资源泄漏。
/// </remarks>
public sealed class VideoFrame : IDisposableFrame
{
    /// <summary>帧宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>帧高度（像素）。</summary>
    public int Height { get; }

    /// <summary>像素格式。</summary>
    public PixelFormat Format { get; }

    /// <summary>帧资源（SoftwareFrameResource=CPU 或 GPU 资源=零拷贝句柄）。</summary>
    public IFrameResource Resource { get; }

    /// <summary>显示时间戳（PTS）。</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>帧持续时间。</summary>
    public TimeSpan Duration { get; }

    /// <summary>是否关键帧。</summary>
    public bool KeyFrame { get; }

    /// <inheritdoc/>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// 初始化 <see cref="VideoFrame"/> 的新实例。
    /// </summary>
    public VideoFrame(int width, int height, PixelFormat format, IFrameResource resource,
        TimeSpan timestamp, TimeSpan duration, bool keyFrame)
    {
        Width = width;
        Height = height;
        Format = format;
        Resource = resource;
        Timestamp = timestamp;
        Duration = duration;
        KeyFrame = keyFrame;
    }

    /// <summary>
    /// 释放帧资源，级联释放 <see cref="Resource"/>。
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Resource.Dispose();
    }
}

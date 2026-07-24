namespace LingFan.Media.Abstractions;

/// <summary>
/// CPU 内存帧资源。实现 <see cref="IFrameResource"/>。
/// </summary>
/// <remarks>
/// <para>放在 Abstractions 的原因：Backends.FFmpeg 只引用 Abstractions，
/// FFmpegVideoDecoder 在软件解码路径中需要创建 SoftwareFrameResource。
/// 如果放在 Renderers 模块，Backends 无法访问。</para>
/// <para>内存所有权：<see cref="Data"/> 表示资源拥有该内存。</para>
/// <list type="bullet">
/// <item>FFmpeg 软解路径: av_frame_get_buffer 分配 → 拷贝到 Memory&lt;byte&gt; → av_frame_free 释放原生帧</item>
/// <item>Dispose 时: GC 管理内存（V1 简化，不使用 ArrayPool）</item>
/// </list>
/// </remarks>
public sealed class SoftwareFrameResource : IFrameResource
{
    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>CPU 内存数据（拥有所有权）。</summary>
    public Memory<byte> Data { get; }

    private bool _disposed;

    /// <inheritdoc/>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 初始化 <see cref="SoftwareFrameResource"/> 的新实例。
    /// </summary>
    public SoftwareFrameResource(int width, int height, PixelFormat format, Memory<byte> data)
    {
        Width = width;
        Height = height;
        Format = format;
        Data = data;
    }

    /// <summary>释放内存资源。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

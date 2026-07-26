using System.Buffers;

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
/// <item>V2（L12）: ArrayPool 租借/归还，减少 GC 压力（60fps 每秒 60 个帧）</item>
/// <item>Dispose 时: 归还 ArrayPool 租借的 buffer（若通过 ArrayPool 构造函数创建）</item>
/// </list>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，ArrayPool 为运行时内置 API。</para>
/// </remarks>
public sealed class SoftwareFrameResource : IFrameResource
{
    private byte[]? _rentedBuffer;
    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>CPU 内存数据（拥有所有权，底层由 ArrayPool 或外部提供）。</summary>
    public Memory<byte> Data { get; }

    /// <inheritdoc/>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 从 <see cref="ArrayPool{T}"/> 租借内存创建实例（V2 L12 优化，减少 GC 压力）。
    /// </summary>
    /// <param name="width">帧宽度（像素）。</param>
    /// <param name="height">帧高度（像素）。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="dataLength">所需数据长度（字节）。实际租借的数组可能更长，但 <see cref="Data"/> 仅包含前 <paramref name="dataLength"/> 字节。</param>
    /// <remarks>
    /// 调用方通过 <see cref="Data"/>.<see cref="Memory{T}.Span"/> 写入数据。
    /// Dispose 时自动归还数组到 <see cref="ArrayPool{T}"/>。
    /// </remarks>
    public SoftwareFrameResource(int width, int height, PixelFormat format, int dataLength)
    {
        Width = width;
        Height = height;
        Format = format;
        _rentedBuffer = ArrayPool<byte>.Shared.Rent(dataLength);
        Data = _rentedBuffer.AsMemory(0, dataLength);
    }

    /// <summary>
    /// 使用外部提供的 <see cref="Memory{T}"/> 创建实例（不租借 ArrayPool）。
    /// 保留此构造函数兼容 V1 调用方（如 TestFrameFactory）。
    /// </summary>
    /// <param name="width">帧宽度（像素）。</param>
    /// <param name="height">帧高度（像素）。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="data">外部提供的数据（调用方拥有所有权，Dispose 时不释放）。</param>
    public SoftwareFrameResource(int width, int height, PixelFormat format, Memory<byte> data)
    {
        Width = width;
        Height = height;
        Format = format;
        Data = data;
        // _rentedBuffer = null，Dispose 时不归还 ArrayPool
    }

    /// <summary>释放内存资源（归还 ArrayPool 租借的 buffer）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 归还租借的 buffer 到 ArrayPool（仅 ArrayPool 构造函数创建的实例）
        if (_rentedBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
        }
    }
}

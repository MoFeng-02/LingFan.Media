namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体数据流接口。
/// </summary>
/// <remarks>
/// <para>Read 和 ReadAsync 均采用 caller-allocated buffer 模式。不接受 null buffer。
/// buffer 大小为 0 时返回 0。</para>
/// <para>优先使用 <see cref="ReadAsync"/>（支持 CancellationToken，不阻塞线程）。
/// 同步 <see cref="Read"/> 仅用于同步边界（如 FormatDetector 探测、FFmpeg AVIO 回调）。</para>
/// </remarks>
public interface IMediaStream
{

    /// <summary>
    /// 同步读取数据到指定 buffer。
    /// </summary>
    /// <param name="buffer">目标 buffer（Span 切片）。长度为 0 时返回 0。</param>
    /// <returns>实际读取的字节数，0 表示流结束。</returns>
    /// <exception cref="IOException">读取发生 I/O 错误。</exception>
    /// <remarks>网络流会阻塞调用线程直到数据到达。仅在同步上下文使用。</remarks>
    int Read(Span<byte> buffer);

    /// <summary>异步读取数据到指定 buffer。</summary>
    /// <param name="buffer">目标 buffer。不接受空 buffer。</param>
    /// <param name="ct">取消令牌（网络流必须支持，本地文件可忽略）。</param>
    /// <returns>实际读取的字节数，0 表示流结束。</returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>流长度（不可求时为 -1）。</summary>
    long Length { get; }

    /// <summary>当前读取位置。</summary>
    long Position { get; }

    /// <summary>是否可定位。</summary>
    bool CanSeek { get; }

    /// <summary>定位到指定偏移。</summary>
    long Seek(long offset, SeekOrigin origin);

    /// <summary>关闭流。</summary>
    void Close();
}

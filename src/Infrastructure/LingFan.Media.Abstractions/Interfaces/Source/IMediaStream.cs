namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体数据流接口。
/// </summary>
/// <remarks>
/// ReadAsync 采用 caller-allocated buffer 模式。不接受 null buffer。buffer 大小为 0 时返回 0。
/// </remarks>
public interface IMediaStream
{
    /// <summary>读取数据到指定 buffer。</summary>
    /// <param name="buffer">目标 buffer。</param>
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

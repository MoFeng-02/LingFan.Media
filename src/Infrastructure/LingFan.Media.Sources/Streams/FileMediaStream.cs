using System.IO;

namespace LingFan.Media.Sources;

/// <summary>
/// 文件流包装实现 <see cref="IMediaStream"/>。
/// </summary>
/// <remarks>
/// 非线程安全（IMediaStream 契约：ReadAsync/Seek 不可并发调用）。
/// Close 后所有操作抛 ObjectDisposedException。
/// </remarks>
public sealed class FileMediaStream : IMediaStream
{
    private readonly FileStream _stream;
    private readonly string _path;
    private bool _closed;

    /// <inheritdoc/>
    public long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _stream.Length;
        }
    }

    /// <inheritdoc/>
    public long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _stream.Position;
        }
    }

    /// <inheritdoc/>
    public bool CanSeek
    {
        get
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _stream.CanSeek;
        }
    }

    /// <inheritdoc/>
    public string Location => _path;

    /// <summary>
    /// 初始化 <see cref="FileMediaStream"/> 的新实例并打开文件流。
    /// </summary>
    /// <param name="source">文件媒体源。</param>
    public FileMediaStream(FileMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _stream = new FileStream(
            source.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: false); // 同步 Read（IMediaStream 主路径为 demuxer 同步读取）；false 避免 overlapped I/O 开销（同步为主时更优）
        _path = source.Path;
    }

    /// <inheritdoc/>
    public int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        if (buffer.Length == 0)
            return 0;

        return _stream.Read(buffer);
    }

    /// <inheritdoc/>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        // 空 buffer 直接返回 0（接口契约）
        if (buffer.Length == 0)
            return 0;

        // 本地文件 IO，CT 可透传（FileStream.ReadAsync 原生支持）
        return await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>本地文件流无需建连，为无操作。</remarks>
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        return _stream.Seek(offset, origin);
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (_closed)
            return;

        _closed = true;
        _stream.Dispose();
    }
}

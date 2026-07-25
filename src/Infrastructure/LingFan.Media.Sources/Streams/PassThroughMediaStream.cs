using System.IO;

namespace LingFan.Media.Sources;

/// <summary>
/// 直接透传 <see cref="Stream"/> 的 <see cref="IMediaStream"/> 实现。
/// </summary>
/// <remarks>
/// 非线程安全（IMediaStream 契约：ReadAsync/Seek 不可并发调用）。
/// Close 后所有操作抛 ObjectDisposedException。
/// 如果 <see cref="StreamMediaSource.OwnsStream"/> 为 true，Close 时释放底层流。
/// </remarks>
public sealed class PassThroughMediaStream : IMediaStream
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private bool _closed;

    /// <inheritdoc/>
    public long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _stream.CanSeek ? _stream.Length : -1;
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

    /// <summary>
    /// 初始化 <see cref="PassThroughMediaStream"/> 的新实例。
    /// </summary>
    /// <param name="source">流媒体源。</param>
    public PassThroughMediaStream(StreamMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _stream = source.Stream;
        _ownsStream = source.OwnsStream;
    }

    /// <inheritdoc/>
    public int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        // 空 buffer 直接返回 0（接口契约）
        if (buffer.Length == 0)
            return 0;

        // 直接透传底层 Stream 的同步 Read
        return _stream.Read(buffer);
    }

    /// <inheritdoc/>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        // 空 buffer 直接返回 0（接口契约）
        if (buffer.Length == 0)
            return 0;

        return await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

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

        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

}

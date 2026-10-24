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
    private readonly bool _canSeek;
    private long _position;
    private bool _closed;

    /// <summary>
    /// 初始化 <see cref="PassThroughMediaStream"/> 的新实例。
    /// </summary>
    /// <param name="source">流媒体源。</param>
    public PassThroughMediaStream(StreamMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _stream = source.Stream;
        _ownsStream = source.OwnsStream;
        _canSeek = _stream.CanSeek;

        // 可定位流以底层位置为权威;不可定位流(如 NetworkStream)访问 Position 会抛异常,以计数为准(从 0 起)
        _position = _canSeek ? _stream.Position : 0;
    }

    /// <inheritdoc/>
    public long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _canSeek ? _stream.Length : -1;
        }
    }

    /// <inheritdoc/>
    public long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _canSeek ? _stream.Position : _position;
        }
    }

    /// <inheritdoc/>
    public bool CanSeek
    {
        get
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _canSeek;
        }
    }

    /// <inheritdoc/>
    public string? Location => null; // 透传流无地址；需以字节流方式打开的 backend 不适用按地址打开

    /// <inheritdoc/>
    /// <remarks>透传流无地址语义,无建议打开头。</remarks>
    public IReadOnlyDictionary<string, string>? SuggestedOpenHeaders => null;

    /// <inheritdoc/>
    public int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        // 空 buffer 直接返回 0（接口契约）
        if (buffer.Length == 0)
            return 0;

        // 直接透传底层 Stream 的同步 Read
        int read = _stream.Read(buffer);
        if (!_canSeek)
            _position += read;
        return read;
    }

    /// <inheritdoc/>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        // 空 buffer 直接返回 0（接口契约）
        if (buffer.Length == 0)
            return 0;

        var read = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (!_canSeek)
            _position += read;
        return read;
    }

    /// <inheritdoc/>
    /// <remarks>透传流底层已就绪，无需建连，为无操作。</remarks>
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>不可定位流在此明确拒绝（而非透传底层流的实现相关异常）。</remarks>
    public long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        if (!_canSeek)
            throw new NotSupportedException("底层流不可定位，无法 Seek。");

        _position = _stream.Seek(offset, origin);
        return _position;
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

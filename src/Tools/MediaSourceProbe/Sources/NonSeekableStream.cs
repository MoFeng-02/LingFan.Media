namespace MediaSourceProbe.Sources;

/// <summary>
/// 不可定位流装饰器：隐藏底层可定位性（<see cref="CanSeek"/>=false），位置走自计数。
/// </summary>
/// <remarks>
/// 用于验证线性播放兜底：探测跳过、解封装不回退、无定位回调。
/// 本装饰器不拥有底层流，<see cref="Dispose"/> 只释放自身，底层流由创建方释放。
/// </remarks>
internal sealed class NonSeekableStream(Stream inner) : Stream
{
    private long _position;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException("不可定位流无长度语义");
    public override long Position { get => _position; set => throw new NotSupportedException("不可定位流"); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("不可定位流");
    public override void SetLength(long value) => throw new NotSupportedException("不可定位流");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("只读");
    public override void Flush() { }

    /// <summary>只释放本装饰器；底层流归创建方所有，此处不得释放。</summary>
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}

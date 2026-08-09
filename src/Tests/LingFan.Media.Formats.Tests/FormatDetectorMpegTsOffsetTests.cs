using FluentAssertions;
using LingFan.Media.Abstractions;
using LingFan.Media.Formats;
using LingFan.Media.Formats.Detection;

namespace LingFan.Media.Formats.Tests;

/// <summary>
/// 内存 IMediaStream 测试替身：包裹一段字节数据，支持同步 Read/Seek（Detect 需要）。
/// </summary>
file sealed class MemoryMediaStream : IMediaStream
{
    private readonly byte[] _data;
    private int _position;

    public MemoryMediaStream(byte[] data) => _data = data;

    public long Length => _data.Length;
    public long Position
    {
        get => _position;
        set => _position = (int)value;
    }
    public bool CanSeek => true;

    public string? Location => null;

    public int Read(Span<byte> buffer)
    {
        if (_position >= _data.Length)
            return 0;
        int toCopy = Math.Min(buffer.Length, _data.Length - _position);
        _data.AsSpan(_position, toCopy).CopyTo(buffer);
        _position += toCopy;
        return toCopy;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => (int)offset,
            SeekOrigin.Current => _position + (int)offset,
            SeekOrigin.End => _data.Length + (int)offset,
            _ => _position,
        };
        return _position;
    }

    public void Close() { }
}

public class FormatDetectorMpegTsOffsetTests
{
    private static byte[] BuildTsBuffer(int startOffset)
    {
        var buf = new byte[4096];
        // 在任意偏移放置连续的 0x47 sync byte（间隔 188）
        buf[startOffset] = 0x47;
        buf[startOffset + 188] = 0x47;
        buf[startOffset + 376] = 0x47;
        return buf;
    }

    [Fact]
    public void Detect_RecognizesTsAtOffsetZero()
    {
        var stream = new MemoryMediaStream(BuildTsBuffer(0));
        FormatDetector.Detect(stream).Should().Be(ContainerFormat.TS);
    }

    [Fact]
    public void Detect_RecognizesTsAtNonZeroOffset() // L5：非零偏移起始
    {
        var stream = new MemoryMediaStream(BuildTsBuffer(10));
        FormatDetector.Detect(stream).Should().Be(ContainerFormat.TS);
    }

    [Fact]
    public void Detect_RecognizesTsAtOffsetNearEnd()
    {
        var stream = new MemoryMediaStream(BuildTsBuffer(2048));
        FormatDetector.Detect(stream).Should().Be(ContainerFormat.TS);
    }

    [Fact]
    public void Detect_ReturnsUnknownForRandomData()
    {
        var buf = new byte[4096];
        new Random(42).NextBytes(buf);
        // 确保没有恰好形成 TS 同步序列（极低概率，固定种子可控）
        var stream = new MemoryMediaStream(buf);
        FormatDetector.Detect(stream).Should().Be(ContainerFormat.Unknown);
    }
}

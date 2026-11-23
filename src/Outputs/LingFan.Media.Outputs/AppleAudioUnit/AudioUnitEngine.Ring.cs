using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.AppleAudioUnit;

/// <summary>
/// 数据通路分部：Submit 背压写入、渲染回调拉取与 S16 软件增益（环形缓冲，回调与 Submit 经 _gate 同步）。
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
internal sealed unsafe partial class AudioUnitEngine
{
    // 环形缓冲（托管，回调与 Submit 经 _gate 同步）
    private readonly object _gate = new();
    private byte[] _ring = [];
    private int _readPos;
    private int _writePos;
    private int _count;

    /// <summary>提交音频帧：软件增益后写入环形缓冲（满时阻塞背压，2 秒超时）。同步边界（原生调用）；不接管帧所有权。</summary>
    internal void Submit(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioUnit == IntPtr.Zero)
            throw new InvalidOperationException($"{_displayName} 输出尚未初始化，无法提交音频帧。");
        if (frame.Channels != _channels)
            throw new ArgumentException($"音频帧声道数 {frame.Channels} 与输出配置 {_channels} 不匹配。", nameof(frame));

        // 仅支持 S16（流格式固定 lpcm S16；上游需保证一致，否则由宿主/管线转换）
        if (frame.SampleFormat != SampleFormat.S16)
            throw new NotSupportedException($"{_displayName} 输出仅支持 S16，收到 {frame.SampleFormat}。");

        int byteLength = frame.FrameCount * frame.Channels * 2; // S16
        if (frame.Data.Length < byteLength)
            throw new ArgumentException($"音频帧数据不足：期望 {byteLength} 字节，实际 {frame.Data.Length} 字节。", nameof(frame));

        ReadOnlySpan<byte> src = frame.Data.Span[..byteLength];

        if (_volume >= 0.999f)
        {
            WriteToRing(src);
            return;
        }

        // 软件增益：S16 样本缩放到租用缓冲（与 AAudio 一致）
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteLength);
        try
        {
            Span<byte> dst = rented.AsSpan(0, byteLength);
            src.CopyTo(dst);
            ApplyGainS16(dst, _volume);
            WriteToRing(dst);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void WriteToRing(ReadOnlySpan<byte> src)
    {
        int offset = 0;
        lock (_gate)
        {
            while (offset < src.Length)
            {
                while (_count == _ring.Length)
                {
                    if (!Monitor.Wait(_gate, SubmitTimeoutMs))
                        throw new TimeoutException($"{_displayName} 输出缓冲写入超时（2 秒内渲染回调未释放空间）。");
                    ObjectDisposedException.ThrowIf(_disposed, this);
                }

                int toWrite = Math.Min(_ring.Length - _count, src.Length - offset);
                int firstSegment = Math.Min(toWrite, _ring.Length - _writePos);
                src.Slice(offset, firstSegment).CopyTo(_ring.AsSpan(_writePos));
                if (toWrite > firstSegment)
                    src.Slice(offset + firstSegment, toWrite - firstSegment).CopyTo(_ring.AsSpan(0));

                _writePos = (_writePos + toWrite) % _ring.Length;
                _count += toWrite;
                offset += toWrite;
            }
        }
    }

    /// <summary>渲染回调（CoreAudio 实时线程）：从环形缓冲拉取 PCM，不足补零静音。</summary>
    [UnmanagedCallersOnly]
    private static int RenderCallback(IntPtr refCon, IntPtr ioActionFlags, IntPtr inTimeStamp,
        uint inBusNumber, uint inNumberFrames, AudioBufferList* ioData)
    {
        if (ioData == null)
            return NoErr;

        var engine = GCHandle.FromIntPtr(refCon).Target as AudioUnitEngine;
        AudioBuffer* buffers = &ioData->Buffer0;

        for (uint i = 0; i < ioData->NumberBuffers; i++)
        {
            AudioBuffer* buf = buffers + i;
            if (buf->Data == IntPtr.Zero || buf->DataByteSize == 0)
                continue;

            var dst = new Span<byte>((void*)buf->Data, (int)buf->DataByteSize);
            int copied = 0;

            if (engine is not null && !engine._disposed)
            {
                lock (engine._gate)
                {
                    int toCopy = Math.Min(dst.Length, engine._count);
                    int firstSegment = Math.Min(toCopy, engine._ring.Length - engine._readPos);
                    engine._ring.AsSpan(engine._readPos, firstSegment).CopyTo(dst);
                    if (toCopy > firstSegment)
                        engine._ring.AsSpan(0, toCopy - firstSegment).CopyTo(dst[firstSegment..]);

                    engine._readPos = (engine._readPos + toCopy) % Math.Max(engine._ring.Length, 1);
                    engine._count -= toCopy;
                    copied = toCopy;
                    Monitor.PulseAll(engine._gate);
                }

                if (engine._bytesPerFrame > 0)
                    Interlocked.Add(ref engine._framesRendered, copied / engine._bytesPerFrame);
            }

            if (copied < dst.Length)
                dst[copied..].Clear(); // underrun → 静音
        }

        return NoErr;
    }

    /// <summary>对 S16 交错 PCM 应用线性增益（就地缩放）。</summary>
    private static void ApplyGainS16(Span<byte> pcm, float gain)
    {
        Span<short> samples = MemoryMarshal.Cast<byte, short>(pcm);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)Math.Clamp((int)(samples[i] * gain), short.MinValue, short.MaxValue);
    }
}
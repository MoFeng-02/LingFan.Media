namespace LingFan.Media.Audio;

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;

/// <summary>
/// 混音通道。接收一路音频数据，按通道音量/静音状态参与混音。
/// </summary>
/// <remarks>
/// <para><see cref="Submit"/> 将 <see cref="AudioFrame"/> 数据直接解码为 float 采样并写入内部环形缓冲区，
/// 供 <see cref="AudioMixer.Mix"/> 读取。使用 <see cref="lock"/> 保证线程安全
/// （Submit 从解码线程调用，Mix 从音频输出线程调用）。</para>
/// <para>（AU5）：内部缓冲由 <see cref="System.Collections.Generic.Queue{T}"/> 改为
/// <see cref="ArrayPool{T}"/> 环形缓冲区，消除逐采样 Enqueue 的装箱/动态扩容开销，
/// 且不再分配中间 float[]（直接解码入环）。</para>
/// <para>实现 <see cref="IDisposable"/> 以归还池化数组。</para>
/// </remarks>
public sealed class MixerChannel : IDisposable
{
    private float[]? _buf;
    private int _capacity;
    private int _writePos;
    private int _readPos;
    private int _count;
    private readonly object _lock = new();
    private int _inputChannels;

    /// <summary>通道最近一次提交输入的声道数（供 AudioMixer 自动声道转换）。</summary>
    public int InputChannels => _inputChannels;

    /// <summary>通道名称。</summary>
    public string Name { get; }

    /// <summary>通道音量（0.0~1.0）。</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }
    private float _volume = 1.0f;

    /// <summary>是否静音。</summary>
    public bool IsMuted { get; set; }

    /// <summary>是否激活。</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// 初始化 <see cref="MixerChannel"/> 的新实例。
    /// </summary>
    public MixerChannel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>激活通道。</summary>
    public void Activate() => IsActive = true;

    /// <summary>停用通道。</summary>
    public void Deactivate() => IsActive = false;

    /// <summary>
    /// 提交音频数据到通道。
    /// </summary>
    /// <param name="frame">音频帧（数据被解码并拷贝到内部环形缓冲，frame 本身不被 Dispose）。</param>
    public void Submit(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var fmt = frame.SampleFormat;
        int bps = PcmConversions.BytesPerSample(fmt);
        var bytes = frame.Data.Span;
        int sampleCount = bytes.Length / bps;
        _inputChannels = frame.Channels;

        lock (_lock)
        {
            EnsureCapacity(_count + sampleCount);
            DecodeIntoRing(bytes[..(sampleCount * bps)], fmt, sampleCount);
        }
    }

    private void DecodeIntoRing(ReadOnlySpan<byte> pcm, SampleFormat fmt, int sampleCount)
    {
        switch (fmt)
        {
            case SampleFormat.S16:
            {
                var s = MemoryMarshal.Cast<byte, short>(pcm);
                for (int i = 0; i < sampleCount; i++) WriteSample(s[i] / 32768f);
                break;
            }
            case SampleFormat.S32:
            {
                var s = MemoryMarshal.Cast<byte, int>(pcm);
                for (int i = 0; i < sampleCount; i++) WriteSample(s[i] / 2147483648f);
                break;
            }
            case SampleFormat.F32:
            {
                var s = MemoryMarshal.Cast<byte, float>(pcm);
                for (int i = 0; i < sampleCount; i++) WriteSample(s[i]);
                break;
            }
        }
    }

    private void WriteSample(float s)
    {
        if (_count == _capacity) Grow();
        _buf![_writePos] = s;
        _writePos = (_writePos + 1) % _capacity;
        _count++;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _capacity) return;
        Grow(needed);
    }

    private void Grow(int? minCapacity = null)
    {
        int need = minCapacity ?? (_capacity + 1);
        int newCap = _capacity == 0 ? 1024 : _capacity * 2;
        while (newCap < need) newCap *= 2;
        var newBuf = ArrayPool<float>.Shared.Rent(newCap);
        for (int i = 0; i < _count; i++)
            newBuf[i] = _buf![(_readPos + i) % _capacity];
        if (_buf != null) ArrayPool<float>.Shared.Return(_buf);
        _buf = newBuf;
        _capacity = newCap;
        _readPos = 0;
        _writePos = _count;
    }

    /// <summary>
    /// 从通道缓冲区读取采样数据到指定 Span。
    /// </summary>
    /// <param name="output">输出缓冲区（长度应为 sampleCount * 输入声道数）。</param>
    /// <returns>实际读取的采样数（不足部分由调用方补零）。</returns>
    public int Read(Span<float> output)
    {
        lock (_lock)
        {
            var count = Math.Min(output.Length, _count);
            for (int i = 0; i < count; i++)
            {
                output[i] = _buf![_readPos];
                _readPos = (_readPos + 1) % _capacity;
                _count--;
            }
            return count;
        }
    }

    /// <summary>清空通道缓冲区。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _count = 0;
            _readPos = 0;
            _writePos = 0;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_buf != null)
            {
                ArrayPool<float>.Shared.Return(_buf);
                _buf = null;
            }
            _capacity = 0;
            _count = 0;
            _readPos = 0;
            _writePos = 0;
        }
    }
}

namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频帧。实现 <see cref="IDisposableFrame"/>。
/// </summary>
/// <remarks>
/// <para>Span 访问模式（文档注释）：</para>
/// <code>
/// ReadOnlySpan&lt;float&gt; samples = MemoryMarshal.Cast&lt;byte, float&gt;(frame.Data.Span);
/// ReadOnlySpan&lt;short&gt; samples = MemoryMarshal.Cast&lt;byte, short&gt;(frame.Data.Span);
/// </code>
/// </remarks>
public sealed class AudioFrame : IDisposableFrame
{
    /// <summary>PCM 音频数据（只读视图，底层 buffer 由帧拥有）。</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>采样率（Hz，如 44100）。</summary>
    public int SampleRate { get; }

    /// <summary>声道数。</summary>
    public int Channels { get; }

    /// <summary>采样格式。</summary>
    public SampleFormat SampleFormat { get; }

    /// <summary>时间戳。</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>帧持续时间。</summary>
    public TimeSpan Duration { get; }

    /// <summary>本帧包含的采样数。</summary>
    public int FrameCount { get; }

    /// <inheritdoc/>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// 初始化 <see cref="AudioFrame"/> 的新实例。
    /// </summary>
    public AudioFrame(ReadOnlyMemory<byte> data, int sampleRate, int channels,
        SampleFormat sampleFormat, TimeSpan timestamp, TimeSpan duration, int frameCount)
    {
        Data = data;
        SampleRate = sampleRate;
        Channels = channels;
        SampleFormat = sampleFormat;
        Timestamp = timestamp;
        Duration = duration;
        FrameCount = frameCount;
    }

    /// <summary>
    /// 释放底层 buffer。
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
    }
}

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
/// <para><b>V2 池化支持</b>：属性使用 internal set，<see cref="Reset"/> 方法供解码器复用帧实例。</para>
/// </remarks>
public sealed class AudioFrame : IDisposableFrame
{
    /// <summary>PCM 音频数据（只读视图，底层 buffer 由帧拥有）。</summary>
    public ReadOnlyMemory<byte> Data { get; internal set; }

    /// <summary>采样率（Hz，如 44100）。</summary>
    public int SampleRate { get; internal set; }

    /// <summary>声道数。</summary>
    public int Channels { get; internal set; }

    /// <summary>采样格式。</summary>
    public SampleFormat SampleFormat { get; internal set; }

    /// <summary>时间戳。</summary>
    public TimeSpan Timestamp { get; internal set; }

    /// <summary>帧持续时间。</summary>
    public TimeSpan Duration { get; internal set; }

    /// <summary>本帧包含的采样数。</summary>
    public int FrameCount { get; internal set; }

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
    /// 无参构造函数（供 FramePool 工厂创建空壳）。
    /// </summary>
    /// <remarks>仅供帧对象池使用。调用方须通过 <see cref="Reset"/> 填充实际数据。</remarks>
    public AudioFrame()
    {
        Data = default;
        SampleRate = 0;
        Channels = 0;
        SampleFormat = SampleFormat.S16;
        Timestamp = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        FrameCount = 0;
    }

    /// <summary>
    /// 重置帧状态，供 FramePool 复用。
    /// 设置新属性值，重置 IsDisposed。
    /// </summary>
    public void Reset(ReadOnlyMemory<byte> data, int sampleRate, int channels,
        SampleFormat sampleFormat, TimeSpan timestamp, TimeSpan duration, int frameCount)
    {
        Data = data;
        SampleRate = sampleRate;
        Channels = channels;
        SampleFormat = sampleFormat;
        Timestamp = timestamp;
        Duration = duration;
        FrameCount = frameCount;
        IsDisposed = false;
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

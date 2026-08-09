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
/// <para>属性使用 internal set，<see cref="Reset"/> 方法供解码器复用帧实例（帧池化）。</para>
/// <para><b>零拷贝支持</b>：<see cref="Data"/> 可直接映射原生引用计数 buffer，
/// 所有者以中立 <see cref="IDisposable"/> 经 <see cref="Reset"/> 传入；
/// <see cref="Dispose"/> 与下一次 <see cref="Reset"/> 均会释放所有者（原生引用计数减一）。
/// 所有者释放后不得再访问 <see cref="Data"/>。</para>
/// </remarks>
public sealed class AudioFrame : IDisposableFrame
{
    private IDisposable? _dataOwner;

    /// <summary>PCM 音频数据（只读视图，底层 buffer 由帧拥有或经引用计数共享）。</summary>
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
    /// 释放旧的零拷贝所有者（若有），设置新属性值，重置 IsDisposed。
    /// </summary>
    /// <param name="data">PCM 数据（托管副本或零拷贝原生视图）。</param>
    /// <param name="sampleRate">采样率。</param>
    /// <param name="channels">声道数。</param>
    /// <param name="sampleFormat">采样格式。</param>
    /// <param name="timestamp">时间戳。</param>
    /// <param name="duration">帧持续时间。</param>
    /// <param name="frameCount">采样数。</param>
    /// <param name="dataOwner">
    /// 零拷贝所有者（可选）。非 null 时 <paramref name="data"/> 映射原生引用计数 buffer。
    /// </param>
    public void Reset(ReadOnlyMemory<byte> data, int sampleRate, int channels,
        SampleFormat sampleFormat, TimeSpan timestamp, TimeSpan duration, int frameCount,
        IDisposable? dataOwner = null)
    {
        // 释放旧的零拷贝所有者（防泄漏：池复用路径旧帧可能仍持有原生引用）
        _dataOwner?.Dispose();
        _dataOwner = dataOwner;

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
    /// 释放底层 buffer（零拷贝路径：原生引用计数减一）。
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        if (_dataOwner != null)
        {
            _dataOwner.Dispose();
            _dataOwner = null;
        }
    }
}

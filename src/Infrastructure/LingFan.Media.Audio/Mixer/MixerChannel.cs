namespace LingFan.Media.Audio;

/// <summary>
/// 混音通道。接收一路音频数据，按通道音量/静音状态参与混音。
/// </summary>
/// <remarks>
/// <para><see cref="Submit"/> 将 <see cref="AudioFrame"/> 数据转换为 float 采样后存入内部缓冲区，
/// 供 <see cref="AudioMixer.Mix"/> 读取。使用 <see cref="lock"/> 保证线程安全
/// （Submit 从解码线程调用，Mix 从音频输出线程调用）。</para>
/// <para>内部缓冲区使用 <see cref="Queue{T}"/> 存储 float 采样（V1 实现）。
/// V2 可替换为环形缓冲区 + <see cref="System.Buffers.ArrayPool{T}"/> 以减少分配。</para>
/// <para><b>V1 限制</b>：通道不跟踪输入声道数，调用方需确保所有通道的输入声道数
/// 与 <see cref="MixerSettings.Channels"/> 一致，否则采样会错位。</para>
/// </remarks>
public sealed class MixerChannel
{
    private readonly Queue<float> _buffer = new();
    private readonly object _lock = new();

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
    /// <param name="name">通道名称。</param>
    /// <exception cref="ArgumentNullException">name 为 null。</exception>
    public MixerChannel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>
    /// 激活通道。
    /// </summary>
    public void Activate() => IsActive = true;

    /// <summary>
    /// 停用通道。
    /// </summary>
    public void Deactivate() => IsActive = false;

    /// <summary>
    /// 提交音频数据到通道。
    /// </summary>
    /// <param name="frame">音频帧（数据将被拷贝到内部缓冲区，frame 本身不被 Dispose）。</param>
    /// <exception cref="ArgumentNullException">frame 为 null。</exception>
    public void Submit(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var samples = ConvertToFloat(frame);
        lock (_lock)
        {
            // V1: 直接入队。V2 可用 ArrayPool + 环形缓冲区减少分配。
            for (int i = 0; i < samples.Length; i++)
            {
                _buffer.Enqueue(samples[i]);
            }
        }
    }

    /// <summary>
    /// 从通道缓冲区读取采样数据到指定 Span。
    /// </summary>
    /// <param name="output">输出缓冲区。</param>
    /// <returns>实际读取的采样数（不足部分由调用方补零）。</returns>
    internal int Read(Span<float> output)
    {
        lock (_lock)
        {
            var count = Math.Min(output.Length, _buffer.Count);
            for (int i = 0; i < count; i++)
            {
                output[i] = _buffer.Dequeue();
            }
            return count;
        }
    }

    /// <summary>
    /// 清空通道缓冲区。
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }

    /// <summary>
    /// 将 AudioFrame 数据转换为 float 采样（交错排列）。
    /// </summary>
    private static ReadOnlySpan<float> ConvertToFloat(AudioFrame frame)
    {
        var data = frame.Data.Span;
        var bytesPerSample = frame.SampleFormat switch
        {
            SampleFormat.S16 => 2,
            SampleFormat.S32 => 4,
            SampleFormat.F32 => 4,
            _ => 4,
        };

        var sampleCount = data.Length / bytesPerSample;
        // 防御性切片：MemoryMarshal.Cast 要求源 span 长度是目标类型大小的整数倍，
        // 截断尾部不完整字节避免 ArgumentException
        var validData = data[..(sampleCount * bytesPerSample)];
        var result = new float[sampleCount];

        switch (frame.SampleFormat)
        {
            case SampleFormat.S16:
            {
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(validData);
                for (int i = 0; i < sampleCount; i++)
                {
                    result[i] = src[i] / 32768f;
                }
                break;
            }
            case SampleFormat.S32:
            {
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(validData);
                for (int i = 0; i < sampleCount; i++)
                {
                    result[i] = src[i] / 2147483648f;
                }
                break;
            }
            case SampleFormat.F32:
            {
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(validData);
                src.CopyTo(result);
                break;
            }
        }

        return result;
    }
}

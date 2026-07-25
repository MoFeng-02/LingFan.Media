namespace LingFan.Media.Audio;

/// <summary>
/// 多路音频混音器。将多个 <see cref="MixerChannel"/> 的音频数据混合为一路输出。
/// </summary>
/// <remarks>
/// <para>混音流程：</para>
/// <list type="number">
/// <item>从每个激活且未静音的通道获取 sampleCount 个采样</item>
/// <item>将所有通道的采样相加（每个通道乘以其 Volume）</item>
/// <item>乘以 <see cref="MasterVolume"/></item>
/// <item>Clipping 到 [-1.0, 1.0] 防止溢出</item>
/// <item>转换为输出格式并返回 <see cref="AudioFrame"/></item>
/// </list>
/// <para>使用 <see cref="Span{T}"/> 做零拷贝 DSP 处理。</para>
/// <para><see cref="Mix"/> 方法线程安全（通过各通道内部锁保证），但通道列表的增删
/// 应在播放启动前完成（非线程安全）。</para>
/// <para><b>V1 限制</b>：所有通道的输入声道数需与 <see cref="Settings"/>.<see cref="MixerSettings.Channels"/>
/// 一致，Mix 不做声道数转换。V2 可增加自动声道数转换。</para>
/// </remarks>
public sealed class AudioMixer
{
    private readonly List<MixerChannel> _channels = [];
    private readonly MixerSettings _settings;
    private float _masterVolume = 1.0f;

    /// <summary>混音通道列表（只读视图）。</summary>
    public IReadOnlyList<MixerChannel> Channels => _channels;

    /// <summary>主音量（0.0~1.0）。</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>混音输出设置。</summary>
    public MixerSettings Settings => _settings;

    /// <summary>
    /// 初始化 <see cref="AudioMixer"/> 的新实例。
    /// </summary>
    /// <param name="settings">混音输出设置。</param>
    /// <exception cref="ArgumentNullException">settings 为 null。</exception>
    public AudioMixer(MixerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>
    /// 创建并添加混音通道。
    /// </summary>
    /// <param name="name">通道名称。</param>
    /// <returns>新创建的通道。</returns>
    /// <exception cref="ArgumentNullException">name 为 null。</exception>
    public MixerChannel CreateChannel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var channel = new MixerChannel(name);
        _channels.Add(channel);
        return channel;
    }

    /// <summary>
    /// 移除混音通道。
    /// </summary>
    /// <param name="channel">要移除的通道。</param>
    public void RemoveChannel(MixerChannel channel)
    {
        _channels.Remove(channel);
    }

    /// <summary>
    /// 混合指定采样数的音频。
    /// </summary>
    /// <param name="sampleCount">每声道采样数（如 1024 = 每声道 1024 个采样点）。</param>
    /// <returns>混合后的 <see cref="AudioFrame"/>。若无激活通道，返回静音帧。</returns>
    /// <remarks>
    /// <para>从每个激活且未静音的通道获取 sampleCount * Channels 个交错采样，
    /// 乘以通道音量后相加，再乘以主音量，最后 clipping 到 [-1.0, 1.0]。</para>
    /// <para>不足的采样以静音（0.0）填充。</para>
    /// </remarks>
    public AudioFrame Mix(int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);

        var totalSamples = sampleCount * _settings.Channels;

        // 混音缓冲区（stackalloc 小缓冲优化，切片到实际长度）
        Span<float> mixBuffer = (totalSamples <= 256
            ? stackalloc float[256]
            : new float[totalSamples])[..totalSamples];
        mixBuffer.Clear();

        Span<float> channelBuffer = (totalSamples <= 256
            ? stackalloc float[256]
            : new float[totalSamples])[..totalSamples];

        foreach (var channel in _channels)
        {
            if (!channel.IsActive || channel.IsMuted || channel.Volume <= 0f)
                continue;

            channelBuffer.Clear();
            var read = channel.Read(channelBuffer);

            var volume = channel.Volume;
            for (int i = 0; i < read; i++)
            {
                mixBuffer[i] += channelBuffer[i] * volume;
            }
        }

        // 应用主音量 + clipping
        for (int i = 0; i < totalSamples; i++)
        {
            var sample = mixBuffer[i] * _masterVolume;
            mixBuffer[i] = Math.Clamp(sample, -1f, 1f);
        }

        // 转换为输出格式
        var data = ConvertFromFloat(mixBuffer, _settings.SampleFormat);

        return new AudioFrame(
            data,
            _settings.SampleRate,
            _settings.Channels,
            _settings.SampleFormat,
            TimeSpan.Zero,
            TimeSpan.Zero,
            sampleCount);
    }

    /// <summary>
    /// 清空所有通道的缓冲区。
    /// </summary>
    public void ClearAll()
    {
        foreach (var channel in _channels)
        {
            channel.Clear();
        }
    }

    /// <summary>
    /// 将 float 采样转换为指定格式的字节数据。
    /// </summary>
    private static byte[] ConvertFromFloat(ReadOnlySpan<float> samples, SampleFormat format)
    {
        return format switch
        {
            SampleFormat.F32 => ConvertToF32(samples),
            SampleFormat.S16 => ConvertToS16(samples),
            SampleFormat.S32 => ConvertToS32(samples),
            _ => ConvertToF32(samples),
        };
    }

    private static byte[] ConvertToF32(ReadOnlySpan<float> samples)
    {
        var result = new byte[samples.Length * sizeof(float)];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples)
            .CopyTo(result);
        return result;
    }

    private static byte[] ConvertToS16(ReadOnlySpan<float> samples)
    {
        var result = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            result[i] = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
        }
        var bytes = new byte[result.Length * sizeof(short)];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes<short>(result)
            .CopyTo(bytes);
        return bytes;
    }

    private static byte[] ConvertToS32(ReadOnlySpan<float> samples)
    {
        var result = new int[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            // samples 已被 clamp 到 [-1, 1]
            // int.MaxValue (2147483647) 无法精确表示为 float（舍入为 2^31 = 2147483648.0f）
            // 直接 (int)(1.0f * 2147483647f) 会溢出为 int.MinValue
            // 用显式边界比较避免溢出
            if (samples[i] >= 1.0f)
                result[i] = int.MaxValue;
            else if (samples[i] <= -1.0f)
                result[i] = int.MinValue;
            else
                result[i] = (int)(samples[i] * 2147483647f);
        }
        var bytes = new byte[result.Length * sizeof(int)];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes<int>(result)
            .CopyTo(bytes);
        return bytes;
    }
}

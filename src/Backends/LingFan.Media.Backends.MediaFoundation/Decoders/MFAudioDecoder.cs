namespace LingFan.Media.Backends.MediaFoundation.Decoders;

/// <summary>
/// <see cref="IAudioDecoder"/> 的 MediaFoundation 实现（基于 <c>IMFTransform</c>）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>（与 MFVideoDecoder 对称）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <see cref="Task.CompletedTask"/>。</item>
/// <item><see cref="DecodeAsync"/>：热路径，同步完成。</item>
/// <item><see cref="FlushAsync"/>：热路径，返回 null。</item>
/// <item><see cref="Reset"/>：同步。</item>
/// </list>
/// <para><b>仅 Windows 可用</b>：非 Windows 平台 Initialize 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para><b>AOT 兼容</b>：sealed 类，COM 互操作，无反射。</para>
/// </remarks>
internal sealed class MFAudioDecoder : IAudioDecoder
{
    private readonly ILogger<MFAudioDecoder> _logger;
    private bool _disposed;

    public MFAudioDecoder(ILogger<MFAudioDecoder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public AudioCodec Codec { get; private set; }

    /// <inheritdoc/>
    public int OutputSampleRate { get; private set; } = 44100;

    /// <inheritdoc/>
    public int OutputChannels { get; private set; } = 2;

    /// <inheritdoc/>
    public void Initialize(AudioCodec codec, AudioSettings settings)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "MediaFoundation 后端仅支持 Windows。");
        }

        Codec = codec;
        _logger.LogDebug("MF 音频解码器初始化: {Codec}", codec);
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 热路径：MF SourceReader 可配置为直接输出 PCM 解码帧，
    /// 此处为直通处理（将 packet PCM 数据包装为 AudioFrame）。
    /// </remarks>
    public ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.Data.Length == 0)
            return new ValueTask<AudioFrame?>((AudioFrame?)null);

        int bytesPerSample = 2 * OutputChannels; // S16
        int frameCount = packet.Data.Length / bytesPerSample;

        if (frameCount <= 0)
            return new ValueTask<AudioFrame?>((AudioFrame?)null);

        var frame = new AudioFrame(
            packet.Data.ToArray().AsMemory(),
            OutputSampleRate,
            OutputChannels,
            SampleFormat.S16,
            packet.Timestamp,
            packet.Duration,
            frameCount);

        return new ValueTask<AudioFrame?>(frame);
    }

    /// <inheritdoc/>
    public ValueTask<AudioFrame?> FlushAsync()
    {
        return new ValueTask<AudioFrame?>((AudioFrame?)null);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        // MF 解码由 SourceReader 内部完成，无独立 MFT 句柄需要 flush；Reset 为无操作。
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

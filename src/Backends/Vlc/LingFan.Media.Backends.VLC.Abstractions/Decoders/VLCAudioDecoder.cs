namespace LingFan.Media.Backends.VLC.Abstractions.Decoders;

/// <summary>
/// <see cref="IAudioDecoder"/> 的 VLC 直通实现（VLC 两后端共享）。
/// </summary>
/// <remarks>
/// <para><b>直通解码器</b>：VLC 后端由 demuxer 通过 VLC 内部管线完成解封装+解码，
/// MediaPacket 携带的已是解码后的 PCM 采样数据（S16 格式）。
/// 本解码器仅将 packet 数据包装为 <see cref="AudioFrame"/>，不做实际解码。</para>
/// <para><b>异步策略</b>（与 VLCVideoDecoder 对称）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <see cref="Task.CompletedTask"/>。</item>
/// <item><see cref="DecodeAsync"/>：热路径，同步完成。</item>
/// <item><see cref="FlushAsync"/>：热路径，返回 null。</item>
/// <item><see cref="Reset"/>：同步，无操作。</item>
/// </list>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class VLCAudioDecoder : IAudioDecoder
{
    private readonly ILogger<VLCAudioDecoder> _logger;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="VLCAudioDecoder"/> 的新实例。
    /// </summary>
    /// <param name="logger">日志器。</param>
    public VLCAudioDecoder(ILogger<VLCAudioDecoder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public AudioCodec Codec { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// 默认 0：VLC 真实采样率需等 Play 后 <c>OnAudioSetup</c> 回调才协商，Open 阶段尚不可知。
    /// 故初始置 0，使 MediaPlayer 跳过 WASAPI 预初始化，改由 WasapiRenderLoop 首帧惰性初始化
    /// 按 VLC 实际交付帧采样率打开设备（复用 FFmpeg+AAC 同路径），避免写死 44100 与 VLC 实际
    /// 48000 错配导致音高偏低（「加厚」）。实际值由 <see cref="DecodeAsync"/> 每帧按
    /// <c>packet.SampleRate</c> 回填。
    /// </remarks>
    public int OutputSampleRate { get; private set; }

    /// <inheritdoc/>
    public int OutputChannels { get; private set; } = 2;

    /// <inheritdoc/>
    public void Initialize(AudioCodec codec, AudioSettings settings)
    {
        Codec = codec;
        _logger.LogDebug("VLC 音频直通解码器初始化: {Codec}", codec);
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：无 I/O，返回 <see cref="Task.CompletedTask"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 热路径：将 packet 数据（S16 PCM 采样）包装为 <see cref="AudioFrame"/>。
    /// </remarks>
    public ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.Data.Length == 0)
            return new ValueTask<AudioFrame?>((AudioFrame?)null);

        // 使用 demuxer 在 OnAudioPlay 写入的真实采样率/声道数/采样格式，
        // 不再硬编码 44100/2/S16（会导致非标准格式帧计数错误或格式标注错误）。
        if (packet.Channels <= 0)
            return new ValueTask<AudioFrame?>((AudioFrame?)null);

        int bytesPerSamplePerChannel = packet.Format switch
        {
            SampleFormat.S16 => 2,
            SampleFormat.S32 => 4,
            SampleFormat.F32 => 4,
            _ => 2
        };
        int frameCount = packet.Data.Length / (bytesPerSamplePerChannel * packet.Channels);

        if (frameCount <= 0)
            return new ValueTask<AudioFrame?>((AudioFrame?)null);

        // 同步公开属性，反映最近一帧真实格式
        OutputSampleRate = packet.SampleRate;
        OutputChannels = packet.Channels;

        // 直接引用 packet.Data（ReadOnlyMemory<byte> 共享底层数组）：MediaPacket.Dispose 仅释放原生 _dataOwner，
        // 不动托管 Data，故帧持有期间数组安全；免去一次 ToArray 分配+拷贝（热路径性能）。
        var frame = new AudioFrame(
            packet.Data,
            packet.SampleRate,
            packet.Channels,
            packet.Format,
            packet.Timestamp,
            packet.Duration,
            frameCount);

        return new ValueTask<AudioFrame?>(frame);
    }

    /// <inheritdoc/>
    /// <remarks>热路径：直通解码器无缓冲帧，返回 null。</remarks>
    public ValueTask<AudioFrame?> FlushAsync()
    {
        return new ValueTask<AudioFrame?>((AudioFrame?)null);
    }

    /// <inheritdoc/>
    /// <remarks>直通解码器无状态需重置。</remarks>
    public void Reset()
    {
        // 无操作
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：无异步资源，委托 Dispose + CompletedTask。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

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
/// <para><b>格式来源</b>（2026-07-31 修复）：本类为<b>直通</b>实现——真正的解码在
/// <c>MFDemuxer</c> 侧由 SourceReader（协商 PCM 输出类型后自动加载解码 MFT）完成。
/// 故输出格式无从自知，必须由 MediaPlayer 经 <see cref="IAudioSourceFormatAware"/> 注入实测值。
/// 早期硬编码 44100Hz/2ch 会在 48kHz/单声道媒体上导致音高与节奏错乱。</para>
/// <para><b>关闭安全性</b>：本类为<b>直通</b>实现，<b>不持有任何原生 MFT / COM 指针</b>（无 IMFTransform 实例，
/// 亦无 Marshal.Release）——所有原生资源都在 MFDemuxer 与 MFVideoDecoder 侧。因此本类无需接入
/// <c>NativeCallGate</c> 两阶段关闭协议（MF 冷启动 0x80131506 修复）；其 Dispose 仅释放托管状态。</para>
/// </remarks>
internal sealed class MFAudioDecoder : IAudioDecoder, IAudioSourceFormatAware
{
    private readonly ILogger<MFAudioDecoder> _logger;
    private bool _disposed;
    private bool _sourceFormatApplied;
    private AudioSettings? _settings;
    private SampleFormat _sampleFormat = SampleFormat.S16;

    public MFAudioDecoder(ILogger<MFAudioDecoder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public AudioCodec Codec { get; private set; }

    /// <summary>
    /// SourceReader 实际输出的 PCM 采样率。
    /// </summary>
    /// <remarks>
    /// 默认 44100 仅为「未注入实测格式」时的兜底值；正常路径由
    /// <see cref="SetSourceFormat"/> 覆盖为解封装层实测值。
    /// </remarks>
    public int OutputSampleRate { get; private set; } = 44100;

    /// <inheritdoc cref="OutputSampleRate"/>
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
        _settings = settings;
        _logger.LogDebug("MF 音频解码器初始化: {Codec}", codec);
    }

    /// <inheritdoc/>
    public void SetSourceFormat(int sampleRate, int channels, SampleFormat sampleFormat)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        OutputSampleRate = sampleRate;
        OutputChannels = channels;
        _sampleFormat = sampleFormat;
        _sourceFormatApplied = true;

        // MF 直通路径无自有重采样器：AudioSettings 中显式指定的目标格式无法在此满足，
        // 静默忽略会让调用方误以为已生效，故显式告警（如需转换应在 SourceReader 协商阶段指定）。
        if (_settings is { } s)
        {
            if (s.OutputSampleRate is { } wantRate && wantRate != sampleRate)
                _logger.LogWarning("AudioSettings 请求 {Want}Hz，但 MF 直通解码器不重采样，实际输出 {Actual}Hz", wantRate, sampleRate);
            if (s.OutputChannels is { } wantCh && wantCh != channels)
                _logger.LogWarning("AudioSettings 请求 {Want} 声道，但 MF 直通解码器不做声道混音，实际输出 {Actual} 声道", wantCh, channels);
            if (s.OutputSampleFormat is { } wantFmt && wantFmt != sampleFormat)
                _logger.LogWarning("AudioSettings 请求 {Want} 采样格式，但 MF 直通解码器不转换，实际输出 {Actual}", wantFmt, sampleFormat);
        }

        _logger.LogDebug("MF 音频解码器输出格式: {Rate}Hz {Ch}ch {Fmt}", sampleRate, channels, sampleFormat);
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 热路径：MFDemuxer 已在打开阶段把音频流协商为 PCM 输出（SetCurrentMediaType），
    /// SourceReader 内部完成解码，此处为直通处理（将 packet PCM 数据包装为 AudioFrame）。
    /// </remarks>
    public ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.Data.Length == 0)
            return new ValueTask<AudioFrame?>((AudioFrame?)null);

        if (!_sourceFormatApplied)
        {
            // 一次性告警：未注入实测格式意味着正在用兜底的 44100/2 解释字节，
            // 与真实媒体不符时表现为音高/节奏错乱——静默会极难排查。
            _sourceFormatApplied = true;
            _logger.LogWarning("MF 音频解码器未收到实测输出格式（IAudioSourceFormatAware 未被调用），" +
                "回落 {Rate}Hz/{Ch}ch/S16；若与媒体实际格式不符将出现音高异常。", OutputSampleRate, OutputChannels);
        }

        // 每帧（所有声道一组样本）字节数 = 单样本字节数 × 声道数
        int bytesPerFrame = BytesPerSample(_sampleFormat) * OutputChannels;
        int frameCount = packet.Data.Length / bytesPerFrame;

        if (frameCount <= 0)
            return new ValueTask<AudioFrame?>((AudioFrame?)null);

        var frame = new AudioFrame(
            packet.Data.ToArray().AsMemory(),
            OutputSampleRate,
            OutputChannels,
            _sampleFormat,
            packet.Timestamp,
            packet.Duration,
            frameCount);

        return new ValueTask<AudioFrame?>(frame);
    }

    /// <summary>单个采样（单声道）的字节数。</summary>
    private static int BytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.S16 => 2,
        SampleFormat.S32 => 4,
        SampleFormat.F32 => 4,
        _ => 2
    };

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

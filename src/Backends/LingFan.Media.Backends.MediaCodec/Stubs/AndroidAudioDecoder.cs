namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// <see cref="AndroidAudioDecoder"/> 的 net10.0 可移植桩：非 Android 运行时注册到 DI，
/// <see cref="Initialize"/> 抛 <see cref="PlatformNotSupportedException"/>，由上层回退链到跨平台后端。
/// 与 MF 后端在非 Windows 运行时同构。
/// </summary>
/// <remarks>仅 net10.0 目标编译（见 csproj Stubs/ 排除；real 实现走 net10.0-android 的托管 Android.Media）。</remarks>
internal sealed class AndroidAudioDecoder : IAudioDecoder
{
    private readonly AndroidBackend _backend;
    private readonly ILogger<AndroidAudioDecoder> _logger;

    public AndroidAudioDecoder(AndroidBackend backend, ILogger<AndroidAudioDecoder> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public AudioCodec Codec => AudioCodec.Unknown;

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <inheritdoc/>
    public int OutputSampleRate => 0;

    /// <inheritdoc/>
    public int OutputChannels => 0;

    /// <inheritdoc/>
    public void Initialize(AudioCodec codec, AudioSettings settings)
        => throw new PlatformNotSupportedException(
            "Android 音频解码器仅支持 Android 运行时。请使用 FFmpeg 作为跨平台后端。");

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
        => throw new PlatformNotSupportedException(
            "Android 音频解码器仅支持 Android 运行时。请使用 FFmpeg 作为跨平台后端。");

    /// <inheritdoc/>
    public ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet)
        => throw new PlatformNotSupportedException(
            "Android 音频解码器仅支持 Android 运行时。请使用 FFmpeg 作为跨平台后端。");

    /// <inheritdoc/>
    public ValueTask<AudioFrame?> FlushAsync()
        => throw new PlatformNotSupportedException(
            "Android 音频解码器仅支持 Android 运行时。请使用 FFmpeg 作为跨平台后端。");

    /// <inheritdoc/>
    public void Reset() { }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public void Dispose() { }
}
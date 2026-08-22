namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// <see cref="AndroidVideoDecoder"/> 的 net10.0 可移植桩：非 Android 运行时经此类型注册到 DI，
/// 但实际解码不会发生——<see cref="Initialize"/> 抛 <see cref="PlatformNotSupportedException"/>，
/// 由上层按「后端回退链」来到 FFmpeg/VLC 等跨平台后端。与 MF 后端在非 Windows 运行时同构。
/// </summary>
/// <remarks>仅 net10.0 目标编译（见 csproj Stubs/ 排除；real 实现走 net10.0-android 的托管 Android.Media）。</remarks>
internal sealed class AndroidVideoDecoder : IVideoDecoder
{
    private readonly AndroidBackend _backend;
    private readonly ILogger<AndroidVideoDecoder> _logger;

    public AndroidVideoDecoder(AndroidBackend backend, ILogger<AndroidVideoDecoder> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public VideoCodec Codec => VideoCodec.Unknown;

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <inheritdoc/>
    public void Initialize(VideoCodec codec, VideoSettings settings)
        => throw new PlatformNotSupportedException(
            "Android 视频解码器仅支持 Android 运行时。请使用 FFmpeg / VLC 作为跨平台后端。");

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
        => throw new PlatformNotSupportedException(
            "Android 视频解码器仅支持 Android 运行时。请使用 FFmpeg / VLC 作为跨平台后端。");

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
        => throw new PlatformNotSupportedException(
            "Android 视频解码器仅支持 Android 运行时。请使用 FFmpeg / VLC 作为跨平台后端。");

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> FlushAsync()
        => throw new PlatformNotSupportedException(
            "Android 视频解码器仅支持 Android 运行时。请使用 FFmpeg / VLC 作为跨平台后端。");

    /// <inheritdoc/>
    public void Reset() { }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public void Dispose() { }
}
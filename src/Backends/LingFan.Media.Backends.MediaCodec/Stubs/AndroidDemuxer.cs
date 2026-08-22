namespace LingFan.Media.Backends.MediaCodec.Demuxer;

/// <summary>
/// <see cref="AndroidDemuxer"/> 的 net10.0 可移植桩：非 Android 运行时注册到 DI，
/// <see cref="OpenAsync"/> 抛 <see cref="PlatformNotSupportedException"/>，由上层回退链到跨平台 demuxer。
/// 与 MF 后端在非 Windows 运行时同构。
/// </summary>
/// <remarks>仅 net10.0 目标编译（见 csproj Stubs/ 排除；real 实现走 net10.0-android 的托管 Android.Media）。</remarks>
internal sealed class AndroidDemuxer : IMediaDemuxer
{
    private readonly AndroidBackend _backend;
    private readonly ILogger<AndroidDemuxer> _logger;

    public AndroidDemuxer(AndroidBackend backend, ILogger<AndroidDemuxer> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public IReadOnlyList<MediaTrack> Tracks => Array.Empty<MediaTrack>();

    /// <inheritdoc/>
    public MediaMetadata Metadata { get; } = new();

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task OpenAsync(IMediaStream stream, CancellationToken ct = default)
        => throw new PlatformNotSupportedException(
            "Android 解封装器仅支持 Android 运行时。请使用 FFmpeg 作为跨平台后端。");

    /// <inheritdoc/>
    public ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default)
        => throw new PlatformNotSupportedException(
            "Android 解封装器仅支持 Android 运行时。请使用 FFmpeg 作为跨平台后端。");

    /// <inheritdoc/>
    public Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default) => Task.FromResult(false);

    /// <inheritdoc/>
    public void Close() { }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public void Dispose() { }
}
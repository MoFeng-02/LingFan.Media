using Android.Media;
using Java.IO;
// Android.Media 亦含 MediaMetadata，与 Abstractions 全局冲突 → 别名锁定契约层类型。
using MediaMetadata = LingFan.Media.Abstractions.MediaMetadata;

namespace LingFan.Media.Backends.MediaCodec.Demuxer;

/// <summary>
/// 基于托管 <see cref="MediaExtractor"/> 的 <see cref="IMediaDemuxer"/> 实现（net-android 内置，非手写 P/Invoke）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>（与 MFBackend 对称）：</para>
/// <list type="bullet">
/// <item><see cref="OpenAsync"/>：混合——条件式 <c>await stream.ConnectAsync</c>（仅无地址流需预建连；文件/URL 由系统读取器按地址打开，流不被读取）+
/// <c>await Task.Run(OpenCore)</c>（伪异步：托管同步调用卸载线程池，与 MF 后端同构）。</item>
/// <item><see cref="ReadPacketAsync"/> / <see cref="SeekAsync"/>：伪异步——<c>await Task.Run</c> 卸载同步调用。</item>
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <c>Task.CompletedTask</c>。</item>
/// <item><see cref="Close"/> / <see cref="Dispose"/> / <see cref="DisposeAsync"/>：同步释放。</item>
/// </list>
/// <para><b>仅 Android 可用</b>：非 Android 运行时 <see cref="OpenAsync"/> 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para><b>数据源选择</b>：</para>
/// <list type="bullet">
/// <item>本地文件 → <see cref="MediaExtractor.SetDataSource(Java.IO.FileDescriptor, long, long)"/>（<c>FileInputStream.FD</c>），
/// 经 fd 直读，规避路径/URI 解析差异（部分 ROM 对裸路径返回 MALFORMED）。</item>
/// <item>http(s) URL → <c>MediaExtractor.SetDataSource(url, headers)</c>（托管原生网络读取；随附建议打开头）。</item>
/// <item>无地址流 → <see cref="AndroidManagedDataSource"/> 桥接 <see cref="IMediaStream"/>（API 23+，走 Java 绑定规避 CFI SIGTRAP）。</item>
/// </list>
/// <para><b>多轨交织</b>：选中全部轨道后，extractor 按 PTS 自动交错返回各轨采样，<see cref="ReadPacketAsync"/> 直接透传，
/// 调用方按 <see cref="MediaPacket.TrackIndex"/> 路由至对应解码器。</para>
/// <para><b>分部划分</b>：Tracks 分部管轨道/元数据解析与 MediaFormat 键读取；Read 分部管采样读取核心与复用缓冲。
/// 本文件为数据源打开、契约入口与生命周期。</para>
/// </remarks>
internal sealed partial class AndroidDemuxer : IMediaDemuxer
{
    private readonly AndroidBackend _backend;
    private readonly ILogger<AndroidDemuxer> _logger;

    private MediaExtractor? _extractor;
    private MediaDataSource? _dataSource;   // 仅无地址流路径使用；文件/URL 为 null
    private IMediaStream? _stream;

    private bool _opened;
    private bool _disposed;

    private IReadOnlyList<MediaTrack> _tracks = Array.Empty<MediaTrack>();
    private MediaMetadata _metadata = new();

    /// <summary>初始化 Android 解封装器的新实例。</summary>
    public AndroidDemuxer(AndroidBackend backend, ILogger<AndroidDemuxer> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <inheritdoc/>
    public MediaMetadata Metadata => _metadata;

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask; // 接口契约：无 I/O
    }

    /// <inheritdoc/>
    public async Task OpenAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_opened)
            throw new InvalidOperationException("AndroidDemuxer 实例已打开，不可重复打开；请新建实例。");

        if (!OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException(
                "Android 解封装器仅支持 Android 运行时。请使用 FFmpeg 作为跨平台后端。");

        ct.ThrowIfCancellationRequested();
        _stream = stream;

        // 网络流预建连：仅无地址流（MediaDataSource 桥内同步读流）需要；
        // 文件/URL 由系统读取器按地址原生打开，流本身不被读取（预建连只会白建一条连接）。
        if (string.IsNullOrEmpty(stream.Location))
            await stream.ConnectAsync(ct).ConfigureAwait(false);

        // 伪异步：托管同步调用卸载线程池（与 MFBackend 同构）
        try
        {
            await Task.Run(() => OpenCore(ct), ct).ConfigureAwait(false);
            _opened = true;
        }
        catch
        {
            CloseSync();
            throw;
        }

        _logger.LogInformation("[ANDROID-DEMUX] 打开成功: {Count} 条轨道, 时长 {Duration}",
            _tracks.Count, _metadata.Duration);
    }

    /// <summary>OpenAsync 的同步核心逻辑（Task.Run 线程上执行，伪异步）。</summary>
    private void OpenCore(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var extractor = new MediaExtractor();
        try
        {
            var location = _stream!.Location;
            if (!string.IsNullOrEmpty(location) && !LooksLikeUrl(location))
            {
                // 本地文件（托管 SetDataSource(FileDescriptor,long,long)）：
                // 经 fd 直读，绕开 NDK setDataSource 的路径/URI 解析差异（部分 ROM 对裸路径返回
                // MALFORMED + 误导性 "can't create http service" 日志）。extractor 内部 dup fd，
                // 本方法执行完关闭文件无冲突。
                var fi = new FileInfo(location);
                using var fis = new FileInputStream(location!);
                extractor.SetDataSource(fis.FD!, 0, fi.Length);
            }
            else if (!string.IsNullOrEmpty(location))
            {
                // http(s) URL：托管原生网络读取（native 到 native，无 CFI 问题）。
                // 透传建议打开头（默认 User-Agent/用户自定义头/Cookie）：地址式打开若丢头，
                // 防盗链源（Referer/UA/Cookie 校验）会直接 403。无头时用无参重载。
                var openHeaders = _stream!.SuggestedOpenHeaders;
                if (openHeaders is { Count: > 0 })
                {
                    extractor.SetDataSource(location!, openHeaders.ToDictionary(kv => kv.Key, kv => kv.Value));
                }
                else
                {
                    extractor.SetDataSource(location!);
                }
            }
            else
            {
                // 无地址流（内存/透传）：托管 MediaDataSource 桥接 IMediaStream（API 23+，走 Java 绑定）。
                // SetDataSource(MediaDataSource) 为 API 23+：低版本无法桥接，诚实失败（文件/URL 仍可用）。
                if (!OperatingSystem.IsAndroidVersionAtLeast(23))
                    throw new PlatformNotSupportedException(
                        "当前 Android API < 23，MediaDataSource 不可用，无法桥接无地址流；请使用文件/URL 源。");
                _dataSource = new AndroidManagedDataSource(_stream);
                extractor.SetDataSource(_dataSource);
            }

            // 容器级格式 → 元数据 + 轨道解析（一次性遍历各轨 MediaFormat）
            (_tracks, _metadata) = ParseTracksAndMetadata(extractor);

            // 选中全部轨道（按 PTS 自动交错返回各轨采样）
            for (int i = 0; i < extractor.TrackCount; i++)
                extractor.SelectTrack(i);

            _extractor = extractor;
        }
        catch
        {
            // 任一阶段失败：释放已分配资源后向上传播（避免 extractor/_dataSource 泄漏）
            extractor.Release();
            _dataSource?.Dispose();
            _dataSource = null;
            throw;
        }
    }

    /// <summary>判定 location 是否带 URL scheme（http(s)://、content:// 等）；本地路径无 scheme。</summary>
    private static bool LooksLikeUrl(string location)
        => location.Contains("://", StringComparison.Ordinal);

    /// <inheritdoc/>
    public async ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _extractor is null)
            throw new InvalidOperationException("解封装器未打开，请先调用 OpenAsync。");
        ct.ThrowIfCancellationRequested();

        // 伪异步：托管同步读取卸载线程池
        return await Task.Run(() => ReadPacketCore(), ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _extractor is null) return false;
        ct.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            long us = position.Ticks / 10; // ticks → 微秒
            _extractor!.SeekTo(us, MediaExtractorSeekTo.ClosestSync);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Close() => CloseSync();

    private void CloseSync()
    {
        _opened = false;
        _extractor?.Release();
        _extractor = null;
        _dataSource?.Dispose();
        _dataSource = null;
        _stream = null;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        CloseSync();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseSync();
    }
}
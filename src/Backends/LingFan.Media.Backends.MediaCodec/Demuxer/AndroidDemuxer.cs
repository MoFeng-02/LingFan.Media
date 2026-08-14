using System.Runtime.Versioning;
using LingFan.Media.Backends.MediaCodec.Wrappers;

namespace LingFan.Media.Backends.MediaCodec.Demuxer;

/// <summary>
/// 基于 Android NDK <c>AMediaExtractor</c> 的 <see cref="IMediaDemuxer"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>（与 MFBackend 对称）：</para>
/// <list type="bullet">
/// <item><see cref="OpenAsync"/>：混合——<c>await stream.ConnectAsync</c>（真异步 I/O）+
/// <c>await Task.Run(OpenCore)</c>（<b>伪异步</b>：NDK 同步原生调用卸载线程池，与 MF 后端同构，已获用户认可）。</item>
/// <item><see cref="ReadPacketAsync"/> / <see cref="SeekAsync"/>：<b>伪异步</b>——<c>await Task.Run</c> 卸载 NDK 同步调用。</item>
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <c>Task.CompletedTask</c>。</item>
/// <item><see cref="Close"/> / <see cref="Dispose"/> / <see cref="DisposeAsync"/>：同步原生释放。</item>
/// </list>
/// <para><b>仅 Android 可用</b>：非 Android 运行时 OpenAsync 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para><b>数据源选择</b>：</para>
/// <list type="bullet">
/// <item>流有 <c>Location</c>（文件/URL/content URI）→ <c>AMediaExtractor_setDataSource</c>（API 21+），NDK 原生读取。</item>
/// <item>流无 Location（内存/透传）→ <c>AMediaDataSource</c> 桥接 <see cref="IMediaStream"/>（API 28+）；
/// 低版本运行时该符号缺失，捕获 <see cref="EntryPointNotFoundException"/> 后降级为 <see cref="PlatformNotSupportedException"/>。</item>
/// </list>
/// <para><b>多轨交织</b>：选中全部轨道后，extractor 按 PTS 自动交错返回各轨采样，<see cref="ReadPacketAsync"/> 直接透传，
/// 调用方按 <c>SampleTrackIndex</c> 路由至对应解码器。</para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2050",
    Justification = "无 [ComImport]，使用原始 [LibraryImport] P/Invoke，不会被裁剪器移除。仅 Android 运行时使用。")]
internal sealed class AndroidDemuxer : IMediaDemuxer
{
    private readonly AndroidBackend _backend;
    private readonly ILogger<AndroidDemuxer> _logger;

    private AndroidMediaExtractor? _extractor;
    private AndroidDataSource? _dataSource;   // 仅无地址流路径使用；URL 路径为 null
    private IMediaStream? _stream;

    private bool _opened;
    private bool _disposed;

    private IReadOnlyList<MediaTrack> _tracks = Array.Empty<MediaTrack>();
    private MediaMetadata _metadata = new();

    // 解封装热路径复用缓冲（避免每包分配）；_maxInputSize 在 Open 时按轨道 max-input-size 上调。
    // 注意：AMediaExtractor_getSampleSize 是 API 28+，为兼容 API 21+ 文件路径，热路径不使用它，
    // 改为可增长读取缓冲（readSampleData 不推进游标，容量不足可安全重读）。
    private byte[] _readScratch = Array.Empty<byte>();
    private int _maxInputSize = 1 << 16; // 64 KiB 下限

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

        // 真异步：网络流预建连（文件/透传流为无操作）
        await stream.ConnectAsync(ct).ConfigureAwait(false);

        // 伪异步：NDK 同步原生调用卸载线程池（与 MFBackend 同构）
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

        _extractor = new AndroidMediaExtractor();
        try
        {
            var location = _stream!.Location;
            if (!string.IsNullOrEmpty(location))
            {
                // API 21+：NDK 按地址原生打开（文件路径 / http(s) URL / content URI）
                _extractor.SetDataSource(location);
            }
            else
            {
                // 无地址流：API 28+ 自定义数据源桥接 IMediaStream；
                // 低版本运行时符号缺失 → EntryPointNotFoundException，捕获降级
                try
                {
                    _dataSource = new AndroidDataSource(_stream);
                    _extractor.SetDataSource(_dataSource);
                }
                catch (EntryPointNotFoundException ex)
                {
                    throw new PlatformNotSupportedException(
                        "当前 Android API < 28，AMediaDataSource 不可用，无法桥接非地址流；请使用文件/URL 源。", ex);
                }
            }

            // 容器级格式 → 元数据
            var fileFmt = _extractor.GetFileFormat();
            try
            {
                _metadata = BuildMetadata(fileFmt);
            }
            finally
            {
                fileFmt.Dispose();
            }

            // 轨道解析
            _tracks = ParseTracks();

            // 选中全部轨道（extractor 按 PTS 自动交错返回各轨采样）
            for (nuint i = 0; i < _extractor.TrackCount; i++)
                _extractor.SelectTrack(i);
        }
        catch
        {
            // 任一阶段失败：释放已分配原生资源后向上传播（避免 _extractor 泄漏）
            _extractor.Dispose();
            _extractor = null;
            _dataSource?.Dispose();
            _dataSource = null;
            throw;
        }
    }

    private MediaMetadata BuildMetadata(AndroidMediaFormat fileFmt)
    {
        TimeSpan duration = TimeSpan.Zero;
        if (fileFmt.TryGetInt64(AndroidMediaConstants.KEY_DURATION_US, out long durUs) && durUs > 0)
            duration = TimeSpan.FromTicks(durUs * 10); // 微秒 → ticks

        ContainerFormat container = ContainerFormat.Unknown;
        string? mime = fileFmt.GetString(AndroidMediaConstants.KEY_MIME);
        if (mime is not null)
            container = AndroidCodecMaps.MimeToContainerFormat(mime);

        return new MediaMetadata
        {
            Duration = duration,
            ContainerFormat = container,
        };
    }

    private IReadOnlyList<MediaTrack> ParseTracks()
    {
        var list = new List<MediaTrack>();
        nuint count = _extractor!.TrackCount;
        for (nuint i = 0; i < count; i++)
        {
            var fmt = _extractor.GetTrackFormat(i);
            try
            {
                string? mime = fmt.GetString(AndroidMediaConstants.KEY_MIME);
                if (string.IsNullOrEmpty(mime)) continue;
                var type = AndroidCodecMaps.MimeToTrackType(mime);
                var track = BuildTrack((int)i, type, mime, fmt);
                if (track is not null) list.Add(track);
            }
            finally
            {
                fmt.Dispose();
            }
        }
        return list;
    }

    private MediaTrack? BuildTrack(int idx, TrackType type, string mime, AndroidMediaFormat fmt)
    {
        // 累计轨道 max-input-size，供解封装读取缓冲定容（避免逐包分配巨大缓冲）
        if (fmt.TryGetInt32(AndroidMediaConstants.KEY_MAX_INPUT_SIZE, out int mis) && mis > _maxInputSize)
            _maxInputSize = mis;

        VideoTrackInfo? vinfo = null;
        AudioTrackInfo? ainfo = null;

        switch (type)
        {
            case TrackType.Video:
            {
                fmt.TryGetInt32(AndroidMediaConstants.KEY_WIDTH, out int w);
                fmt.TryGetInt32(AndroidMediaConstants.KEY_HEIGHT, out int h);
                fmt.TryGetInt32(AndroidMediaConstants.KEY_FRAME_RATE, out int fps);
                fmt.TryGetInt64(AndroidMediaConstants.KEY_DURATION_US, out long durUs);
                fmt.TryGetInt32(AndroidMediaConstants.KEY_ROTATION, out _); // 诊断用，当前未消费
                var csd0 = fmt.GetBuffer(AndroidMediaConstants.KEY_CSD_0);
                var v = new VideoTrackInfo
                {
                    Width = w,
                    Height = h,
                    FrameRate = fps,
                    // 解码器实际输出格式在 Initialize 时由输出媒体类型定，此处仅占位
                    PixelFormat = PixelFormat.YUV420P,
                    Duration = durUs > 0 ? TimeSpan.FromTicks(durUs * 10) : TimeSpan.Zero,
                    CodecConfiguration = csd0, // 可读写属性，构造后回填亦可；此处一并赋值
                };
                vinfo = v;
                break;
            }

            case TrackType.Audio:
            {
                fmt.TryGetInt32(AndroidMediaConstants.KEY_SAMPLE_RATE, out int sr);
                fmt.TryGetInt32(AndroidMediaConstants.KEY_CHANNEL_COUNT, out int ch);
                fmt.TryGetInt32(AndroidMediaConstants.KEY_PCM_ENCODING, out int enc);
                fmt.TryGetInt64(AndroidMediaConstants.KEY_DURATION_US, out long adurUs);
                // csd-0（如 AAC AudioSpecificConfig）为解码器必需要件；CodecConfiguration 是 init 属性，
                // 必须在初始化器内赋值（构造后赋值会触发 CS8852）。
                var acsd0 = fmt.GetBuffer(AndroidMediaConstants.KEY_CSD_0);
                ainfo = new AudioTrackInfo
                {
                    SampleRate = sr,
                    Channels = ch,
                    BitsPerSample = enc == AndroidMediaConstants.ENCODING_PCM_FLOAT ? 32
                        : enc == AndroidMediaConstants.ENCODING_PCM_16BIT ? 16 : 0,
                    Duration = adurUs > 0 ? TimeSpan.FromTicks(adurUs * 10) : TimeSpan.Zero,
                    CodecConfiguration = acsd0, // byte[]? → ReadOnlyMemory<byte>（null 经隐式转换映射 Empty）
                };
                break;
            }

            case TrackType.Subtitle:
                // 当前后端无字幕解码器；仅列举轨道，不填充详情
                break;
        }

        var lang = fmt.GetString(AndroidMediaConstants.KEY_LANGUAGE);

        return new MediaTrack
        {
            Index = idx,
            Type = type,
            VideoCodec = type == TrackType.Video ? AndroidCodecMaps.MimeToVideoCodec(mime) : null,
            AudioCodec = type == TrackType.Audio ? AndroidCodecMaps.MimeToAudioCodec(mime) : null,
            VideoInfo = vinfo,
            AudioInfo = ainfo,
            Language = string.IsNullOrEmpty(lang) ? null : lang,
        };
    }

    /// <inheritdoc/>
    public async ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _extractor is null)
            throw new InvalidOperationException("解封装器未打开，请先调用 OpenAsync。");
        ct.ThrowIfCancellationRequested();

        // 伪异步：NDK 同步读取卸载线程池
        return await Task.Run(() => ReadPacketCore(), ct).ConfigureAwait(false);
    }

    /// <summary>ReadPacketAsync 的同步核心：读取当前采样（按 PTS 已自动交错），推进后返回。</summary>
    private MediaPacket? ReadPacketCore()
    {
        var ext = _extractor!;
        while (true)
        {
            // EOF 判定：当前无采样。
            // 注：NDK getSampleFlags 在流尾（getSampleMeta 失败）按 AOSP 实现返回 -1（即 0xFFFFFFFF），
            // 与 SAMPLE_FLAG_* 重叠且不可靠（任何错误都返回 -1）；故判尾以 SampleTrackIndex < 0
            // （AOSP 文档专门在 EOF 返回 -1）或 readSampleData < 0 为权威依据，不依赖 flags。
            int trackIdx = ext.SampleTrackIndex;
            if (trackIdx < 0) return null;

            uint flags = ext.SampleFlags;
            long ptsUs = ext.SampleTimeUs;

            byte[]? data = ReadCurrentSample(ext);
            if (data is null) return null; // 无更多采样（冗余 EOF 保护）

            // 推进到下一采样（必须在 advance 前完成当前采样读取）
            ext.Advance();

            if (data.Length == 0) continue; // 空采样（非 EOF）：跳过，不产出空包

            bool key = (flags & AndroidMediaConstants.AMEDIAEXTRACTOR_SAMPLE_FLAG_SYNC) != 0;
            var ts = ptsUs >= 0 ? TimeSpan.FromTicks(ptsUs * 10) : TimeSpan.Zero;
            var pkt = new MediaPacket(trackIdx, data, ts, TimeSpan.Zero, key);

            // 不支持 DRM：跳过加密采样，不假装解码
            if ((flags & AndroidMediaConstants.AMEDIAEXTRACTOR_SAMPLE_FLAG_ENCRYPTED) != 0)
            {
                _logger.LogDebug("[ANDROID-DEMUX] 跳过加密采样 track={Track}", trackIdx);
                pkt.Dispose();
                continue;
            }

            return pkt;
        }
    }

    /// <summary>
    /// 读取当前采样到托管数组（owned by 返回的 packet）。
    /// 复用 <see langword="_readScratch"/> 缓冲；<c>readSampleData</c> 不推进游标，故容量不足时增长后安全重读。
    /// 返回 <see langword="null"/> 表示无更多采样（EOF）。
    /// </summary>
    private byte[]? ReadCurrentSample(AndroidMediaExtractor ext)
    {
        int init = Math.Min(_maxInputSize, 64 << 20); // 上限 64 MiB，防失控分配
        if (_readScratch.Length < init)
            _readScratch = new byte[init];

        while (true)
        {
            int n = ext.ReadSampleData(_readScratch);
            if (n < 0) return null;                  // 无更多采样（EOF）
            if (n < _readScratch.Length)            // 完整读入（含 n==0 空采样）
            {
                var copy = new byte[n];
                if (n > 0) Buffer.BlockCopy(_readScratch, 0, copy, 0, n);
                return copy;
            }
            // 缓冲区填满：采样可能更大，增长后重读（readSampleData 不推进游标）
            int next = Math.Min((int)(_readScratch.Length * 1.5), 64 << 20);
            if (next <= _readScratch.Length) return _readScratch; // 已达 64MiB 上限：返回已读部分（极端兜底）
            _readScratch = new byte[next];
        }
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
            _extractor!.SeekTo(us, AndroidMediaConstants.AMEDIAEXTRACTOR_SEEK_CLOSEST_SYNC);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Close() => CloseSync();

    private void CloseSync()
    {
        _opened = false;
        _extractor?.Dispose();
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

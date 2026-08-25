using Android.Media;
using Java.IO;
using Java.Nio;
using System.Buffers;
// Android.Media 亦含 MediaMetadata，与 Abstractions 全局冲突 → 别名锁定契约层类型。
using MediaMetadata = LingFan.Media.Abstractions.MediaMetadata;

namespace LingFan.Media.Backends.MediaCodec.Demuxer;

/// <summary>
/// 基于托管 <see cref="MediaExtractor"/> 的 <see cref="IMediaDemuxer"/> 实现（net-android 内置，非手写 P/Invoke）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>（与 MFBackend 对称）：</para>
/// <list type="bullet">
/// <item><see cref="OpenAsync"/>：混合——<c>await stream.ConnectAsync</c>（真异步 I/O）+
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
/// <item>http(s) URL → <see cref="MediaExtractor.SetDataSource(string)"/>（托管原生网络读取）。</item>
/// <item>无地址流 → <see cref="AndroidManagedDataSource"/> 桥接 <see cref="IMediaStream"/>（API 23+，走 Java 绑定规避 CFI SIGTRAP）。</item>
/// </list>
/// <para><b>多轨交织</b>：选中全部轨道后，extractor 按 PTS 自动交错返回各轨采样，<see cref="ReadPacketAsync"/> 直接透传，
/// 调用方按 <see cref="MediaPacket.TrackIndex"/> 路由至对应解码器。</para>
/// </remarks>
internal sealed class AndroidDemuxer : IMediaDemuxer
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

    // 解封装热路径复用缓冲（避免每包分配）；_maxInputSize 在 Open 时按轨道 max-input-size 上调。
    // 复用经 Java.Nio.ByteBuffer.Wrap 包装的视图；缓冲容量不足时增长并重 Wrap。
    private byte[] _readScratch = Array.Empty<byte>();
    private ByteBuffer? _readBuf;
    private int _maxInputSize = 1 << 16; // 64 KiB 下限

    // 诊断节流：每 64 包打一条读包日志
    private int _packetCounter;
    private const int PacketLogInterval = 64;

    // AOSP MediaExtractor SampleFlags 位：1=SYNC(关键帧)、2=ENCRYPTED（不支持 DRM 即跳过）。
    private const int SampleFlagSync = 1;
    private const int SampleFlagEncrypted = 2;

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
                extractor.SetDataSource(location!);
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

    private (IReadOnlyList<MediaTrack> Tracks, MediaMetadata Metadata) ParseTracksAndMetadata(MediaExtractor extractor)
    {
        int count = extractor.TrackCount;
        var list = new List<MediaTrack>(count);
        long maxDurationUs = 0;
        string? containerMime = null;

        for (int i = 0; i < count; i++)
        {
            using var fmt = extractor.GetTrackFormat(i);

            // 容器格式优先读 AOSP 轨道格式上的 "file-format" 键（如 video/mp4、audio/mp4）。
            if (containerMime is null && fmt.ContainsKey("file-format"))
                containerMime = fmt.GetString("file-format");
            if (fmt.ContainsKey(MediaFormat.KeyDuration))
            {
                long d = fmt.GetLong(MediaFormat.KeyDuration);
                if (d > maxDurationUs) maxDurationUs = d;
            }

            string? mime = fmt.GetString(MediaFormat.KeyMime);
            if (string.IsNullOrEmpty(mime)) continue;
            var type = AndroidCodecMaps.MimeToTrackType(mime);
            var track = BuildTrack((int)i, type, mime, fmt);
            if (track is not null) list.Add(track);
        }

        var metadata = new MediaMetadata
        {
            Duration = maxDurationUs > 0 ? TimeSpan.FromTicks(maxDurationUs * 10) : TimeSpan.Zero,
            ContainerFormat = containerMime is null
                ? ContainerFormat.Unknown
                : AndroidCodecMaps.MimeToContainerFormat(containerMime),
        };
        return (list, metadata);
    }

    private MediaTrack? BuildTrack(int idx, TrackType type, string mime, MediaFormat fmt)
    {
        // 累计轨道 max-input-size，供解封装读取缓冲定容
        if (GetInteger(fmt, MediaFormat.KeyMaxInputSize, out int mis) && mis > _maxInputSize)
            _maxInputSize = mis;

        VideoTrackInfo? vinfo = null;
        AudioTrackInfo? ainfo = null;

        switch (type)
        {
            case TrackType.Video:
            {
                GetInteger(fmt, MediaFormat.KeyWidth, out int w);
                GetInteger(fmt, MediaFormat.KeyHeight, out int h);
                GetInteger(fmt, MediaFormat.KeyFrameRate, out int fps);
                long durUs = GetLong(fmt, MediaFormat.KeyDuration);
                var csd0 = ReadCsd(fmt);
                vinfo = new VideoTrackInfo
                {
                    Width = w,
                    Height = h,
                    FrameRate = fps,
                    // 解码器实际输出格式在 Initialize 时由输出媒体类型定，此处仅占位
                    PixelFormat = PixelFormat.YUV420P,
                    Duration = durUs > 0 ? TimeSpan.FromTicks(durUs * 10) : TimeSpan.Zero,
                    CodecConfiguration = csd0, // csd-0（SPS+PPS 等），init 属性，此处一并赋值
                };
                break;
            }

            case TrackType.Audio:
            {
                GetInteger(fmt, MediaFormat.KeySampleRate, out int sr);
                GetInteger(fmt, MediaFormat.KeyChannelCount, out int ch);
                // KeyPcmEncoding 为 API 24+：低版本无此键，按默认 0（BitsPerSample 归 0）处理。
                int enc = 0;
                if (OperatingSystem.IsAndroidVersionAtLeast(24))
                    GetInteger(fmt, MediaFormat.KeyPcmEncoding, out enc);
                long adurUs = GetLong(fmt, MediaFormat.KeyDuration);
                var acsd0 = ReadCsd(fmt);
                ainfo = new AudioTrackInfo
                {
                    SampleRate = sr,
                    Channels = ch,
                    BitsPerSample = enc == (int)Encoding.PcmFloat ? 32
                        : enc == (int)Encoding.Pcm16bit ? 16 : 0,
                    Duration = adurUs > 0 ? TimeSpan.FromTicks(adurUs * 10) : TimeSpan.Zero,
                    CodecConfiguration = acsd0, // byte[]? → ReadOnlyMemory<byte>
                };
                break;
            }

            case TrackType.Subtitle:
                // 当前后端无字幕解码器；仅列举轨道，不填充详情
                break;
        }

        var lang = fmt.GetString(MediaFormat.KeyLanguage);

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

    /// <summary>读取整数键；不存在返回 false（MediaFormat.GetInteger 无键即抛，须先判 ContainsKey）。</summary>
    private static bool GetInteger(MediaFormat fmt, string key, out int value)
    {
        if (fmt.ContainsKey(key))
        {
            value = fmt.GetInteger(key);
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>读取长整型键（如 durationUs）；不存在返回 0。</summary>
    private static long GetLong(MediaFormat fmt, string key)
        => fmt.ContainsKey(key) ? fmt.GetLong(key) : 0;

    /// <summary>读取 csd-0/csd-1/…（拷贝为托管 byte[] 并依序拼接）；无则返回 null。键名 "csd-N"（AOSP <c>MediaFormat.KEY_CSD0/1/2</c>）。</summary>
    /// <remarks>
    /// H264 的 csd-0=SPS、csd-1=PPS（各自带 Annex-B 起始码）——<b>PPS 缺失时解码器无法解任何 slice</b>
    /// （表现为：queueInputBuffer 全成功、解码器持续释放输入缓冲，但 dequeue 恒 TRY_AGAIN、整流 0 帧产出），
    /// 故必须拼接完整参数集（多 NAL 同一 csd 缓冲合法且常见）。HEVC 的 csd-0 通常已含 VPS+SPS+PPS，
    /// 额外拼接无害。AAC 的 csd-0 单包完整，循环在 csd-1 处自然终止。
    /// </remarks>
    private static byte[]? ReadCsd(MediaFormat fmt)
    {
        byte[]? result = null;
        for (int i = 0; ; i++)
        {
            string key = $"csd-{i}";
            if (!fmt.ContainsKey(key)) break;
            var bb = fmt.GetByteBuffer(key);
            if (bb is null) break;
            int n = bb.Remaining();
            if (n <= 0) break;
            var chunk = new byte[n];
            bb.Get(chunk);
            if (result is null) { result = chunk; continue; }
            var merged = new byte[result.Length + chunk.Length];
            System.Buffer.BlockCopy(result, 0, merged, 0, result.Length);
            System.Buffer.BlockCopy(chunk, 0, merged, result.Length, chunk.Length);
            result = merged;
        }
        return result;
    }

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

    /// <summary>ReadPacketAsync 的同步核心：读取当前采样（按 PTS 已自动交错），推进后返回。</summary>
    private MediaPacket? ReadPacketCore()
    {
        var ext = _extractor!;
        while (true)
        {
            // EOF 判定：SampleTrackIndex < 0（AOSP 文档在 EOF 返回 -1），比 flags 可靠（flags 在流尾返回
            // 0xFFFFFFFF 与 SAMPLE_FLAG_* 重叠）。readSampleData <= 0 为冗余 EOF 保护。
            int trackIdx = ext.SampleTrackIndex;
            if (trackIdx < 0) return null;

            int flags = (int)ext.SampleFlags; // SampleFlags 为枚举，显式转 int 再按位判定
            long ptsUs = ext.SampleTime;

            EnsureScratchBuffered();
            _readBuf!.Clear();
            int n = ext.ReadSampleData(_readBuf, 0);
            if (n <= 0) return null; // 无更多采样（EOF）

            if (n >= _readScratch.Length)
            {
                // 缓冲恰好填满：采样可能更大，增长后重读（ReadSampleData 不推进游标）
                GrowScratch();
                EnsureScratchBuffered();
                _readBuf!.Clear();
                n = ext.ReadSampleData(_readBuf, 0);
                if (n <= 0) return null;
            }

            // 推进到下一采样（必须在当前采样读取完成后调用）
            ext.Advance();

            if (n == 0) continue; // 空采样（非 EOF）：跳过，不产出空包

            // 关键：ByteBuffer.Wrap(托管数组) 在 Java 侧是 marshal 副本——ReadSampleData 写入的是
            // Java 侧副本数组，绝不回写 _readScratch！必须用 Get()（Java→C# 方向）把字节取回。
            // （此前误用 Array.Copy(_readScratch,…) 拷出的是从未写入的托管数组 → 喂给解码器全零字节，
            //  导致 video/audio dequeue 恒 TRY_AGAIN、0 产出。）
            // 每包数据经 ArrayPool 租借（grow-only，消除每包 new 的 GC 压力）；packet Dispose 时由
            // RentedBufferOwner 归还池，避免跨包复用导致数据串号（Data 持有数组引用）。
            byte[] data = ArrayPool<byte>.Shared.Rent(n);
            _readBuf.Rewind(); // position=0（Position 在此绑定为方法组，用 Rewind 等价）
            _readBuf.Get(data, 0, n);

            bool key = (flags & SampleFlagSync) != 0;
            var ts = ptsUs >= 0 ? TimeSpan.FromTicks(ptsUs * 10) : TimeSpan.Zero;
            var pkt = new MediaPacket(trackIdx, data.AsMemory(0, n), ts, TimeSpan.Zero, key,
                dataOwner: new RentedBufferOwner(data));

            // 诊断节流日志：读包节奏（track/size/pts/key）；首包附前 12 字节 hex（验证取回方向正确、非全零）
            if ((_packetCounter++ % PacketLogInterval) == 0)
            {
                if (_packetCounter == 1)
                    _logger.LogInformation(
                        "[ANDROID-DEMUX] 读包 track={Track} size={Size} pts={PtsUs}us key={Key} 累计={Total} hex={Hex}",
                        trackIdx, n, ptsUs, key, _packetCounter,
                        Convert.ToHexString(data.AsSpan(0, Math.Min(12, n))));
                else
                    _logger.LogInformation(
                        "[ANDROID-DEMUX] 读包 track={Track} size={Size} pts={PtsUs}us key={Key} 累计={Total}",
                        trackIdx, n, ptsUs, key, _packetCounter);
            }

            // 不支持 DRM：跳过加密采样，不假装解码
            if ((flags & SampleFlagEncrypted) != 0)
            {
                _logger.LogDebug("[ANDROID-DEMUX] 跳过加密采样 track={Track}", trackIdx);
                pkt.Dispose();
                continue;
            }

            return pkt;
        }
    }

    /// <summary>确保读取缓冲已实例化并 Wrap 到位。</summary>
    private void EnsureScratchBuffered()
    {
        if (_readBuf is not null) return;
        if (_readScratch.Length < _maxInputSize)
            _readScratch = new byte[Math.Min(_maxInputSize, 64 << 20)]; // 上限 64 MiB
        _readBuf = ByteBuffer.Wrap(_readScratch);
    }

    /// <summary>读取缓冲增长（约 1.5x，上限 64 MiB）。</summary>
    private void GrowScratch()
    {
        int next = (int)Math.Min((long)_readScratch.Length * 3 / 2, 64L << 20);
        if (next <= _readScratch.Length) return; // 已达上限
        _readScratch = new byte[next];
        _readBuf = null; // 触发重 Wrap
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

    /// <summary>ArrayPool 租借缓冲的归还者：MediaPacket.Dispose 经 dataOwner 触发，把缓冲归还 Shared 池，
    /// 消除每包 new 的 GC 压力，同时因 Data 持有数组引用而避免跨包复用导致的数据串号。</summary>
    private sealed class RentedBufferOwner : IDisposable
    {
        private byte[]? _buffer;
        public RentedBufferOwner(byte[] buffer) => _buffer = buffer;
        public void Dispose()
        {
            var buf = _buffer;
            _buffer = null;
            if (buf is not null) ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
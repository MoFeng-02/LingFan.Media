using System.Runtime.InteropServices;
using LingFan.Media.Backends.FFmpeg.Interop;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Demuxer;

/// <summary>
/// 基于 FFmpeg libavformat 的 <see cref="IMediaDemuxer"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><c>OpenAsync</c> / <c>ReadPacketAsync</c> / <c>SeekAsync</c>：使用 <c>await Task.Run(...)</c>
/// 将同步 FFmpeg 调用卸载到线程池——FFmpeg C API 本质同步，<c>Task.Run</c> 含 <c>await</c> 满足真异步要求，
/// 避免阻塞调用线程（可能为 UI 线程）。</item>
/// <item><c>InitializeAsync</c>：接口契约，返回 <c>Task.CompletedTask</c>（无 I/O）。</item>
/// <item>AVIO <c>ReadPacketCallback</c>：同步边界（C 函数指针），调用 <see cref="IMediaStream.Read(Span{byte})"/>；
/// 网络建连已由 <see cref="OpenAsync"/> 在 <c>Task.Run</c> 前经 <see cref="IMediaStream.ConnectAsync"/> 异步完成。</item>
/// <item><c>Close</c> / <c>Dispose</c> / <c>DisposeAsync</c>：同步原生释放。</item>
/// </list>
/// <para><b>线程安全</b>：单线程使用（BufferManager 读取线程），非线程安全。</para>
/// <para><b>AOT 兼容</b>：sealed 类，SafeHandle 管理原生资源，无反射。</para>
/// </remarks>
internal sealed class FFmpegDemuxer : IMediaDemuxer
{
    private readonly ILogger<FFmpegDemuxer> _logger;

    // 原生资源
    private SafeAVFormatContextHandle? _formatContextHandle;
    private IntPtr _avioBuffer = IntPtr.Zero;
    private IntPtr _avioContext = IntPtr.Zero;

    // AVIO 回调委托（必须保持引用防止 GC 回收）
    // 用 object 存储避免 unsafe 字段声明（委托类型含指针参数需要 unsafe 上下文）
    private readonly object _readDelegate;
    private readonly object _seekDelegate;

    // 流引用（AVIO 回调使用）
    private IMediaStream? _stream;

    // 状态
    private bool _opened;
    private bool _disposed;
    private IReadOnlyList<MediaTrack> _tracks = Array.Empty<MediaTrack>();
    private MediaMetadata _metadata = new();

    /// <summary>AVIO 缓冲区大小（字节）。</summary>
    private const int AvioBufferSize = 32768;

    /// <summary>
    /// 初始化 <see cref="FFmpegDemuxer"/> 的新实例。
    /// </summary>
    /// <param name="stream">媒体数据流。</param>
    /// <param name="logger">日志器。</param>
    public unsafe FFmpegDemuxer(IMediaStream stream, ILogger<FFmpegDemuxer> logger)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _readDelegate = new avio_alloc_context_read_packet(ReadPacketCallback);
        _seekDelegate = new avio_alloc_context_seek(SeekCallback);
    }

    /// <inheritdoc/>
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <inheritdoc/>
    public MediaMetadata Metadata => _metadata;

    /// <inheritdoc/>
    /// <remarks>接口契约：无 I/O，返回 <see cref="Task.CompletedTask"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 真异步：先 <c>await stream.ConnectAsync</c> 在异步路径完成网络建连（消除同步 Read 内的硬阻塞），
    /// 再用 <c>await Task.Run</c> 卸载 avformat_open_input + avformat_find_stream_info 到线程池。
    /// FFmpeg AVIO 回调内部调用的 <see cref="IMediaStream.Read(Span{byte})"/> 此时已连接，仅做逐块同步读取。
    /// </remarks>
    public async Task OpenAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ct.ThrowIfCancellationRequested();
        _stream = stream;

        // 异步预建连（网络流在此完成 DNS/TLS 握手，不硬阻塞；文件/透传流为无操作）
        await stream.ConnectAsync(ct).ConfigureAwait(false);

        await Task.Run(() => OpenCore(stream, ct), ct).ConfigureAwait(false);
        _opened = true;
    }

    /// <summary>
    /// OpenAsync 的同步核心逻辑。在 Task.Run 线程上执行。
    /// </summary>
    /// <remarks>
    /// <para><b>资源安全</b>：avformat_open_input 失败时会自动释放 AVFormatContext 并将指针置 null。
    /// 因此 SafeHandle 必须在 avformat_open_input 成功后才创建，避免持有悬垂指针（use-after-free）。
    /// AVIO 资源因 AVFMT_FLAG_CUSTOM_IO 不被 avformat_free_context 释放，需手动清理。</para>
    /// </remarks>
    private unsafe void OpenCore(IMediaStream stream, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            // 1. 分配 AVIO 缓冲区和上下文
            _avioBuffer = Marshal.AllocHGlobal(AvioBufferSize);
            if (_avioBuffer == IntPtr.Zero)
                throw new InvalidOperationException("AllocHGlobal 失败：AVIO 缓冲区");

            // 委托实例已作为字段保持引用，回调通过实例方法访问 _stream
            AVIOContext* avioCtx = ffmpeg.avio_alloc_context(
                (byte*)_avioBuffer, AvioBufferSize,
                0, // write_flag = 0 (read-only)
                null, // opaque
                (avio_alloc_context_read_packet)_readDelegate,
                null, // write_packet (null = read-only)
                stream.CanSeek ? (avio_alloc_context_seek)_seekDelegate : null);

            if (avioCtx == null)
                throw new InvalidOperationException("avio_alloc_context 失败：内存不足");
            _avioContext = (IntPtr)avioCtx;

            // 2. 分配 AVFormatContext 并设置自定义 AVIO
            AVFormatContext* fmtCtx = ffmpeg.avformat_alloc_context();
            if (fmtCtx == null)
                throw new InvalidOperationException("avformat_alloc_context 失败");
            fmtCtx->pb = avioCtx;
            fmtCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            // 3. 打开输入（使用自定义 AVIO，url 传 null）
            // 注意：avformat_open_input 失败时会自动释放 fmtCtx 并将 pFmtCtx 置 null
            // 因此 SafeHandle 必须在此调用成功后才创建
            AVFormatContext* pFmtCtx = fmtCtx;
            int ret = ffmpeg.avformat_open_input(&pFmtCtx, null, null, null);
            if (ret < 0)
            {
                // avformat_open_input 失败时已自动释放 fmtCtx 并将 pFmtCtx 置 null
                // 不创建 SafeHandle，避免悬垂指针（use-after-free）
                string errorMsg = GetErrorString(ret);
                throw new InvalidOperationException($"avformat_open_input 失败: {errorMsg} (code={ret})");
            }

            // 成功：创建 SafeHandle（pFmtCtx 可能与原 fmtCtx 不同，avformat_open_input 可能重新分配）
            _formatContextHandle = new SafeAVFormatContextHandle((IntPtr)pFmtCtx);

            // 4. 查找流信息
            ret = ffmpeg.avformat_find_stream_info(pFmtCtx, null);
            if (ret < 0)
            {
                _logger.LogWarning("avformat_find_stream_info 返回 {Ret}，部分轨道信息可能不可用", ret);
            }

            // 5. 解析轨道
            _tracks = ParseTracks(pFmtCtx);

            // 6. 解析元数据
            _metadata = ParseMetadata(pFmtCtx);

            _logger.LogInformation("FFmpeg 打开成功: {StreamCount} 条轨道, 时长 {Duration}",
                pFmtCtx->nb_streams, _metadata.Duration);
        }
        catch
        {
            // 清理已分配的资源（顺序：先 AVFormatContext 后 AVIO，避免 use-after-free）：
            // - SafeHandle 仅在 avformat_open_input 成功后创建，失败时为 null，不会悬垂
            // - avformat_open_input 失败时已自动释放 AVFormatContext，无需重复释放
            // - avformat_open_input 之前的失败（avformat_alloc_context 返回 null）无资源需释放
            // - AVIO 缓冲区和上下文需手动清理（因 AVFMT_FLAG_CUSTOM_IO 不会被自动释放）
            _formatContextHandle?.Dispose();
            _formatContextHandle = null;
            CleanupAVIO();
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 真异步：使用 <c>await Task.Run</c> 卸载 av_read_frame 到线程池。
    /// av_read_frame 通过 AVIO 回调调用 <see cref="IMediaStream.Read(Span{byte})"/>（同步边界）。
    /// </remarks>
    public async ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        return await Task.Run(() => ReadPacketCore(ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ReadPacketAsync 的同步核心逻辑。
    /// </summary>
    private unsafe MediaPacket? ReadPacketCore(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        AVFormatContext* fmtCtx = (AVFormatContext*)_formatContextHandle!.DangerousGetHandle();

        // 分配临时 AVPacket
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        if (pkt == null)
            throw new InvalidOperationException("av_packet_alloc 失败");

        try
        {
            int ret = ffmpeg.av_read_frame(fmtCtx, pkt);

            if (ret < 0)
            {
                // AVERROR_EOF 或其他错误 → 返回 null 表示流结束
                if (ret != ffmpeg.AVERROR_EOF)
                    _logger.LogWarning("av_read_frame 返回 {Ret}: {Error}", ret, GetErrorString(ret));
                return null;
            }

            // V2-05 B5 引用计数零拷贝：av_packet_clone = av_packet_alloc + av_packet_ref，
            // 共享 FFmpeg 内部 buffer（引用计数 +1，非引用计数包内部自动降级为拷贝），
            // 消除 V1 的 new byte[] + Marshal.Copy 托管拷贝。
            // 克隆包生命周期由 SafeAVPacketHandle 控制（MediaPacket.Dispose → av_packet_free → 引用计数 -1）。
            AVPacket* clone = ffmpeg.av_packet_clone(pkt);
            if (clone == null)
                throw new InvalidOperationException("av_packet_clone 失败（内存不足）");
            var owner = new SafeAVPacketHandle((IntPtr)clone);

            ReadOnlyMemory<byte> data = clone->size > 0 && clone->data != null
                ? new NativeBufferMemoryManager((IntPtr)clone->data, clone->size).Memory
                : ReadOnlyMemory<byte>.Empty;

            // 提取时间戳和元数据
            double timeBase = GetTimeBase(fmtCtx, pkt->stream_index);
            TimeSpan timestamp = pkt->pts != ffmpeg.AV_NOPTS_VALUE
                ? TimeSpan.FromTicks((long)(pkt->pts * timeBase * TimeSpan.TicksPerSecond))
                : TimeSpan.Zero;
            TimeSpan duration = pkt->duration != 0
                ? TimeSpan.FromTicks((long)(pkt->duration * timeBase * TimeSpan.TicksPerSecond))
                : TimeSpan.Zero;
            bool keyFrame = (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

            return new MediaPacket(pkt->stream_index, data, timestamp, duration, keyFrame, owner);
        }
        finally
        {
            ffmpeg.av_packet_unref(pkt);
            AVPacket* p = pkt;
            ffmpeg.av_packet_free(&p);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 真异步：使用 <c>await Task.Run</c> 卸载 av_seek_frame 到线程池。
    /// </remarks>
    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        return await Task.Run(() => SeekCore(position, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// SeekAsync 的同步核心逻辑。
    /// </summary>
    private unsafe bool SeekCore(TimeSpan position, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        AVFormatContext* fmtCtx = (AVFormatContext*)_formatContextHandle!.DangerousGetHandle();

        // 转换为 FFmpeg 时间戳（AV_TIME_BASE = 微秒）
        long targetTs = (long)(position.TotalSeconds * ffmpeg.AV_TIME_BASE);

        int ret = ffmpeg.av_seek_frame(fmtCtx, -1, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (ret < 0)
        {
            _logger.LogWarning("av_seek_frame 失败: {Error} (code={Ret})", GetErrorString(ret), ret);
            return false;
        }

        _logger.LogDebug("Seek 到 {Position}", position);
        return true;
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (!_opened) return;
        _opened = false;

        // 必须先释放 AVFormatContext（avformat_close_input 会访问 pb 但因 CUSTOM_IO 不会释放它），
        // 再清理 AVIO 资源。顺序反了会导致 use-after-free。
        _formatContextHandle?.Dispose();
        _formatContextHandle = null;
        CleanupAVIO();
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：原生释放为快速同步操作，返回 <see cref="ValueTask.CompletedTask"/>。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Close();
    }

    // ── AVIO 回调（同步边界：C 函数指针签名强制同步）──

    /// <summary>
    /// AVIO 读取回调。同步边界：C 函数指针签名强制同步。
    /// 调用 <see cref="IMediaStream.Read(Span{byte})"/> 同步读取。
    /// </summary>
    /// <remarks>
    /// 运行在 FFmpeg 工作线程（非 UI 线程）。建连已在 <see cref="OpenAsync"/> 的异步路径完成，
    /// 此处仅做已连接流的逐块同步读取——这是 FFmpeg C API 的固有同步边界，非伪异步。
    /// </remarks>
    private unsafe int ReadPacketCallback(void* opaque, byte* buf, int bufSize)
    {
        if (_stream == null || _disposed)
            return ffmpeg.AVERROR_EOF;

        Span<byte> span = new(buf, bufSize);
        int read = _stream.Read(span);

        return read == 0 ? ffmpeg.AVERROR_EOF : read;
    }

    /// <summary>
    /// AVIO 定位回调。同步边界：委托给 <see cref="IMediaStream.Seek"/>。
    /// </summary>
    private unsafe long SeekCallback(void* opaque, long offset, int whence)
    {
        if (_stream == null || _disposed || !_stream.CanSeek)
            return ffmpeg.AVERROR_EOF;

        // AVSEEK_SIZE：查询流大小
        if (whence == ffmpeg.AVSEEK_SIZE)
        {
            long len = _stream.Length;
            return len < 0 ? ffmpeg.AVERROR_EOF : len;
        }

        SeekOrigin origin = whence switch
        {
            0 => SeekOrigin.Begin,
            1 => SeekOrigin.Current,
            2 => SeekOrigin.End,
            _ => SeekOrigin.Begin
        };

        return _stream.Seek(offset, origin);
    }

    // ── 辅助方法 ──

    /// <summary>获取流的时间基（转换为秒的 double）。</summary>
    private static unsafe double GetTimeBase(AVFormatContext* fmtCtx, int streamIndex)
    {
        if (streamIndex < 0 || streamIndex >= (int)fmtCtx->nb_streams)
            return 0;
        AVRational tb = fmtCtx->streams[streamIndex]->time_base;
        return ffmpeg.av_q2d(tb);
    }

    /// <summary>解析所有轨道信息。</summary>
    private static unsafe IReadOnlyList<MediaTrack> ParseTracks(AVFormatContext* fmtCtx)
    {
        var tracks = new List<MediaTrack>((int)fmtCtx->nb_streams);

        for (uint i = 0; i < fmtCtx->nb_streams; i++)
        {
            AVStream* avStream = fmtCtx->streams[i];
            AVCodecParameters* codecPar = avStream->codecpar;
            string? language = GetStreamLanguage(avStream);
            double timeBase = GetTimeBase(fmtCtx, (int)i);
            TimeSpan streamDuration = avStream->duration > 0
                ? TimeSpan.FromTicks((long)(avStream->duration * timeBase * TimeSpan.TicksPerSecond))
                : TimeSpan.Zero;

            MediaTrack? track = codecPar->codec_type switch
            {
                AVMediaType.AVMEDIA_TYPE_VIDEO => new MediaTrack
                {
                    Index = (int)i,
                    Type = TrackType.Video,
                    VideoCodec = MapVideoCodecFromFFmpeg(codecPar->codec_id),
                    Language = language,
                    BitRate = codecPar->bit_rate,
                    VideoInfo = new VideoTrackInfo
                    {
                        Width = codecPar->width,
                        Height = codecPar->height,
                        PixelFormat = MapPixelFormatFromFFmpeg((AVPixelFormat)codecPar->format),
                        FrameRate = GetFrameRate(avStream),
                        Duration = streamDuration
                    }
                },

                AVMediaType.AVMEDIA_TYPE_AUDIO => new MediaTrack
                {
                    Index = (int)i,
                    Type = TrackType.Audio,
                    AudioCodec = MapAudioCodecFromFFmpeg(codecPar->codec_id),
                    Language = language,
                    BitRate = codecPar->bit_rate,
                    AudioInfo = new AudioTrackInfo
                    {
                        SampleRate = codecPar->sample_rate,
                        Channels = codecPar->ch_layout.nb_channels,
                        BitsPerSample = codecPar->bits_per_coded_sample,
                        Duration = streamDuration
                    }
                },

                AVMediaType.AVMEDIA_TYPE_SUBTITLE => new MediaTrack
                {
                    Index = (int)i,
                    Type = TrackType.Subtitle,
                    SubtitleCodec = MapSubtitleCodecFromFFmpeg(codecPar->codec_id),
                    Language = language,
                    BitRate = codecPar->bit_rate
                },

                _ => null
            };

            if (track != null)
                tracks.Add(track);
        }

        return tracks;
    }

    /// <summary>解析容器元数据。</summary>
    private static unsafe MediaMetadata ParseMetadata(AVFormatContext* fmtCtx)
    {
        TimeSpan duration = fmtCtx->duration > 0
            ? TimeSpan.FromTicks(fmtCtx->duration * TimeSpan.TicksPerSecond / ffmpeg.AV_TIME_BASE)
            : TimeSpan.Zero;

        string? fmtName = null;
        if (fmtCtx->iformat != null)
        {
            fmtName = Marshal.PtrToStringUTF8((IntPtr)fmtCtx->iformat->name);
        }

        ContainerFormat containerFormat = MapContainerFormatFromFFmpeg(fmtName);

        // 提取元数据字典
        string? title = null, artist = null, album = null, genre = null;
        int? year = null;

        AVDictionaryEntry* entry = null;
        while (true)
        {
            entry = ffmpeg.av_dict_get(fmtCtx->metadata, "", entry, ffmpeg.AV_DICT_IGNORE_SUFFIX);
            if (entry == null) break;

            string key = Marshal.PtrToStringUTF8((IntPtr)entry->key) ?? string.Empty;
            string value = Marshal.PtrToStringUTF8((IntPtr)entry->value) ?? string.Empty;

            switch (key.ToLowerInvariant())
            {
                case "title": title = value; break;
                case "artist": artist = value; break;
                case "album": album = value; break;
                case "genre": genre = value; break;
                case "date" when int.TryParse(value, out int y): year = y; break;
            }
        }

        return new MediaMetadata
        {
            Title = title,
            Artist = artist,
            Album = album,
            Genre = genre,
            Year = year,
            Duration = duration,
            ContainerFormat = containerFormat
        };
    }

    /// <summary>获取流语言标签。</summary>
    private static unsafe string? GetStreamLanguage(AVStream* stream)
    {
        AVDictionaryEntry* entry = ffmpeg.av_dict_get(stream->metadata, "language", null, 0);
        if (entry == null) return null;
        return Marshal.PtrToStringUTF8((IntPtr)entry->value);
    }

    /// <summary>获取帧率。</summary>
    private static unsafe float GetFrameRate(AVStream* stream)
    {
        AVRational r = stream->avg_frame_rate;
        if (r.num == 0 || r.den == 0)
            r = stream->r_frame_rate;
        if (r.num == 0 || r.den == 0)
            return 0;
        return (float)r.num / r.den;
    }

    /// <summary>清理 AVIO 资源。</summary>
    private unsafe void CleanupAVIO()
    {
        if (_avioContext != IntPtr.Zero)
        {
            AVIOContext* avioCtx = (AVIOContext*)_avioContext;
            // 先将 buffer 置空防止 avio_closep 释放我们的 buffer（我们自己管理）
            avioCtx->buffer = null;
            avioCtx->buffer_size = 0;
            ffmpeg.avio_closep(&avioCtx);
            _avioContext = IntPtr.Zero;
        }

        if (_avioBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_avioBuffer);
            _avioBuffer = IntPtr.Zero;
        }
    }

    /// <summary>将 FFmpeg 错误码转换为可读字符串。</summary>
    private static string GetErrorString(int errorCode)
    {
        unsafe
        {
            byte* buf = stackalloc byte[ffmpeg.AV_ERROR_MAX_STRING_SIZE];
            ffmpeg.av_strerror(errorCode, buf, ffmpeg.AV_ERROR_MAX_STRING_SIZE);
            return Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"error code {errorCode}";
        }
    }

    // ── 编解码器映射（FFmpeg → LingFan）──

    private static VideoCodec MapVideoCodecFromFFmpeg(AVCodecID codecId) => codecId switch
    {
        AVCodecID.AV_CODEC_ID_H264 => VideoCodec.H264,
        AVCodecID.AV_CODEC_ID_HEVC => VideoCodec.H265,
        AVCodecID.AV_CODEC_ID_AV1 => VideoCodec.AV1,
        AVCodecID.AV_CODEC_ID_VP9 => VideoCodec.VP9,
        AVCodecID.AV_CODEC_ID_MPEG2VIDEO => VideoCodec.MPEG2,
        AVCodecID.AV_CODEC_ID_MPEG4 => VideoCodec.MPEG4,
        _ => VideoCodec.Unknown
    };

    private static AudioCodec MapAudioCodecFromFFmpeg(AVCodecID codecId) => codecId switch
    {
        AVCodecID.AV_CODEC_ID_AAC => AudioCodec.AAC,
        AVCodecID.AV_CODEC_ID_MP3 => AudioCodec.MP3,
        AVCodecID.AV_CODEC_ID_OPUS => AudioCodec.Opus,
        AVCodecID.AV_CODEC_ID_FLAC => AudioCodec.FLAC,
        AVCodecID.AV_CODEC_ID_VORBIS => AudioCodec.Vorbis,
        AVCodecID.AV_CODEC_ID_PCM_S16LE => AudioCodec.PCM,
        AVCodecID.AV_CODEC_ID_PCM_S24LE => AudioCodec.PCM,
        AVCodecID.AV_CODEC_ID_PCM_S32LE => AudioCodec.PCM,
        AVCodecID.AV_CODEC_ID_AC3 => AudioCodec.AC3,
        _ => AudioCodec.Unknown
    };

    private static SubtitleCodec MapSubtitleCodecFromFFmpeg(AVCodecID codecId) => codecId switch
    {
        AVCodecID.AV_CODEC_ID_SUBRIP => SubtitleCodec.SRT,
        AVCodecID.AV_CODEC_ID_ASS => SubtitleCodec.ASS,
        AVCodecID.AV_CODEC_ID_SSA => SubtitleCodec.ASS,
        AVCodecID.AV_CODEC_ID_WEBVTT => SubtitleCodec.WebVTT,
        AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE => SubtitleCodec.PGS,
        AVCodecID.AV_CODEC_ID_DVD_SUBTITLE => SubtitleCodec.VobSub,
        _ => SubtitleCodec.Unknown
    };

    private static PixelFormat MapPixelFormatFromFFmpeg(AVPixelFormat fmt) => fmt switch
    {
        AVPixelFormat.AV_PIX_FMT_YUV420P => PixelFormat.YUV420P,
        AVPixelFormat.AV_PIX_FMT_YUV422P => PixelFormat.YUV422P,
        AVPixelFormat.AV_PIX_FMT_YUV444P => PixelFormat.YUV444P,
        AVPixelFormat.AV_PIX_FMT_NV12 => PixelFormat.NV12,
        AVPixelFormat.AV_PIX_FMT_NV21 => PixelFormat.NV21,
        AVPixelFormat.AV_PIX_FMT_BGRA => PixelFormat.BGRA32,
        AVPixelFormat.AV_PIX_FMT_RGBA => PixelFormat.RGBA32,
        AVPixelFormat.AV_PIX_FMT_RGB24 => PixelFormat.RGB24,
        _ => PixelFormat.YUV420P
    };

    private static ContainerFormat MapContainerFormatFromFFmpeg(string? formatName)
    {
        if (string.IsNullOrEmpty(formatName)) return ContainerFormat.Unknown;
        if (formatName.Contains("mp4") || formatName.Contains("mov")) return ContainerFormat.MP4;
        if (formatName.Contains("matroska") || formatName.Contains("webm")) return ContainerFormat.MKV;
        if (formatName.Contains("avi")) return ContainerFormat.AVI;
        if (formatName.Contains("mpegts") || formatName.Contains("ts")) return ContainerFormat.TS;
        if (formatName.Contains("flv")) return ContainerFormat.FLV;
        return ContainerFormat.Unknown;
    }
}

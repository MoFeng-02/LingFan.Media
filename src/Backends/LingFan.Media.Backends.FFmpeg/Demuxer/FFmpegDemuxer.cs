using System.IO;
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
/// 将同步 FFmpeg 调用卸载到线程池——FFmpeg C API 本质同步，<c>Task.Run</c> 仅将阻塞从调用线程移到线程池，
/// 是<b>伪异步</b>而非真异步。但这是 FFmpeg C API 的圆有局限（无原生异步 I/O API），
/// 未来若 FFmpeg 提供异步读取接口应替换。网络建连部分由 <c>stream.ConnectAsync</c> 提供真异步。</item>
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

    // 🔴 重播归零诊断（2026-08-07）：SeekAsync 设 _logFirstPacketAfterSeek=true，
    // ReadPacketCore 在 seek 后首包打印其时间戳，用于确证「回到起点」seek 落点是否已回到 ≈0
    // （此前 matroska/webm 在 EOF 后 AVSEEK_FLAG_BACKWARD 到 ts=0 会回绕到末关键帧，
    // 实测 m1.webm 落点≈10.267s → 重播视频冻结 ~10s）。一次性、零架构风险。
    private bool _logFirstPacketAfterSeek;
    private long _lastSeekTargetTs;

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
    /// 混合：<c>await stream.ConnectAsync</c>（真异步 I/O）+ <c>await Task.Run(OpenCore)</c>（伪异步：avformat_open_input + avformat_find_stream_info 为同步 C 调用）。
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

        // 伪异步：avformat_open_input + avformat_find_stream_info 为同步 C 调用，Task.Run 仅卸载到线程池。
        // 未来改进：FFmpeg 无原生异步 API，除非更换为异步 I/O 层（如 libavformat async protocol）。
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

        // 🔴 诊断：在每条原生调用前后打印到 stderr 并 flush，
        // 若进程在原生调用内静默死亡（无托管异常），最后一条 [demux-trace] 即指出崩溃点。
        void Dbg(string s) { try { Console.Error.WriteLine($"  [demux-trace] {s}"); Console.Error.Flush(); } catch { } }

        try
        {
            // 本地真实文件 → ffmpeg 原生 file 协议；否则自定义 AVIO（内存/网络流）。
            // 🔴 规避：自定义 AVIO 需手动写 fmtCtx->pb/flags，当 AutoGen 8.1.0 的 AVFormatContext
            // 布局与 BtbN master 的 avformat-62.dll 漂移时，该写会损坏原生结构体 → avformat_open_input
            // 内部 0xC0000005。本地文件走 file 协议（传真实路径、让 ffmpeg 自分配上下文）彻底规避。
            bool useFileProtocol = stream.Location != null
                && stream.Location.IndexOf("://", StringComparison.Ordinal) < 0
                && File.Exists(stream.Location);

            AVFormatContext* pFmtCtx;
            AVFormatContext* fmtCtx = null;

            if (!useFileProtocol)
            {
                // 1. 分配 AVIO 缓冲区和上下文
                _avioBuffer = Marshal.AllocHGlobal(AvioBufferSize);
                if (_avioBuffer == IntPtr.Zero)
                    throw new InvalidOperationException("AllocHGlobal 失败：AVIO 缓冲区");

                // 委托实例已作为字段保持引用，回调通过实例方法访问 _stream
                Dbg("avio_alloc_context 之前");
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
                Dbg("avio_alloc_context OK");

                // 2. 分配 AVFormatContext 并设置自定义 AVIO
                fmtCtx = ffmpeg.avformat_alloc_context();

                if (fmtCtx == null)
                    throw new InvalidOperationException("avformat_alloc_context 失败");
                fmtCtx->pb = avioCtx;
                fmtCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
                Dbg("avformat_alloc_context OK");
            }

            if (useFileProtocol)
            {
                // ffmpeg 原生 file 协议：pFmtCtx 传 null 让 ffmpeg 自分配（正确原生布局），
                // 不手动碰 pb/flags，彻底规避结构体布局漂移导致的 AV。
                Dbg($"avformat_open_input（file 协议: {stream.Location}）之前");
                pFmtCtx = null;
                int openRet = ffmpeg.avformat_open_input(&pFmtCtx, stream.Location, null, null);
                Dbg($"avformat_open_input 返回 ret={openRet}");
                if (openRet < 0)
                {
                    string errorMsg = GetErrorString(openRet);
                    throw new InvalidOperationException($"avformat_open_input（file 协议）失败: {errorMsg} (code={openRet})");
                }
            }
            else
            {
                // 3. 打开输入（自定义 AVIO，url 传 null）
                pFmtCtx = fmtCtx;
                Dbg("avformat_open_input 之前");
                int openRet = ffmpeg.avformat_open_input(&pFmtCtx, null, null, null);
                Dbg($"avformat_open_input 返回 ret={openRet}");
                if (openRet < 0)
                {
                    string errorMsg = GetErrorString(openRet);
                    throw new InvalidOperationException($"avformat_open_input 失败: {errorMsg} (code={openRet})");
                }
            }

            // 成功：创建 SafeHandle（pFmtCtx 由 ffmpeg 分配/重新分配，持有即有效）
            _formatContextHandle = new SafeAVFormatContextHandle((IntPtr)pFmtCtx);
            Dbg("avformat_open_input OK，开始 find_stream_info");

            // 4. 查找流信息
            int ret = ffmpeg.avformat_find_stream_info(pFmtCtx, null);
            Dbg($"avformat_find_stream_info 返回 ret={ret}");
            if (ret < 0)
            {
                _logger.LogWarning("avformat_find_stream_info 返回 {Ret}，部分轨道信息可能不可用", ret);
            }

            // 5. 解析轨道
            Dbg("ParseTracks 之前");
            _tracks = ParseTracks(pFmtCtx);
            Dbg($"ParseTracks OK（{_tracks.Count} 条）");

            // 6. 解析元数据
            Dbg("ParseMetadata 之前");
            _metadata = ParseMetadata(pFmtCtx);
            Dbg("ParseMetadata OK");

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
    /// 伪异步：<c>await Task.Run</c> 卸载 av_read_frame（同步 C 调用）到线程池。
    /// av_read_frame 通过 AVIO 回调调用 <see cref="IMediaStream.Read(Span{byte})"/>（同步边界）。
    /// </remarks>
    public async ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        // 伪异步：av_read_frame 为同步 C 调用，Task.Run 仅卸载到线程池。
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

            // 引用计数零拷贝：av_packet_clone = av_packet_alloc + av_packet_ref，
            // 共享 FFmpeg 内部 buffer（引用计数 +1，非引用计数包内部自动降级为拷贝），
            // 消除 new byte[] + Marshal.Copy 托管拷贝。
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

            // 🔴 重播归零诊断（2026-08-07）：SeekAsync 设 _logFirstPacketAfterSeek=true 后，
            // 这里打印 seek 后首包的时间戳，确证「回到起点」落点已回到 ≈0（而非回绕到末关键帧）。
            // 一次性消费：打印后即刻复位，不影响后续读取性能。
            if (_logFirstPacketAfterSeek)
            {
                _logFirstPacketAfterSeek = false;
                _logger.LogInformation(
                    "[SEEK-DIAG] seek 后首包: stream={Stream} pts={Pts} dts={Dts} key={Key} targetTs={TargetTs} " +
                    "(pts≈0 表示已正确回到起点；pts≠0 表示回绕到末关键帧)",
                    pkt->stream_index, timestamp,
                    pkt->dts != ffmpeg.AV_NOPTS_VALUE
                        ? TimeSpan.FromTicks((long)(pkt->dts * timeBase * TimeSpan.TicksPerSecond))
                        : TimeSpan.Zero,
                    keyFrame, _lastSeekTargetTs);
            }

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
    /// 伪异步：<c>await Task.Run</c> 卸载 av_seek_frame（同步 C 调用）到线程池。
    /// </remarks>
    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        // 伪异步：av_seek_frame 为同步 C 调用，Task.Run 仅卸载到线程池。
        // 标记诊断：下个首包打印时间戳，确证回到起点的落点。
        long reqTs = (long)(position.TotalSeconds * ffmpeg.AV_TIME_BASE);
        _lastSeekTargetTs = reqTs;
        _logFirstPacketAfterSeek = true;
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

        int ret;
        if (targetTs <= 0)
        {
            // 🔴 重播归零修正（2026-08-07）：matroska/webm 在流末 EOF 后对「ts=0 + AVSEEK_FLAG_BACKWARD」
            // 会回绕到**末关键帧**（实测 m1.webm 落点≈10.267s 而非 0）→ 重播时解码器从 ~10s 处产出首帧，
            // 视频比音频(主时钟=0)超前 ~10s，同步器令呈现线程 WaitUntilDue 休眠等主时钟追上 → 画面冻结。
            // 改用 nearest（flags=0）：落点=最接近 0 的关键帧 = 文件首关键帧@0，杜绝回绕到末关键帧。
            // 兜底：nearest 仍失败则回退 BACKWARD（至少保证能 seek，由诊断日志暴露落点异常）。
            ret = ffmpeg.av_seek_frame(fmtCtx, -1, 0, 0);
            if (ret < 0)
                ret = ffmpeg.av_seek_frame(fmtCtx, -1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
        }
        else
        {
            ret = ffmpeg.av_seek_frame(fmtCtx, -1, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
        }
        if (ret < 0)
        {
            _logger.LogWarning("av_seek_frame 失败: {Error} (code={Ret})", GetErrorString(ret), ret);
            return false;
        }

        _logger.LogDebug("Seek 到 {Position}（归零修正={IsRewind}）", position, targetTs <= 0);
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
        // 🔴 同步边界：绝不允许托管异常逃逸进原生 ffmpeg（否则进程静默死亡、无托管栈迹）。
        try
        {
            if (_stream == null || _disposed)
                return ffmpeg.AVERROR_EOF;

            if (bufSize <= 0)
                return ffmpeg.AVERROR_EOF;

            Span<byte> span = new(buf, bufSize);
            int read = _stream.Read(span);

            return read <= 0 ? ffmpeg.AVERROR_EOF : read;
        }
        catch (Exception ex)
        {
            try { _logger.LogError(ex, "AVIO ReadPacketCallback 异常（已吞除以防逃逸进原生 ffmpeg）"); } catch { }
            return ffmpeg.AVERROR_EOF;
        }
    }

    /// <summary>
    /// AVIO 定位回调。同步边界：委托给 <see cref="IMediaStream.Seek"/>。
    /// </summary>
    private unsafe long SeekCallback(void* opaque, long offset, int whence)
    {
        // 🔴 同步边界：同上，绝不允许托管异常逃逸进原生 ffmpeg。
        try
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
        catch (Exception ex)
        {
            try { _logger.LogError(ex, "AVIO SeekCallback 异常（已吞除以防逃逸进原生 ffmpeg）"); } catch { }
            return ffmpeg.AVERROR_EOF;
        }
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

    /// <summary>从 ffmpeg 流参数拷贝 <c>extradata</c>（SPS+PPS / AudioSpecificConfig 等）到托管内存。
    /// 返回默认空表示无 extradata。</summary>
    /// <remarks>托管拷贝以确保生命周期独立于 AVStream（AVFormatContext 关闭后指针即失效）。</remarks>
    private static unsafe ReadOnlyMemory<byte> CopyExtradata(AVCodecParameters* codecPar)
    {
        if (codecPar->extradata == null || codecPar->extradata_size <= 0)
            return default;
        var bytes = new byte[codecPar->extradata_size];
        Marshal.Copy((IntPtr)codecPar->extradata, bytes, 0, bytes.Length);
        return bytes;
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

            // 🔴 流时间基（AVStream.time_base）：ffmpeg 解码帧的 pts/dts 以此为单位。须透传给解码器写入
            // ctx->pkt_timebase，否则解码后 avFrame->time_base / ctx->time_base 常为 0，帧时间戳全 0
            // （视频不节流突发提交、主时钟被 SyncTo(0) 钉死、pos 不前进）。den==0 表示无有效时间基，回落 default。
            AVRational rawTb = avStream->time_base;
            Rational trackTimeBase = rawTb.den > 0 ? new Rational(rawTb.num, rawTb.den) : default;

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
                        Duration = streamDuration,
                        TimeBase = trackTimeBase,
                        // 🔴 透传编解码器私有配置（H264/H265 的 SPS+PPS 等）。解码器需据此设置 extradata，
                        // 否则 MP4 中 length-prefixed 的 HEVC/H264 包无法被解码器解析（No start code）。
                        CodecConfiguration = CopyExtradata(codecPar)
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
                        TimeBase = trackTimeBase,
                        // 🔴 透传 AudioSpecificConfig 等。AAC 在 MP4 中为裸流，解码器必须据此设置 extradata。
                        CodecConfiguration = CopyExtradata(codecPar),
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

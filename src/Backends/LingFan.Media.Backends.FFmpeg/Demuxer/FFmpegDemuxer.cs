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
/// 是<b>伪异步</b>而非真异步。但这是 FFmpeg C API 的固有局限（无原生异步 I/O API），
/// 未来若 FFmpeg 提供异步读取接口应替换。网络建连部分由 <c>stream.ConnectAsync</c> 提供真异步。</item>
/// <item><c>InitializeAsync</c>：接口契约，返回 <c>Task.CompletedTask</c>（无 I/O）。</item>
/// <item>AVIO <c>ReadPacketCallback</c>：同步边界（C 函数指针），调用 <see cref="IMediaStream.Read(Span{byte})"/>；
/// 网络建连已由 <see cref="OpenAsync"/> 在 <c>Task.Run</c> 前经 <see cref="IMediaStream.ConnectAsync"/> 异步完成。</item>
/// <item><c>Close</c> / <c>Dispose</c> / <c>DisposeAsync</c>：同步原生释放。</item>
/// </list>
/// <para><b>线程安全</b>：单线程使用（BufferManager 读取线程），非线程安全。</para>
/// <para><b>AOT 兼容</b>：sealed 类，SafeHandle 管理原生资源，无反射。</para>
/// </remarks>
internal sealed partial class FFmpegDemuxer : IMediaDemuxer
{
    private readonly ILogger<FFmpegDemuxer> _logger;

    // 原生资源
    private SafeAVFormatContextHandle? _formatContextHandle;
    private IntPtr _avioBuffer = IntPtr.Zero;
    private IntPtr _avioContext = IntPtr.Zero;

    // AVIO 回调委托（必须保持引用防止 GC 回收）
    // 用 object 存储避免 unsafe 字段声明（委托类型含指针参数需要 unsafe 上下文）
    private readonly AVIOReadFunc _readDelegate;
    private readonly AVIOSeekFunc _seekDelegate;

    // 流引用（AVIO 回调使用）
    private IMediaStream? _stream;

    // 状态
    private bool _opened;
    private bool _disposed;
    private IReadOnlyList<MediaTrack> _tracks = Array.Empty<MediaTrack>();
    private MediaMetadata _metadata = new();

    // 重播归零诊断：SeekAsync 设 _logFirstPacketAfterSeek=true，
    // ReadPacketCore 在 seek 后首包打印其时间戳，用于确证「回到起点」seek 落点是否已回到 ≈0
    // （matroska/webm 在 EOF 后 AVSEEK_FLAG_BACKWARD 到 ts=0 可能回绕到末关键帧
    // → 重播视频冻结较长时间）。一次性诊断，不影响控制流。
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
        _readDelegate = new AVIOReadFunc(ReadPacketCallback);
        _seekDelegate = new AVIOSeekFunc(SeekCallback);
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

        // 诊断：在每条原生调用前后打印到 stderr 并 flush，
        // 若进程在原生调用内静默死亡（无托管异常），最后一条 [demux-trace] 即指出崩溃点。
        void Dbg(string s) { try { Console.Error.WriteLine($"  [demux-trace] {s}"); Console.Error.Flush(); } catch { } }

        try
        {
            // 本地真实文件 → ffmpeg 原生 file 协议；否则自定义 AVIO（内存/网络流）。
            // 规避：自定义 AVIO 需手动写 fmtCtx->pb/flags，当 AutoGen 8.1.0 的 AVFormatContext
            // 布局与 BtbN master 的 avformat-62.dll 漂移时，该写会损坏原生结构体 → avformat_open_input
            // 内部原生访问违规。本地文件走 file 协议（传真实路径、让 ffmpeg 自分配上下文）彻底规避。
            bool useFileProtocol = stream.Location != null
                && stream.Location.IndexOf("://", StringComparison.Ordinal) < 0
                && File.Exists(stream.Location);

            AVFormatContext* pFmtCtx;
            AVFormatContext* fmtCtx = null;

            if (!useFileProtocol)
            {
                // 1. 分配 AVIO 缓冲区和上下文。
                //    缓冲必须以 av_malloc 分配（avio.h 契约：libavformat 可能经 av_realloc 替换缓冲、
                //    avio_closep 内部以 av_free 归还当前 buffer）——AllocHGlobal 组合在 find_stream_info
                //    的 seekback 重分配路径上会堆损坏。
                byte* avioBuf = (byte*)FF.av_malloc((UIntPtr)AvioBufferSize);
                if (avioBuf == null)
                    throw new InvalidOperationException("av_malloc 失败：AVIO 缓冲区");
                _avioBuffer = (IntPtr)avioBuf;

                // 委托实例已作为字段保持引用，回调通过实例方法访问 _stream
                Dbg("avio_alloc_context 之前");
                AVIOContext* avioCtx = FF.avio_alloc_context(
                    avioBuf, AvioBufferSize,
                    0, // write_flag = 0 (read-only)
                    null, // opaque
                    _readDelegate,
                    null, // write_packet (null = read-only)
                    stream.CanSeek ? _seekDelegate : null);

                if (avioCtx == null)
                    throw new InvalidOperationException("avio_alloc_context 失败：内存不足");
                _avioContext = (IntPtr)avioCtx;
                Dbg("avio_alloc_context OK");

                // 2. 分配 AVFormatContext 并设置自定义 AVIO
                fmtCtx = FF.avformat_alloc_context();

                if (fmtCtx == null)
                    throw new InvalidOperationException("avformat_alloc_context 失败");
                fmtCtx->pb = avioCtx;
                fmtCtx->flags |= FF.AVFMT_FLAG_CUSTOM_IO;
                Dbg("avformat_alloc_context OK");
            }

            if (useFileProtocol)
            {
                // ffmpeg 原生 file 协议：pFmtCtx 传 null 让 ffmpeg 自分配（正确原生布局），
                // 不手动碰 pb/flags，彻底规避结构体布局漂移导致的 AV。
                Dbg($"avformat_open_input（file 协议: {stream.Location}）之前");
                pFmtCtx = null;
                int openRet = FF.avformat_open_input(&pFmtCtx, stream.Location, null, null);
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
                int openRet = FF.avformat_open_input(&pFmtCtx, null, null, null);
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
            int ret = FF.avformat_find_stream_info(pFmtCtx, null);
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
        AVPacket* pkt = FF.av_packet_alloc();
        if (pkt == null)
            throw new InvalidOperationException("av_packet_alloc 失败");

        try
        {
            int ret = FF.av_read_frame(fmtCtx, pkt);

            if (ret < 0)
            {
                if (ret == FF.AVERROR_EOF)
                {
                    // 定位后的首个读请求就遇到流末：落点可能超出可解码范围，或读链路已断开。
                    // 仍按流末返回（上层据此收尾），但留下上下文，便于与「正常播放完毕」区分。
                    if (_logFirstPacketAfterSeek)
                    {
                        _logFirstPacketAfterSeek = false;
                        _logger.LogWarning(
                            "定位后首包即为流末：targetTs={TargetTs}，落点可能超出可解码范围或读链路已断开",
                            _lastSeekTargetTs);
                    }

                    // 流正常结束
                    return null;
                }

                // 非 EOF 错误（数据源中断、容器损坏等）按错误传播，
                // 不以「返回 null = 流结束」掩盖——否则中断表现为无声无息的提前播完。
                // pkt 由 finally 统一释放，此处不重复释放。
                throw new IOException($"av_read_frame 失败: {GetErrorString(ret)} (code={ret})");
            }

            // 引用计数零拷贝：av_packet_clone = av_packet_alloc + av_packet_ref，
            // 共享 FFmpeg 内部 buffer（引用计数 +1，非引用计数包内部自动降级为拷贝），
            // 消除 new byte[] + Marshal.Copy 托管拷贝。
            // 克隆包生命周期由 SafeAVPacketHandle 控制（MediaPacket.Dispose → av_packet_free → 引用计数 -1）。
            AVPacket* clone = FF.av_packet_clone(pkt);
            if (clone == null)
                throw new InvalidOperationException("av_packet_clone 失败（内存不足）");
            var owner = new SafeAVPacketHandle((IntPtr)clone);

            ReadOnlyMemory<byte> data = clone->size > 0 && clone->data != null
                ? new NativeBufferMemoryManager((IntPtr)clone->data, (int)clone->size).Memory
                : ReadOnlyMemory<byte>.Empty;

            // 提取时间戳和元数据
            double timeBase = GetTimeBase(fmtCtx, pkt->stream_index);
            TimeSpan timestamp = pkt->pts != FF.AV_NOPTS_VALUE
                ? TimeSpan.FromTicks((long)(pkt->pts * timeBase * TimeSpan.TicksPerSecond))
                : TimeSpan.Zero;
            TimeSpan duration = pkt->duration != 0
                ? TimeSpan.FromTicks((long)(pkt->duration * timeBase * TimeSpan.TicksPerSecond))
                : TimeSpan.Zero;
            bool keyFrame = (pkt->flags & FF.AV_PKT_FLAG_KEY) != 0;

            // 重播归零诊断：SeekAsync 设 _logFirstPacketAfterSeek=true 后，
            // 这里打印 seek 后首包的时间戳，确证「回到起点」落点已回到 ≈0（而非回绕到末关键帧）。
            // 一次性消费：打印后即刻复位，不影响后续读取性能。
            if (_logFirstPacketAfterSeek)
            {
                _logFirstPacketAfterSeek = false;
                _logger.LogInformation(
                    "[SEEK-DIAG] seek 后首包: stream={Stream} pts={Pts} dts={Dts} key={Key} targetTs={TargetTs} " +
                    "(pts≈0 表示已正确回到起点；pts≠0 表示回绕到末关键帧)",
                    pkt->stream_index, timestamp,
                    pkt->dts != FF.AV_NOPTS_VALUE
                        ? TimeSpan.FromTicks((long)(pkt->dts * timeBase * TimeSpan.TicksPerSecond))
                        : TimeSpan.Zero,
                    keyFrame, _lastSeekTargetTs);
            }

            return new MediaPacket(pkt->stream_index, data, timestamp, duration, keyFrame, owner);
        }
        finally
        {
            FF.av_packet_unref(pkt);
            AVPacket* p = pkt;
            FF.av_packet_free(&p);
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
        long reqTs = (long)(position.TotalSeconds * FF.AV_TIME_BASE);
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
        long targetTs = (long)(position.TotalSeconds * FF.AV_TIME_BASE);

        int ret;
        if (targetTs <= 0)
        {
            // 重播归零修正：matroska/webm 在流末 EOF 后对「ts=0 + AVSEEK_FLAG_BACKWARD」
            // 会回绕到**末关键帧**（而非起点 0）→ 重播时解码器从靠后的位置产出首帧，
            // 视频比音频(主时钟=0)超前较多，同步器令呈现线程 WaitUntilDue 休眠等主时钟追上 → 画面冻结。
            // 改用 nearest（flags=0）：落点=最接近 0 的关键帧 = 文件首关键帧@0，杜绝回绕到末关键帧。
            // 兜底：nearest 仍失败则回退 BACKWARD（至少保证能 seek，由诊断日志暴露落点异常）。
            ret = FF.av_seek_frame(fmtCtx, -1, 0, 0);
            if (ret < 0)
                ret = FF.av_seek_frame(fmtCtx, -1, 0, FF.AVSEEK_FLAG_BACKWARD);
        }
        else
        {
            ret = FF.av_seek_frame(fmtCtx, -1, targetTs, FF.AVSEEK_FLAG_BACKWARD);
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
}

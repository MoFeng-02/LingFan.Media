using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg.Interop;
using LingFan.Media.Backends.FFmpeg.Models;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Decoders;

// FFmpegVideoDecoder 的 Core 职责段（部分类，由 FFmpegVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与轻量入口（ctor/InitializeAsync/DecodeAsync/FlushAsync/SetFramePool/映射工具）在主文件；
// 本段含 BSF 转换/解码排空核心/复位/释放，全部同步原生调用（热路径 ValueTask 同步完成）；初始化见 Init 段。
internal sealed partial class FFmpegVideoDecoder
{

    /// <summary>
    /// 将入向 <see cref="MediaPacket"/> 转换为待发送 AVPacket：填充数据/pts/dts/keyframe 标志，
    /// 若已安装 mp4toannexb BSF 则先经其转换为 Annex-B（hevc_mp4toannexb 为严格 1:1 过滤器）。
    /// 返回的 AVPacket 由调用方入队 <c>_pendingConv</c>，发送成功后由调用方 av_packet_free。
    /// 返回 null 表示当前无法转换（如 BSF 尚未产出，1:1 过滤器下罕见）。
    /// </summary>
    private unsafe AVPacket* ConvertToConv(MediaPacket packet)
    {
        AVPacket* pkt = FF.av_packet_alloc();
        if (pkt == null)
            return null;
        int allocRet = FF.av_new_packet(pkt, packet.Data.Length);
        if (allocRet < 0)
        {
            FF.av_packet_free(&pkt);
            return null;
        }
        packet.Data.Span.CopyTo(new Span<byte>(pkt->data, packet.Data.Length));
        // 时间戳换算：以「流 time_base」为单位，绝不能用解码后 ctx->time_base（常 0 → 时间戳全 0）。
        double timeBase = _tbSeconds;
        pkt->pts = timeBase > 0
            ? (long)(packet.Timestamp.TotalSeconds / timeBase)
            : FF.AV_NOPTS_VALUE;
        pkt->dts = pkt->pts;
        if (packet.KeyFrame)
            pkt->flags |= FF.AV_PKT_FLAG_KEY;

        if (_bsfContext == null)
            return pkt; // 无 BSF：pkt 即待发送包

        // 经 BSF 转换（BSF 内部引用 pkt 数据；pkt 随后释放安全）
        int bret = FF.av_bsf_send_packet(_bsfContext, pkt);
        if (bret < 0 && bret != EAGAIN)
        {
            if (bret != FF.AVERROR_EOF)
                _logger.LogWarning("av_bsf_send_packet 返回 {Ret}: {Error}", bret, GetErrorString(bret));
            FF.av_packet_free(&pkt);
            return null;
        }
        AVPacket* conv = FF.av_packet_alloc();
        if (conv == null)
        {
            FF.av_packet_free(&pkt);
            return null;
        }
        bret = FF.av_bsf_receive_packet(_bsfContext, conv);
        FF.av_packet_free(&pkt); // 转换完成，原始包释放（conv 为独立分配）
        if (bret == EAGAIN || bret == FF.AVERROR_EOF)
        {
            // 1:1 过滤器下罕见：BSF 暂未产出。pkt 已被 BSF 消费，conv 未用。
            FF.av_packet_free(&conv);
            return null;
        }
        if (bret < 0)
        {
            _logger.LogWarning("av_bsf_receive_packet 返回 {Ret}: {Error}", bret, GetErrorString(bret));
            FF.av_packet_free(&conv);
            return null;
        }
        return conv;
    }

    /// <summary>由已接收的 AVFrame 构造 <see cref="VideoFrame"/>（区分 D3D11VA/MediaCodec/软件路径）。</summary>
    /// <remarks>
    /// 唯一帧路径分流点：三条支路各自计数（GPU 零拷贝 / CPU 拷贝），构造成功后才计数，
    /// 避免异常路径污染统计。计数结果由 <see cref="Dispose"/> 打印帧路径统计。
    /// </remarks>
    private unsafe VideoFrame? MakeFrame(AVFrame* avFrame)
    {
        var hwFmt = (AVPixelFormat)avFrame->format;
        if (hwFmt is AVPixelFormat.AV_PIX_FMT_D3D11VA_VLD or AVPixelFormat.AV_PIX_FMT_D3D11)
            return CreateHardwareFrameFromAVFrame(avFrame);
        if (hwFmt == AVPixelFormat.AV_PIX_FMT_VAAPI)
            return CreateVaApiFrame(avFrame);
        if (hwFmt == AVPixelFormat.AV_PIX_FMT_MEDIACODEC)
            return CreateMediaCodecSurfaceFrame(avFrame);
        return CreateVideoFrameFromAVFrame(avFrame);
    }

    /// <summary>DecodeAsync 的核心逻辑。</summary>
    /// <remarks>
    /// FFmpeg 标准 send/receive 循环：新包先经 BSF 转 Annex-B 入队 <c>_pendingConv</c>；
    /// 随后尽可能把队首包喂给解码器（<c>avcodec_send_packet</c> 返回 EAGAIN 表示输入缓冲满，
    /// 此时停发并留在队中，下次调用再重试——绝不放包）；最后排空解码器已产出的所有帧
    /// （HEVC B 帧重排可能一包多帧，多出的帧入 <c>_pendingFrames</c> 留待下次返回，绝不丢弃）。
    /// 这样无论首播还是重播，都不会因 EAGAIN 丢弃关键参考帧，解决 HEVC 重播 RPS 崩。
    /// </remarks>
    private unsafe VideoFrame? DecodeCore(MediaPacket packet)
    {
        AVCodecContext* ctx = (AVCodecContext*)_codecContextHandle!.DangerousGetHandle();

        // 1) 转换新包并入队
        AVPacket* conv = ConvertToConv(packet);
        if (conv != null)
            _pendingConv.Enqueue((IntPtr)conv);

        AVFrame* avFrame = FF.av_frame_alloc();
        if (avFrame == null)
            throw new InvalidOperationException("av_frame_alloc 失败");
        try
        {
            // 2) 发送所有解码器能接受的包（EAGAIN 即停，留待重试）
            while (_pendingConv.Count > 0)
            {
                AVPacket* p = (AVPacket*)_pendingConv.Peek();
                int ret = FF.avcodec_send_packet(ctx, p);
                if (ret == 0)
                {
                    _pendingConv.Dequeue();
                    FF.av_packet_free(&p);
                }
                else if (ret == EAGAIN)
                {
                    break; // 解码器输入满，稍后重试（包仍保留在队中）
                }
                else if (ret == FF.AVERROR_EOF)
                {
                    _pendingConv.Dequeue();
                    FF.av_packet_free(&p);
                }
                else
                {
                    _logger.LogWarning("avcodec_send_packet 返回 {Ret}: {Error}", ret, GetErrorString(ret));
                    _pendingConv.Dequeue();
                    FF.av_packet_free(&p);
                }
            }

            // 3) 排空解码器已产出的所有帧，一律入队（保证 FIFO 帧序，绝不丢弃）
            while (true)
            {
                int ret = FF.avcodec_receive_frame(ctx, avFrame);
                if (ret == EAGAIN || ret == FF.AVERROR_EOF)
                    break;
                if (ret < 0)
                {
                    _logger.LogWarning("avcodec_receive_frame 返回 {Ret}: {Error}", ret, GetErrorString(ret));
                    break;
                }
                VideoFrame? f = MakeFrame(avFrame);
                if (f != null)
                {
                    // 复位后输出帧诊断：前 4 帧打印 pts，确证重播首帧已回到 ≈0。
                    if (_decodeDiagCount > 0)
                    {
                        --_decodeDiagCount;
                        try
                        {
                            _logger.LogInformation(
                                "[DECODE-DIAG] 复位后输出帧#{Seq}: pts={Pts} key={Key} (pts≈0 表示已正确回到起点)",
                                4 - _decodeDiagCount, f.Timestamp, f.KeyFrame);
                        }
                        catch { }
                    }
                    _pendingFrames.Enqueue(f);
                }
                FF.av_frame_unref(avFrame);
            }
        }
        finally
        {
            AVFrame* af = avFrame;
            FF.av_frame_free(&af);
        }

        // 4) 从队首取一帧返回（多余帧留待后续调用返回，帧序严格 FIFO）
        return _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;
    }

    /// <summary>FlushAsync 的核心逻辑：排空内部队列后发送 null packet 刷新解码器。</summary>
    /// <remarks>
    /// 调用顺序严格为「积压帧 → 待发包 → null packet → receive」：
    /// DecodeCore 的 EAGAIN 重试队列在 EOS 时可能仍有残包，必须先喂完再进入 draining，
    /// 否则尾部若干帧会被静默丢弃（表现为末段掉帧 / Ended 提前）。
    /// </remarks>
    private unsafe VideoFrame? FlushCore()
    {
        AVCodecContext* ctx = (AVCodecContext*)_codecContextHandle!.DangerousGetHandle();

        // 0) 先交出 DecodeCore 一包多帧时积压的帧（严格 FIFO）
        if (_pendingFrames.Count > 0)
            return _pendingFrames.Dequeue();

        // 1) 把仍挂在待发队列里的包尽力喂完；EAGAIN 表示解码器输入满 → 先去 receive 腾空间
        while (_pendingConv.Count > 0)
        {
            AVPacket* pending = (AVPacket*)_pendingConv.Peek();
            int sret = FF.avcodec_send_packet(ctx, pending);
            if (sret == EAGAIN)
                break;
            _pendingConv.Dequeue();
            FF.av_packet_free(&pending);
            if (sret < 0 && sret != FF.AVERROR_EOF)
                _logger.LogWarning("avcodec_send_packet(flush) 返回 {Ret}: {Error}", sret, GetErrorString(sret));
        }

        // 2) 待发队列排空后才进入 draining 模式（重复发 null packet 是安全的）
        if (_pendingConv.Count == 0)
            FF.avcodec_send_packet(ctx, null);

        int ret;
        AVFrame* avFrame = FF.av_frame_alloc();
        if (avFrame == null)
            throw new InvalidOperationException("av_frame_alloc 失败");
        try
        {
            ret = FF.avcodec_receive_frame(ctx, avFrame);
            if (ret < 0)
                return null;

            // Flush 时同样需检查 D3D11VA 硬解输出格式（与 DecodeCore 一致）
            // 同 DecodeCore：FFmpeg 8 产出 AV_PIX_FMT_D3D11，两种命名都接纳。
            var hwFmtFlush = (AVPixelFormat)avFrame->format;
            if (hwFmtFlush is AVPixelFormat.AV_PIX_FMT_D3D11VA_VLD or AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                return CreateHardwareFrameFromAVFrame(avFrame);
            }

            // Flush 时同样需检查 VAAPI 硬解输出格式（与 DecodeCore 一致）
            if (hwFmtFlush == AVPixelFormat.AV_PIX_FMT_VAAPI)
            {
                return CreateVaApiFrame(avFrame);
            }

            // Flush 时同样需检查 MediaCodec 表面输出格式（与 DecodeCore 一致）
            if ((AVPixelFormat)avFrame->format == AVPixelFormat.AV_PIX_FMT_MEDIACODEC)
            {
                return CreateMediaCodecSurfaceFrame(avFrame);
            }

            return CreateVideoFrameFromAVFrame(avFrame);
        }
        finally
        {
            AVFrame* p = avFrame;
            FF.av_frame_free(&p);
        }
    }

    /// <summary>
    /// 清空 send/receive 内部队列：释放未发送的包、归还未交出的帧。
    /// </summary>
    /// <remarks>
    /// 重播/seek 必调：上一轮残留的待发包属于旧时间线，若混入新流会造成参考帧错乱；
    /// 残留帧未归还帧池则会导致 D3D11VA 硬件帧池枯竭（画面冻结但音频前进）。
    /// </remarks>
    private unsafe void ClearPendingQueues()
    {
        while (_pendingConv.Count > 0)
        {
            AVPacket* p = (AVPacket*)_pendingConv.Dequeue();
            FF.av_packet_free(&p);
        }
        while (_pendingFrames.Count > 0)
        {
            _pendingFrames.Dequeue().Dispose();
        }
    }

    /// <inheritdoc/>
    public unsafe void Reset()
    {
        if (_codecContextHandle == null) return;

        // 先丢弃旧时间线的残留包/帧，避免混入新流造成参考帧错乱或 D3D11VA 硬件帧池枯竭。
        ClearPendingQueues();

        // 重播/seek 全量重建：D3D11VA/VP9 硬件解码器在流末 EOF 排干后，
        // 仅 avcodec_flush_buffers 无法让其重新对齐到新流首关键帧——它会把重播首帧当旧流续帧
        // 一路丢帧直到下一个真关键帧 → 视频冻结较长时间。
        // 成因：硬件解码器内部参考帧/序列状态在 EOF 后未干净复位。
        // 唯一稳妥修复=关闭旧 AVCodecContext 并完整重建（同 Initialize 路径，复用原 settings）。
        // 引用计数配对（不泄漏共享 D3D11 设备）：与 Dispose 同序——avcodec_free_context 先行
        //   （其内部完成 hwaccel uninit / hw_frames_ctx 释放 / extradata 释放，全程设备存活），
        //   随后 Dispose 旧 _hwDeviceCtx 归还自身引用销毁设备，最后 Initialize 重新 AddRef + 重建。
        //   旧解码器仍被在途帧(D3D11HardwareFrameResource 持有的 av_frame_clone 引用)保活的纹理，
        //   待那些帧归还池后由 ffmpeg 引用计数自动释放，安全。
        //   注意：avcodec_free_context 会 av_freep(&avctx->extradata)——其后必须清 _extradataBuffer，
        //   否则 Initialize→SetExtradata 的"先释放旧缓冲"对已释放指针二次 av_free（重播原生死亡）。
        try
        {
            _codecContextHandle.Dispose();
            _codecContextHandle = null;
            _initialized = false;
            _extradataBuffer = IntPtr.Zero;
            _hwDeviceCtx?.Dispose();
            _hwDeviceCtx = null;
            if (_bsfContext != null)
            {
                AVBSFContext* local = _bsfContext;
                _bsfContext = null;
                FF.av_bsf_free(&local);
            }

            // 复用原 settings 完整重建（重新分配 ctx + 挂共享 D3D11 设备 + 重建 BSF/extradata + open）
            Initialize(_lastCodec, _lastSettings!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "视频解码器重播全量重建失败");
            throw;
        }

        _decodeDiagCount = 4; // 诊断：下一个 DecodeCore 输出前 4 帧打印 pts
        _logger.LogDebug("视频解码器已全量重建(重播/seek 复位)");
    }

    /// <inheritdoc/>
    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 收尾帧路径统计（零拷贝验证计数）。
        // 判据：硬解激活(IsHardwareAccelerated) 且 GPU 零拷贝帧 > 0 才算「硬件没白用」；
        // 若硬解激活但 GPU=0，说明帧被下载回系统内存 —— 属于「半硬解」缺陷，必须暴露而非静默。
        if (!_frameSummaryLogged)
        {
            _frameSummaryLogged = true;
            if (_gpuZeroCopyFrames > 0 || _cpuFallbackFrames > 0)
                _logger.LogInformation(
                    "[FFMPEG-FRAMEPATH] 解码帧路径统计：GPU零拷贝={Gpu} 帧 / CPU拷贝={Cpu} 帧 | 硬解激活={Hw} | 零拷贝生效={Verdict}",
                    _gpuZeroCopyFrames, _cpuFallbackFrames, IsHardwareAccelerated,
                    IsHardwareAccelerated && _gpuZeroCopyFrames > 0 ? "是" : "否(全程 CPU 帧)");
        }

        // 原生死亡定位踪迹（stderr 直写不丢行，[demux-trace] 同款）：释放链任一原生调用
        // 崩溃时，最后一条踪迹即崩溃子阶段（硬解路径涉及 hw_device_ctx/视频处理器/共享设备引用）。
        Console.Error.WriteLine("[DECODER-DISPOSE] 开始");
        ClearPendingQueues();
        Console.Error.WriteLine("[DECODER-DISPOSE] 待决队列已清");
        // 释放 NV12→RGBA 转换器（仅释放其内部 QI 的视频设备/上下文与处理器；共享设备包装不 Dispose）。
        _nv12ToRgbaConverter?.Dispose();
        _nv12ToRgbaConverter = null;
        Console.Error.WriteLine("[DECODER-DISPOSE] NV12转换器已释放");
        // 释放 ffmpeg 自有 D3D11 设备（与 Initialize 中创建配对）。转换器包装不 Dispose 该设备，
        // 故此处必须显式释放，否则 D3D11 设备泄漏。渲染器共享设备由渲染器工厂持有，此处不 Dispose。
        _vaOwnedContext?.Dispose();
        _vaOwnedContext = null;
        _vaOwnedDevice?.Dispose();
        _vaOwnedDevice = null;
        _vaDeviceHandle = IntPtr.Zero;
        _vaContextHandle = IntPtr.Zero;
        _vaDisplay = nint.Zero;
        Console.Error.WriteLine("[DECODER-DISPOSE] 自有D3D11设备已释放");
        // 先释放比特流过滤器（其内部 par_in->extradata 由 ffmpeg 分配器管理）
        if (_bsfContext != null)
        {
            AVBSFContext* local = _bsfContext;
            FF.av_bsf_free(&local);
            _bsfContext = null;
        }
        // extradata 缓冲已改由 av_malloc 分配并挂入 ctx->extradata，avcodec_free_context 的
        // av_freep(&avctx->extradata) 会释放——此处仅清引用，绝不可再释放（双重释放 → 退出 AV）。
        _extradataBuffer = IntPtr.Zero;
        Console.Error.WriteLine("[DECODER-DISPOSE] bsf/extradata 已释放");
        // avcodec_free_context 先行：其内部完成 hwaccel uninit、hw_frames_ctx（纹理池）释放与
        // ctx->hw_device_ctx 引用归还，全程要求设备仍存活。自身持有的 hw_device_ctx 引用必须
        // 最后归还，设备在全部原生使用结束后才经本次 unref 销毁——先归还自身引用会让设备
        // 销毁时机交叠进 avcodec_free_context 的内部释放链（D3D11VA 退出路径原生死亡定位点）。
        _codecContextHandle?.Dispose();
        _codecContextHandle = null;
        _initialized = false;
        Console.Error.WriteLine("[DECODER-DISPOSE] codec context 已释放");
        _hwDeviceCtx?.Dispose();
        _hwDeviceCtx = null;
        Console.Error.WriteLine("[DECODER-DISPOSE] hw_device_ctx 已释放（Dispose 完成）");
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：原生释放为快速同步操作。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

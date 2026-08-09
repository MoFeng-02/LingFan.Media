using System.Runtime.InteropServices;
using System.Text;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Decoders;

/// <summary>
/// 基于 FFmpeg libavcodec 的 <see cref="ISubtitleDecoder"/> 实现。
/// </summary>
/// <remarks>
/// <para>支持文本字幕（SRT / ASS / WebVTT），位图字幕（PGS / VobSub）延后。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <c>Task.CompletedTask</c>（无 I/O）。</item>
/// <item><see cref="DecodeAsync"/>：热路径异步，<c>ValueTask.FromResult</c> 同步完成。</item>
/// <item><see cref="FlushAsync"/>：热路径异步，同上。</item>
/// <item><see cref="Reset"/>：同步。</item>
/// </list>
/// <para><b>内存安全</b>：AVSubtitle 文本拷贝到托管 string 后才 avsubtitle_free，不持有 FFmpeg 内部缓冲。</para>
/// <para>无参数化 <c>Initialize</c>——编解码信息由工厂 <c>Create(MediaTrack)</c> 时通过
/// <see cref="BindStream"/> 预置（与架构 <c>ISubtitleDecoder</c> 约定一致）。</para>
/// </remarks>
internal sealed class FFmpegSubtitleDecoder : ISubtitleDecoder
{
    private readonly ILogger<FFmpegSubtitleDecoder> _logger;
    private SafeAVCodecContextHandle? _codecContextHandle;
    private bool _disposed;
    private bool _initialized;
    private AVCodecID _boundCodecId;

    /// <summary>FFmpeg EAGAIN 错误码（跨平台）。必须用 ffmpeg.AVERROR(ffmpeg.EAGAIN) 计算，
    /// 禁止硬编码 -11（Windows 正确，但 macOS/iOS 的 EAGAIN=35，会误判"需要更多数据"为解码失败）。</summary>
    private static readonly int EAGAIN = ffmpeg.AVERROR(ffmpeg.EAGAIN);

    /// <summary>
    /// 初始化 <see cref="FFmpegSubtitleDecoder"/> 的新实例。
    /// </summary>
    public FFmpegSubtitleDecoder(ILogger<FFmpegSubtitleDecoder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public SubtitleCodec SubtitleCodec { get; private set; } = SubtitleCodec.Unknown;

    /// <summary>
    /// 绑定字幕流（工厂内部辅助方法，不计入 <see cref="ISubtitleDecoder"/> 公共接口）。
    /// </summary>
    /// <param name="track">字幕轨道元数据（含 codec 信息）。</param>
    internal void BindStream(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        SubtitleCodec = track.SubtitleCodec ?? SubtitleCodec.Unknown;
        _boundCodecId = MapSubtitleCodecToFFmpeg(SubtitleCodec);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：avcodec_alloc_context3 + avcodec_open2 是同步 FFmpeg 调用，无 I/O。
    /// 返回 <see cref="Task.CompletedTask"/>。
    /// </remarks>
    public unsafe Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
            throw new InvalidOperationException("字幕解码器已初始化");
        if (_boundCodecId == default)
            throw new InvalidOperationException("字幕解码器未绑定流（请通过工厂 Create 绑定）");

        AVCodec* avCodec = ffmpeg.avcodec_find_decoder(_boundCodecId);
        if (avCodec == null)
            throw new NotSupportedException($"FFmpeg 未找到字幕解码器: {SubtitleCodec} (codec_id={_boundCodecId})");

        AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(avCodec);
        if (ctx == null)
            throw new InvalidOperationException("avcodec_alloc_context3 失败");

        _codecContextHandle = new SafeAVCodecContextHandle((IntPtr)ctx);

        int ret = ffmpeg.avcodec_open2(ctx, avCodec, null);
        if (ret < 0)
        {
            _codecContextHandle.Dispose();
            _codecContextHandle = null;
            throw new InvalidOperationException($"avcodec_open2 失败: {GetErrorString(ret)} (code={ret})");
        }

        _initialized = true;
        _logger.LogInformation("字幕解码器初始化: {Codec}", SubtitleCodec);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>热路径异步：avcodec_decode_subtitle2 是 CPU 密集型同步操作，<see cref="ValueTask.FromResult{TResult}"/> 同步完成。</remarks>
    public unsafe ValueTask<SubtitleFrame?> DecodeAsync(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new InvalidOperationException("字幕解码器尚未初始化");

        SubtitleFrame? frame = DecodeCore(packet);
        return ValueTask.FromResult(frame);
    }

    /// <summary>DecodeAsync 的核心逻辑。</summary>
    private unsafe SubtitleFrame? DecodeCore(MediaPacket packet)
    {
        AVCodecContext* ctx = (AVCodecContext*)_codecContextHandle!.DangerousGetHandle();

        AVPacket* pkt = ffmpeg.av_packet_alloc();
        if (pkt == null)
            throw new InvalidOperationException("av_packet_alloc 失败");

        try
        {
            // 使用 av_new_packet 分配（含 AV_INPUT_BUFFER_PADDING_SIZE 填充）
            int allocRet = ffmpeg.av_new_packet(pkt, packet.Data.Length);
            if (allocRet < 0)
                throw new InvalidOperationException($"av_new_packet 失败: {GetErrorString(allocRet)} (code={allocRet})");
            packet.Data.Span.CopyTo(new Span<byte>(pkt->data, packet.Data.Length));
            pkt->pts = (long)(packet.Timestamp.TotalSeconds * 1000); // 字幕 PTS 通常为毫秒

            // avsubtitle_free 必须覆盖所有路径（含错误路径），防止解码器部分初始化
            // AVSubtitle 后失败导致 rects 泄漏。零初始化的 AVSubtitle 调用 free 是安全 no-op。
            AVSubtitle avSub = default;
            try
            {
                int gotSub = 0;
                int ret = ffmpeg.avcodec_decode_subtitle2(ctx, &avSub, &gotSub, pkt);

                if (ret < 0 || gotSub == 0)
                    return null;

                return CreateSubtitleFrameFromAVSubtitle(&avSub);
            }
            finally
            {
                ffmpeg.avsubtitle_free(&avSub);
            }
        }
        finally
        {
            // av_packet_unref 释放 av_new_packet 分配的内部缓冲（通过 pkt->buf 引用计数）
            ffmpeg.av_packet_unref(pkt);
            AVPacket* p = pkt;
            ffmpeg.av_packet_free(&p);
        }
    }

    /// <inheritdoc/>
    public ValueTask<SubtitleFrame?> FlushAsync()
    {
        // 字幕解码无内部缓冲延迟，Flush 返回 null
        return ValueTask.FromResult<SubtitleFrame?>(null);
    }

    /// <inheritdoc/>
    public unsafe void Reset()
    {
        if (!_initialized || _codecContextHandle == null) return;
        AVCodecContext* ctx = (AVCodecContext*)_codecContextHandle.DangerousGetHandle();
        ffmpeg.avcodec_flush_buffers(ctx);
        _logger.LogDebug("字幕解码器已重置");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _codecContextHandle?.Dispose();
        _codecContextHandle = null;
        _initialized = false;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ── 辅助方法 ──

    /// <summary>从 AVSubtitle 创建 SubtitleFrame（文本字幕路径）。</summary>
    private static unsafe SubtitleFrame? CreateSubtitleFrameFromAVSubtitle(AVSubtitle* avSub)
    {
        // 拼接各 rect 文本
        var sb = new StringBuilder();
        SubtitleStyle? style = null;

        for (uint i = 0; i < avSub->num_rects; i++)
        {
            AVSubtitleRect* rect = avSub->rects[i];
            if (rect->type == AVSubtitleType.SUBTITLE_TEXT)
            {
                if (rect->text != null)
                    sb.AppendLine(Marshal.PtrToStringUTF8((IntPtr)rect->text));
            }
            else if (rect->type == AVSubtitleType.SUBTITLE_ASS)
            {
                // ASS 格式：解析 Dialogue 行，提取文本部分
                if (rect->ass != null)
                {
                    string assLine = Marshal.PtrToStringUTF8((IntPtr)rect->ass) ?? string.Empty;
                    string assText = ExtractAssText(assLine);
                    if (!string.IsNullOrEmpty(assText))
                        sb.AppendLine(assText);
                }
            }
            // 位图字幕（SUBTITLE_BITMAP）不实现
        }

        string text = sb.ToString().TrimEnd();
        if (string.IsNullOrEmpty(text))
            return null;

        // 时间信息（FFmpeg 毫秒 → TimeSpan）
        TimeSpan start = TimeSpan.FromMilliseconds(avSub->start_display_time);
        TimeSpan end = TimeSpan.FromMilliseconds(avSub->end_display_time);

        return new SubtitleFrame(text, start, end, style);
    }

    /// <summary>从 ASS Dialogue 行提取文本内容。</summary>
    /// <remarks>
    /// ASS Dialogue 格式：Dialogue: Layer,Start,End,Style,Name,MarginL,MarginR,MarginV,Effect,Text
    /// 前 8 个逗号分隔字段是元数据，第 9 个字段开始是文本（文本本身可能包含逗号）。
    /// </remarks>
    private static string ExtractAssText(string assLine)
    {
        // 查找第 9 个逗号的位置
        int commaCount = 0;
        int textStart = 0;
        for (int i = 0; i < assLine.Length; i++)
        {
            if (assLine[i] == ',')
            {
                commaCount++;
                if (commaCount == 8)
                {
                    textStart = i + 1;
                    break;
                }
            }
        }

        if (textStart == 0)
            return assLine; // 无法解析，返回原始内容

        string text = assLine[textStart..];

        // 移除 ASS 样式覆盖标记 {\...}
        var result = new StringBuilder(text.Length);
        int braceDepth = 0;
        foreach (char c in text)
        {
            if (c == '{') { braceDepth++; continue; }
            if (c == '}') { if (braceDepth > 0) braceDepth--; continue; }
            if (braceDepth == 0)
                result.Append(c);
        }

        // ASS 硬换行符 \N → 换行
        return result.ToString().Replace("\\N", "\n").Replace("\\n", "\n");
    }

    private static AVCodecID MapSubtitleCodecToFFmpeg(SubtitleCodec codec) => codec switch
    {
        SubtitleCodec.SRT => AVCodecID.AV_CODEC_ID_SUBRIP,
        SubtitleCodec.ASS => AVCodecID.AV_CODEC_ID_ASS,
        SubtitleCodec.WebVTT => AVCodecID.AV_CODEC_ID_WEBVTT,
        SubtitleCodec.PGS => AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE,
        SubtitleCodec.VobSub => AVCodecID.AV_CODEC_ID_DVD_SUBTITLE,
        _ => throw new NotSupportedException(
            $"不支持的字幕编解码器: {codec}（PGS/VobSub 位图字幕 不支持解码）")
    };

    private static string GetErrorString(int errorCode)
    {
        unsafe
        {
            byte* buf = stackalloc byte[ffmpeg.AV_ERROR_MAX_STRING_SIZE];
            ffmpeg.av_strerror(errorCode, buf, ffmpeg.AV_ERROR_MAX_STRING_SIZE);
            return Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"error code {errorCode}";
        }
    }
}

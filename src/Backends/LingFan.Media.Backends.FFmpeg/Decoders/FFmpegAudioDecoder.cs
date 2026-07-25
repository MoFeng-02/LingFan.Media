using System.Runtime.InteropServices;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Decoders;

/// <summary>
/// 基于 FFmpeg libavcodec 的 <see cref="IAudioDecoder"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>（与 <see cref="FFmpegVideoDecoder"/> 一致）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <c>Task.CompletedTask</c>（无 I/O）。</item>
/// <item><see cref="Initialize"/>：同步，avcodec_find_decoder + alloc + open。</item>
/// <item><see cref="DecodeAsync"/>：热路径异步，<c>ValueTask.FromResult</c> 同步完成。</item>
/// <item><see cref="FlushAsync"/>：热路径异步，同上。</item>
/// <item><see cref="Reset"/>：同步，avcodec_flush_buffers。</item>
/// </list>
/// <para><b>V1 限制</b>：不做重采样（SwrContext），解码输出使用源格式。
/// 格式转换由 AudioPipeline 消费方处理。未来 V2 可接入 SwrContext。</para>
/// <para><b>内存安全</b>：PCM 数据通过独立 byte[] 拷贝（不引用 FFmpeg 内部帧内存），
/// AudioFrame 以 <see cref="ReadOnlyMemory{T}"/> 封装，消费方用
/// <c>MemoryMarshal.Cast&lt;byte, float&gt;</c> 零拷贝访问。</para>
/// </remarks>
internal sealed class FFmpegAudioDecoder : IAudioDecoder
{
    private readonly ILogger<FFmpegAudioDecoder> _logger;
    private SafeAVCodecContextHandle? _codecContextHandle;
    private bool _disposed;
    private bool _initialized;

    /// <summary>FFmpeg EAGAIN 错误码（POSIX EAGAIN = 11）。</summary>
    private const int EAGAIN = -11;

    /// <summary>
    /// 初始化 <see cref="FFmpegAudioDecoder"/> 的新实例。
    /// </summary>
    public FFmpegAudioDecoder(ILogger<FFmpegAudioDecoder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public AudioCodec Codec { get; private set; } = AudioCodec.Unknown;

    /// <inheritdoc/>
    /// <remarks>接口契约：无 I/O，返回 <see cref="Task.CompletedTask"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public unsafe void Initialize(AudioCodec codec, AudioSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            throw new InvalidOperationException("音频解码器已初始化");

        Codec = codec;
        AVCodecID codecId = MapAudioCodecToFFmpeg(codec);

        AVCodec* avCodec = ffmpeg.avcodec_find_decoder(codecId);
        if (avCodec == null)
            throw new NotSupportedException($"FFmpeg 未找到音频解码器: {codec} (codec_id={codecId})");

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

        // V1: 不做重采样，解码输出使用源格式
        _initialized = true;
        _logger.LogInformation("音频解码器初始化: {Codec}, {Rate}Hz/{Ch}ch/{Fmt}",
            codec, ctx->sample_rate, ctx->ch_layout.nb_channels, ctx->sample_fmt);
    }

    /// <inheritdoc/>
    /// <remarks>热路径异步：CPU 密集型同步操作，<see cref="ValueTask.FromResult{TResult}"/> 同步完成。</remarks>
    public unsafe ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new InvalidOperationException("音频解码器尚未初始化");

        AudioFrame? frame = DecodeCore(packet);
        return ValueTask.FromResult(frame);
    }

    /// <summary>DecodeAsync 的核心逻辑。</summary>
    private unsafe AudioFrame? DecodeCore(MediaPacket packet)
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
            // 防御 time_base.num==0 导致 av_q2d 返回 0 → 除以零产生 Infinity/NaN
            double timeBase = ffmpeg.av_q2d(ctx->time_base);
            pkt->pts = timeBase > 0
                ? (long)(packet.Timestamp.TotalSeconds / timeBase)
                : ffmpeg.AV_NOPTS_VALUE;

            int ret = ffmpeg.avcodec_send_packet(ctx, pkt);
            if (ret < 0 && ret != EAGAIN)
            {
                if (ret != ffmpeg.AVERROR_EOF)
                    _logger.LogWarning("avcodec_send_packet 返回 {Ret}: {Error}", ret, GetErrorString(ret));
                return null;
            }

            AVFrame* avFrame = ffmpeg.av_frame_alloc();
            if (avFrame == null)
                throw new InvalidOperationException("av_frame_alloc 失败");
            try
            {
                ret = ffmpeg.avcodec_receive_frame(ctx, avFrame);
                if (ret == EAGAIN || ret == ffmpeg.AVERROR_EOF)
                    return null;
                if (ret < 0)
                {
                    _logger.LogWarning("avcodec_receive_frame 返回 {Ret}: {Error}", ret, GetErrorString(ret));
                    return null;
                }

                return CreateAudioFrameFromAVFrame(avFrame, ctx);
            }
            finally
            {
                AVFrame* p = avFrame;
                ffmpeg.av_frame_free(&p);
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
    public unsafe ValueTask<AudioFrame?> FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new InvalidOperationException("音频解码器尚未初始化");

        AudioFrame? frame = FlushCore();
        return ValueTask.FromResult(frame);
    }

    /// <summary>FlushAsync 的核心逻辑。</summary>
    private unsafe AudioFrame? FlushCore()
    {
        AVCodecContext* ctx = (AVCodecContext*)_codecContextHandle!.DangerousGetHandle();

        int ret = ffmpeg.avcodec_send_packet(ctx, null);
        if (ret < 0)
            return null;

        AVFrame* avFrame = ffmpeg.av_frame_alloc();
        if (avFrame == null)
            throw new InvalidOperationException("av_frame_alloc 失败");
        try
        {
            ret = ffmpeg.avcodec_receive_frame(ctx, avFrame);
            if (ret < 0)
                return null;
            return CreateAudioFrameFromAVFrame(avFrame, ctx);
        }
        finally
        {
            AVFrame* p = avFrame;
            ffmpeg.av_frame_free(&p);
        }
    }

    /// <inheritdoc/>
    public unsafe void Reset()
    {
        if (!_initialized || _codecContextHandle == null) return;
        AVCodecContext* ctx = (AVCodecContext*)_codecContextHandle.DangerousGetHandle();
        ffmpeg.avcodec_flush_buffers(ctx);
        _logger.LogDebug("音频解码器已重置");
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

    /// <summary>从 AVFrame 创建 AudioFrame（V1 无重采样，直接拷贝 PCM 数据）。</summary>
    private static unsafe AudioFrame CreateAudioFrameFromAVFrame(AVFrame* avFrame, AVCodecContext* ctx)
    {
        TimeSpan timestamp = avFrame->pts != ffmpeg.AV_NOPTS_VALUE
            ? TimeSpan.FromTicks((long)(avFrame->pts * ffmpeg.av_q2d(ctx->time_base) * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;

        int frameCount = avFrame->nb_samples;
        int channels = ctx->ch_layout.nb_channels;
        int sampleRate = ctx->sample_rate;
        AVSampleFormat sampleFmt = ctx->sample_fmt;
        SampleFormat outFormat = MapSampleFormatFromFFmpeg(sampleFmt);

        // 计算数据大小并拷贝
        bool isPlanar = ffmpeg.av_sample_fmt_is_planar(sampleFmt) != 0;
        int bytesPerSample = ffmpeg.av_get_bytes_per_sample(sampleFmt);
        if (bytesPerSample <= 0)
            throw new InvalidOperationException($"无效的音频采样格式: {sampleFmt}");
        int planeSize = frameCount * bytesPerSample; // 每个平面的数据大小
        int dataSize;
        byte[] buffer;

        if (!isPlanar)
        {
            // 交错格式（如 S16, FLT）：data[0] 包含所有数据
            dataSize = avFrame->linesize[0];
            if (dataSize < 0)
                throw new InvalidOperationException($"无效的音频行大小: {dataSize}");
            buffer = new byte[dataSize];
            Marshal.Copy((IntPtr)avFrame->data[0], buffer, 0, dataSize);
        }
        else
        {
            // 平面格式（如 FLTP）：拼接各平面数据
            dataSize = planeSize * channels;
            if (dataSize < 0)
                throw new InvalidOperationException($"无效的音频数据大小: {dataSize}（frameCount={frameCount}, channels={channels}）");
            buffer = new byte[dataSize];
            for (int ch = 0; ch < channels; ch++)
            {
                if (avFrame->extended_data[ch] == null) continue;
                Marshal.Copy((IntPtr)avFrame->extended_data[ch], buffer, ch * planeSize, planeSize);
            }
        }

        TimeSpan duration = sampleRate > 0
            ? TimeSpan.FromTicks((long)frameCount * TimeSpan.TicksPerSecond / sampleRate)
            : TimeSpan.Zero;

        return new AudioFrame(buffer, sampleRate, channels, outFormat, timestamp, duration, frameCount);
    }

    private static AVCodecID MapAudioCodecToFFmpeg(AudioCodec codec) => codec switch
    {
        AudioCodec.AAC => AVCodecID.AV_CODEC_ID_AAC,
        AudioCodec.MP3 => AVCodecID.AV_CODEC_ID_MP3,
        AudioCodec.Opus => AVCodecID.AV_CODEC_ID_OPUS,
        AudioCodec.FLAC => AVCodecID.AV_CODEC_ID_FLAC,
        AudioCodec.Vorbis => AVCodecID.AV_CODEC_ID_VORBIS,
        AudioCodec.PCM => AVCodecID.AV_CODEC_ID_PCM_S16LE,
        AudioCodec.AC3 => AVCodecID.AV_CODEC_ID_AC3,
        _ => throw new NotSupportedException($"不支持的音频编解码器: {codec}")
    };

    private static SampleFormat MapSampleFormatFromFFmpeg(AVSampleFormat fmt) => fmt switch
    {
        AVSampleFormat.AV_SAMPLE_FMT_S16 => SampleFormat.S16,
        AVSampleFormat.AV_SAMPLE_FMT_S16P => SampleFormat.S16,
        AVSampleFormat.AV_SAMPLE_FMT_S32 => SampleFormat.S32,
        AVSampleFormat.AV_SAMPLE_FMT_S32P => SampleFormat.S32,
        AVSampleFormat.AV_SAMPLE_FMT_FLT => SampleFormat.F32,
        AVSampleFormat.AV_SAMPLE_FMT_FLTP => SampleFormat.F32,
        _ => SampleFormat.S16
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

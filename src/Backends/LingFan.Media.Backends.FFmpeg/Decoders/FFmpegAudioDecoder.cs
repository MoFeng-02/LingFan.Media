using System.Runtime.InteropServices;
using LingFan.Media.Backends.FFmpeg.Interop;
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
/// <para><b>重采样（B11）</b>：当 AudioSettings 指定的目标采样率/声道数/采样格式
/// 与解码源不同时，在 Initialize 中创建 SwrContext 重采样上下文；
/// 解码帧经 swr_convert_frame 重采样为目标格式后再封装为 AudioFrame。
/// 若目标与源一致则不创建。重采样为纯原生同步操作，不引入异步。</para>
/// <para><b>内存安全</b>：PCM 数据通过独立 byte[] 拷贝（不引用 FFmpeg 内部帧内存），
/// AudioFrame 以 <see cref="ReadOnlyMemory{T}"/> 封装，消费方用
/// <c>MemoryMarshal.Cast&lt;byte, float&gt;</c> 零拷贝访问。</para>
/// </remarks>
internal sealed class FFmpegAudioDecoder : IAudioDecoder, IFramePoolAware<AudioFrame>
{
    private readonly ILogger<FFmpegAudioDecoder> _logger;
    private SafeAVCodecContextHandle? _codecContextHandle;
    private IFramePool<AudioFrame>? _framePool;
    private SafeSwrContextHandle? _swrContext;
    private IntPtr _extradataBuffer;          // ctx->extradata 原生缓冲（含 64B padding），本类拥有，Dispose 释放

    // 流时间基：解码帧 pts 以「流 time_base」为单位。demuxer 透传，用于建立 ctx->pkt_timebase 并做时间戳换算。
    // 解码后 ctx->time_base 常为 0，直接换算会使音频帧时间戳全 0（主时钟 SyncTo(0) 钉死、pos 不前进）。
    private Rational _timeBase;
    private double _tbSeconds;
    private AVSampleFormat _targetSampleFormat;
    private int _targetSampleRate;
    private int _targetChannels;
    private bool _disposed;

    /// <summary>解码器实际输出采样率（源采样率，或 B11 重采样目标采样率）。</summary>
    /// <remarks>Initialize 内赋值；供 MediaPlayer 初始化 WASAPI 设备率。</remarks>
    public int OutputSampleRate { get; private set; }

    /// <summary>解码器实际输出声道数（源声道数，或 B11 重采样目标声道数）。</summary>
    public int OutputChannels { get; private set; }
    private bool _initialized;

    /// <summary>FFmpeg EAGAIN 错误码（跨平台）。必须用 FF.AVERROR(FF.EAGAIN) 计算，
    /// 禁止硬编码 -11（Windows 正确，但 macOS/iOS 的 EAGAIN=35，会误判"需要更多数据"为解码失败）。</summary>
    private static readonly int EAGAIN = FF.AVERROR(FF.EAGAIN);

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

        AVCodec* avCodec = FF.avcodec_find_decoder(codecId);
        if (avCodec == null)
            throw new NotSupportedException($"FFmpeg 未找到音频解码器: {codec} (codec_id={codecId})");

        AVCodecContext* ctx = FF.avcodec_alloc_context3(avCodec);
        if (ctx == null)
            throw new InvalidOperationException("avcodec_alloc_context3 失败");

        // 建立流时间基：解码帧 pts 以流 time_base 为单位，须由调用方写入 ctx->pkt_timebase。
        // 解码后 ctx->time_base 常为 0，直接用其换算会使音频帧时间戳全 0（主时钟 SyncTo(0) 钉死、pos 不前进）。
        _timeBase = settings.TimeBase;
        _tbSeconds = _timeBase.ToDouble();
        if (_timeBase.Denominator > 0)
        {
            AVRational tb = ctx->pkt_timebase;
            tb.num = _timeBase.Numerator;
            tb.den = _timeBase.Denominator;
            ctx->pkt_timebase = tb;
        }

        _codecContextHandle = new SafeAVCodecContextHandle((IntPtr)ctx);

        // 应用编解码器私有配置（extradata）：AAC 在 MP4 中为裸流，需 AudioSpecificConfig 才能解码，
        // 否则 avcodec_send_packet 返回 Invalid data。
        ApplyCodecConfiguration(ctx, settings.CodecConfiguration);

        int ret = FF.avcodec_open2(ctx, avCodec, null);
        if (ret < 0)
        {
            _codecContextHandle.Dispose();
            _codecContextHandle = null;
            throw new InvalidOperationException($"avcodec_open2 失败: {GetErrorString(ret)} (code={ret})");
        }

        // 若目标采样率/声道数/采样格式与源不同，创建 SwrContext 重采样上下文。
        // 纯原生同步操作（swr_alloc_set_opts2 + swr_init），保持 Initialize 为 sync void，不引入异步。
        _targetSampleRate = settings.OutputSampleRate ?? ctx->sample_rate;
        _targetChannels = settings.OutputChannels ?? ctx->ch_layout.nb_channels;
        _targetSampleFormat = settings.OutputSampleFormat.HasValue
            ? MapTargetSampleFormat(settings.OutputSampleFormat.Value)
            : ctx->sample_fmt;

        // B11 输出率/声道数回写（供 MediaPlayer 初始化 WASAPI 设备率，确保与帧率一致）。
        OutputSampleRate = _targetSampleRate;
        OutputChannels = _targetChannels;

        bool needResample = _targetSampleFormat != ctx->sample_fmt
                            || _targetSampleRate != ctx->sample_rate
                            || _targetChannels != ctx->ch_layout.nb_channels;
        if (needResample)
        {
            AVChannelLayout outLayout;
            FF.av_channel_layout_default(&outLayout, _targetChannels);

            SwrContext* swr = null;
            SwrContext** pSwr = &swr;
            int sret = FF.swr_alloc_set_opts2(
                pSwr,
                &outLayout, _targetSampleFormat, _targetSampleRate,
                &ctx->ch_layout, ctx->sample_fmt, ctx->sample_rate,
                0, (void*)null);
            if (sret < 0)
            {
                throw new InvalidOperationException($"swr_alloc_set_opts2 失败: {GetErrorString(sret)} (code={sret})");
            }
            sret = FF.swr_init(swr);
            if (sret < 0)
            {
                var bad = new SafeSwrContextHandle((IntPtr)swr);
                bad.Dispose();
                throw new InvalidOperationException($"swr_init 失败: {GetErrorString(sret)} (code={sret})");
            }
            _swrContext = new SafeSwrContextHandle((IntPtr)swr);
        }

        _initialized = true;
        _logger.LogInformation("音频解码器初始化: {Codec}, {Rate}Hz/{Ch}ch/{Fmt}（重采样={Resample}）",
            codec, ctx->sample_rate, ctx->ch_layout.nb_channels, ctx->sample_fmt, _swrContext != null);
    }

    /// <summary>
    /// 将编解码器私有配置写入 <c>ctx->extradata</c>（含 64 字节零填充，符合 ffmpeg 要求）。
    /// 缓冲由本类以 <see cref="Marshal"/> 持有，<see cref="Dispose"/> 时释放。
    /// </summary>
    private unsafe void ApplyCodecConfiguration(AVCodecContext* ctx, ReadOnlyMemory<byte> cfg)
    {
        int size = cfg.Length;
        if (size <= 0)
            return;
        int padded = size + 64;
        IntPtr buf = Marshal.AllocHGlobal(padded);
        Span<byte> span = new((void*)buf, padded);
        cfg.Span.CopyTo(span);
        span[size..].Clear();
        _extradataBuffer = buf;
        ctx->extradata = (byte*)buf;
        ctx->extradata_size = size;
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

        AVPacket* pkt = FF.av_packet_alloc();
        if (pkt == null)
            throw new InvalidOperationException("av_packet_alloc 失败");

        try
        {
            // 使用 av_new_packet 分配（含 AV_INPUT_BUFFER_PADDING_SIZE 填充）
            int allocRet = FF.av_new_packet(pkt, packet.Data.Length);
            if (allocRet < 0)
                throw new InvalidOperationException($"av_new_packet 失败: {GetErrorString(allocRet)} (code={allocRet})");
            packet.Data.Span.CopyTo(new Span<byte>(pkt->data, packet.Data.Length));
            // 防御 time_base.num==0 导致 av_q2d 返回 0 → 除以零产生 Infinity/NaN
            double timeBase = _tbSeconds;
            pkt->pts = timeBase > 0
                ? (long)(packet.Timestamp.TotalSeconds / timeBase)
                : FF.AV_NOPTS_VALUE;

            int ret = FF.avcodec_send_packet(ctx, pkt);
            if (ret < 0 && ret != EAGAIN)
            {
                if (ret != FF.AVERROR_EOF)
                    _logger.LogWarning("avcodec_send_packet 返回 {Ret}: {Error}", ret, GetErrorString(ret));
                return null;
            }

            AVFrame* avFrame = FF.av_frame_alloc();
            if (avFrame == null)
                throw new InvalidOperationException("av_frame_alloc 失败");
            try
            {
                ret = FF.avcodec_receive_frame(ctx, avFrame);
                if (ret == EAGAIN || ret == FF.AVERROR_EOF)
                    return null;
                if (ret < 0)
                {
                    _logger.LogWarning("avcodec_receive_frame 返回 {Ret}: {Error}", ret, GetErrorString(ret));
                    return null;
                }

                return ReceiveToAudioFrame(avFrame, ctx);
            }
            finally
            {
                AVFrame* p = avFrame;
                FF.av_frame_free(&p);
            }
        }
        finally
        {
            // av_packet_unref 释放 av_new_packet 分配的内部缓冲（通过 pkt->buf 引用计数）
            FF.av_packet_unref(pkt);
            AVPacket* p = pkt;
            FF.av_packet_free(&p);
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

        int ret = FF.avcodec_send_packet(ctx, null);
        if (ret < 0)
            return null;

        AVFrame* avFrame = FF.av_frame_alloc();
        if (avFrame == null)
            throw new InvalidOperationException("av_frame_alloc 失败");
        try
        {
            ret = FF.avcodec_receive_frame(ctx, avFrame);
            if (ret < 0)
                return null;
            return ReceiveToAudioFrame(avFrame, ctx);
        }
        finally
        {
            AVFrame* p = avFrame;
            FF.av_frame_free(&p);
        }
    }

    /// <inheritdoc/>
    public void SetFramePool(IFramePool<AudioFrame>? pool)
    {
        _framePool = pool;
    }

    /// <inheritdoc/>
    public unsafe void Reset()
    {
        if (!_initialized || _codecContextHandle == null) return;
        AVCodecContext* ctx = (AVCodecContext*)_codecContextHandle.DangerousGetHandle();
        FF.avcodec_flush_buffers(ctx);
        _logger.LogDebug("音频解码器已重置");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _swrContext?.Dispose();
        _swrContext = null;
        if (_extradataBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_extradataBuffer);
            _extradataBuffer = IntPtr.Zero;
        }
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

    /// <summary>从 AVFrame 创建 AudioFrame。</summary>
    /// <remarks>
    /// <para>若 _framePool 可用，从池中 Rent 帧壳并调用 Reset 填充数据，复用 AudioFrame 实例减少 GC。</para>
    /// <para>交错格式（S16/FLT 等）data[0] 即完整 PCM——
    /// av_frame_clone 引用计数共享原生 buffer，AudioFrame 直接映射 data[0]（长度语义与拷贝路径
    /// 一致：linesize[0]，消费方 WasapiOutput 已按 expectedDataSize 截取）。
    /// 平面格式（FLTP 等）需拼接多平面为交错内存布局，保持既有拷贝路径。</para>
    /// </remarks>
    private unsafe AudioFrame CreateAudioFrameFromAVFrame(AVFrame* avFrame, AVCodecContext* ctx)
    {
        TimeSpan timestamp = avFrame->pts != FF.AV_NOPTS_VALUE
            ? TimeSpan.FromTicks((long)(avFrame->pts * _tbSeconds * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;

        int frameCount = avFrame->nb_samples;
        int channels = avFrame->ch_layout.nb_channels;
        int sampleRate = avFrame->sample_rate;
        AVSampleFormat sampleFmt = (AVSampleFormat)avFrame->format;

        // AAC 等解码器在 avcodec_open2 后 ctx->sample_rate 可能仍为 0（采样率仅在首帧解出后可知）。
        // 用首帧 avFrame->sample_rate / 声道数回填 OutputSampleRate/OutputChannels，使解码器向外导出正确采样率
        // （供 WASAPI 等设备打开、NoOp 实时背压等；Initialize 时刻读到的 0 是 ffmpeg 延迟填充所致）。
        if (OutputSampleRate <= 0 && sampleRate > 0) OutputSampleRate = sampleRate;
        if (OutputChannels <= 0 && channels > 0) OutputChannels = channels;

        SampleFormat outFormat = MapSampleFormatFromFFmpeg(sampleFmt);

        bool isPlanar = FF.av_sample_fmt_is_planar(sampleFmt) != 0;
        int bytesPerSample = FF.av_get_bytes_per_sample(sampleFmt);
        if (bytesPerSample <= 0)
            throw new InvalidOperationException($"无效的音频采样格式: {sampleFmt}");
        int planeSize = frameCount * bytesPerSample; // 每个平面的数据大小

        ReadOnlyMemory<byte> data;
        IDisposable? dataOwner = null;

        if (!isPlanar)
        {
            // 交错格式（如 S16, FLT）：data[0] 包含所有数据
            int dataSize = avFrame->linesize[0];
            if (dataSize < 0)
                throw new InvalidOperationException($"无效的音频行大小: {dataSize}");

            // 引用计数共享原生 buffer；克隆失败（非引用计数帧/OOM）回退拷贝
            AVFrame* clone = FF.av_frame_clone(avFrame);
            if (clone != null && clone->data[0] != IntPtr.Zero)
            {
                var owner = new SafeAVFrameHandle((IntPtr)clone);
                data = new NativeBufferMemoryManager((IntPtr)clone->data[0], dataSize).Memory;
                dataOwner = owner;
            }
            else
            {
                if (clone != null)
                {
                    AVFrame* p = clone;
                    FF.av_frame_free(&p);
                }
                var buffer = new byte[dataSize];
                Marshal.Copy((IntPtr)avFrame->data[0], buffer, 0, dataSize);
                data = buffer;
            }
        }
        else
        {
            // 平面格式（如 FLTP）：必须**逐样本交错**写入，绝不能按平面块拼接。
            // 下游（WASAPI/无头音频）一律按交错语义解释 SampleFormat；若此处留平面块布局
            // [L0..Ln][R0..Rn]，会被当成 [L0 R0 L1 R1…] 解释 → 每样本前半左声道、后半右声道，
            // 周期性畸变 → 典型「电音」。原始注释声称"拼接多平面为交错内存布局"但实现从未交错。
            int dataSize = planeSize * channels;
            if (dataSize < 0)
                throw new InvalidOperationException($"无效的音频数据大小: {dataSize}（frameCount={frameCount}, channels={channels}）");
            var buffer = new byte[dataSize];
            fixed (byte* dstBase = buffer)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    if (avFrame->extended_data[ch] == null) continue;
                    byte* srcPlane = avFrame->extended_data[ch];
                    for (int i = 0; i < frameCount; i++)
                    {
                        byte* src = srcPlane + (long)i * bytesPerSample;
                        byte* dst = dstBase + (long)(i * channels + ch) * bytesPerSample;
                        for (int b = 0; b < bytesPerSample; b++)
                            dst[b] = src[b];
                    }
                }
            }
            data = buffer;
        }

        TimeSpan duration = sampleRate > 0
            ? TimeSpan.FromTicks((long)frameCount * TimeSpan.TicksPerSecond / sampleRate)
            : TimeSpan.Zero;

        // 从池中 Rent 帧壳并 Reset 填充数据（Reset 会释放旧的零拷贝所有者）
        var frame = _framePool?.Rent() ?? new AudioFrame();
        frame.Reset(data, sampleRate, channels, outFormat, timestamp, duration, frameCount, dataOwner);
        return frame;
    }

    // ── 重采样 ──

    /// <summary>将已解码的源格式 AVFrame 重采样为目标格式（如需要）。</summary>
    /// <remarks>纯原生同步操作（swr_convert_frame），不引入异步。</remarks>
    private unsafe AVFrame* ConvertWithSwr(AVFrame* inFrame)
    {
        AVFrame* outFrame = FF.av_frame_alloc();
        if (outFrame == null)
            throw new InvalidOperationException("av_frame_alloc 失败（重采样输出）");
        try
        {
            outFrame->format = (int)_targetSampleFormat;
            outFrame->sample_rate = _targetSampleRate;
            FF.av_channel_layout_default(&outFrame->ch_layout, _targetChannels);

            SwrContext* swr = (SwrContext*)_swrContext!.DangerousGetHandle();
            int outSamples = FF.swr_get_out_samples(swr, inFrame->nb_samples);
            outFrame->nb_samples = outSamples > 0 ? outSamples : inFrame->nb_samples;

            int ret = FF.av_frame_get_buffer(outFrame, 0);
            if (ret < 0)
                throw new InvalidOperationException($"av_frame_get_buffer 失败: {GetErrorString(ret)} (code={ret})");

            ret = FF.swr_convert_frame(swr, outFrame, inFrame);
            if (ret < 0)
                throw new InvalidOperationException($"swr_convert_frame 失败: {GetErrorString(ret)} (code={ret})");
            return outFrame;
        }
        catch
        {
            AVFrame* p = outFrame;
            FF.av_frame_free(&p);
            throw;
        }
    }

    /// <summary>将 AVFrame 封装为 AudioFrame，必要时先经 SwrContext 重采样。</summary>
    /// <remarks>重采样为纯原生同步操作，不引入异步；<see cref="DecodeAsync"/> 仍返回 <see cref="ValueTask"/>。</remarks>
    private unsafe AudioFrame? ReceiveToAudioFrame(AVFrame* avFrame, AVCodecContext* ctx)
    {
        if (_swrContext == null)
            return CreateAudioFrameFromAVFrame(avFrame, ctx);

        AVFrame* outFrame = ConvertWithSwr(avFrame);
        try
        {
            // 重采样不改变时间戳：源帧无 PTS 时沿用源，确保音频时间轴连续
            if (outFrame->pts == FF.AV_NOPTS_VALUE)
                outFrame->pts = avFrame->pts;
            return CreateAudioFrameFromAVFrame(outFrame, ctx);
        }
        finally
        {
            AVFrame* p = outFrame;
            FF.av_frame_free(&p);
        }
    }

    private static AVSampleFormat MapTargetSampleFormat(SampleFormat fmt) => fmt switch
    {
        SampleFormat.S16 => AVSampleFormat.AV_SAMPLE_FMT_S16,
        SampleFormat.S32 => AVSampleFormat.AV_SAMPLE_FMT_S32,
        SampleFormat.F32 => AVSampleFormat.AV_SAMPLE_FMT_FLT,
        _ => AVSampleFormat.AV_SAMPLE_FMT_S16
    };

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
            byte* buf = stackalloc byte[FF.AV_ERROR_MAX_STRING_SIZE];
            FF.av_strerror(errorCode, buf, (UIntPtr)FF.AV_ERROR_MAX_STRING_SIZE);
            return Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"error code {errorCode}";
        }
    }
}

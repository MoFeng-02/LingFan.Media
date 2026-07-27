using System.Runtime.InteropServices;
using LingFan.Media.Backends.FFmpeg.Interop;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Decoders;

/// <summary>
/// 基于 FFmpeg libavcodec 的 <see cref="IVideoDecoder"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <c>Task.CompletedTask</c>（无 I/O）。</item>
/// <item><see cref="Initialize"/>：同步，avcodec_find_decoder + alloc + open（参数化配置）。</item>
/// <item><see cref="DecodeAsync"/>：热路径异步，返回 <c>ValueTask&lt;VideoFrame?&gt;</c>，
/// avcodec_send_packet + avcodec_receive_frame 是 CPU 密集型，无 I/O，
/// 使用 <c>ValueTask.FromResult</c> 同步完成（减少分配）。</item>
/// <item><see cref="FlushAsync"/>：热路径异步，同上。</item>
/// <item><see cref="Reset"/>：同步，avcodec_flush_buffers。</item>
/// <item><see cref="Dispose"/> / <see cref="DisposeAsync"/>：同步原生释放。</item>
/// </list>
/// <para><b>线程安全</b>：单线程使用（管线线程），非线程安全。</para>
/// <para><b>AOT 兼容</b>：sealed 类，SafeHandle，无反射。</para>
/// </remarks>
internal sealed class FFmpegVideoDecoder : IVideoDecoder, IFramePoolAware<VideoFrame>
{
    private readonly ILogger<FFmpegVideoDecoder> _logger;
    private readonly IGpuDeviceContext? _gpuContext;
    private SafeAVCodecContextHandle? _codecContextHandle;
    private SafeAVBufferRefHandle? _hwDeviceCtx;
    private IFramePool<VideoFrame>? _framePool;
    private bool _disposed;
    private bool _initialized;

    /// <summary>FFmpeg EAGAIN 错误码（跨平台）。必须用 ffmpeg.AVERROR(ffmpeg.EAGAIN) 计算，
    /// 禁止硬编码 -11（Windows 正确，但 macOS/iOS 的 EAGAIN=35，会误判"需要更多数据"为解码失败）。</summary>
    private static readonly int EAGAIN = ffmpeg.AVERROR(ffmpeg.EAGAIN);

    /// <summary>
    /// 初始化 <see cref="FFmpegVideoDecoder"/> 的新实例。
    /// </summary>
    /// <param name="logger">日志器。</param>
    /// <param name="gpuContext">可选 GPU 设备上下文（D3D11VA 硬解需要，null=软件解码）。</param>
    public FFmpegVideoDecoder(ILogger<FFmpegVideoDecoder> logger, IGpuDeviceContext? gpuContext = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _gpuContext = gpuContext;
    }

    /// <inheritdoc/>
    public VideoCodec Codec { get; private set; } = VideoCodec.Unknown;

    /// <inheritdoc/>
    public bool IsHardwareAccelerated { get; private set; }

    /// <inheritdoc/>
    /// <remarks>接口契约：无 I/O，返回 <see cref="Task.CompletedTask"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>同步参数化配置：avcodec_find_decoder + avcodec_alloc_context3 + avcodec_open2。</remarks>
    public unsafe void Initialize(VideoCodec codec, VideoSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            throw new InvalidOperationException("视频解码器已初始化");

        Codec = codec;
        AVCodecID codecId = MapVideoCodecToFFmpeg(codec);

        // 查找解码器
        AVCodec* avCodec = ffmpeg.avcodec_find_decoder(codecId);
        if (avCodec == null)
            throw new NotSupportedException($"FFmpeg 未找到视频解码器: {codec} (codec_id={codecId})");

        // 分配上下文
        AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(avCodec);
        if (ctx == null)
            throw new InvalidOperationException("avcodec_alloc_context3 失败");

        _codecContextHandle = new SafeAVCodecContextHandle((IntPtr)ctx);

        // 配置多线程
        if (settings.EnableHardwareAcceleration && _gpuContext is not null && _gpuContext.ApiType == GPUApiType.D3D11)
        {
            // V2-15 B6: D3D11VA 硬件解码——使用渲染器共享的 D3D11 设备
            // 零拷贝链路：硬解输出 ID3D11Texture2D → D3D11HardwareFrameResource → D3D11Renderer
            try
            {
                InitializeD3D11VA(ctx);
                IsHardwareAccelerated = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "D3D11VA 硬件解码初始化失败，回退到软件解码");
                IsHardwareAccelerated = false;
            }
        }
        else if (settings.EnableHardwareAcceleration)
        {
            // 硬解启用但无 GPU 设备上下文——保持软件解码
            IsHardwareAccelerated = false;
        }

        // 打开解码器
        int ret = ffmpeg.avcodec_open2(ctx, avCodec, null);
        if (ret < 0)
        {
            _codecContextHandle.Dispose();
            _codecContextHandle = null;
            throw new InvalidOperationException($"avcodec_open2 失败: {GetErrorString(ret)} (code={ret})");
        }

        _initialized = true;
        _logger.LogInformation("视频解码器初始化: {Codec}, 硬件加速={HwAccel}", codec, IsHardwareAccelerated);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 热路径异步：avcodec_send_packet + avcodec_receive_frame 是 CPU 密集型同步操作，
    /// 使用 <see cref="ValueTask.FromResult{TResult}"/> 同步完成，减少分配。
    /// </remarks>
    public unsafe ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new InvalidOperationException("视频解码器尚未初始化");

        VideoFrame? frame = DecodeCore(packet);
        return ValueTask.FromResult(frame);
    }

    /// <summary>DecodeAsync 的核心逻辑。</summary>
    private unsafe VideoFrame? DecodeCore(MediaPacket packet)
    {
        AVCodecContext* ctx = (AVCodecContext*)_codecContextHandle!.DangerousGetHandle();

        // 分配临时 AVPacket 并填充数据
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        if (pkt == null)
            throw new InvalidOperationException("av_packet_alloc 失败");

        try
        {
            // 使用 av_new_packet 分配（含 AV_INPUT_BUFFER_PADDING_SIZE 填充）
            // FFmpeg 位流读取器会一次读取 32/64 位，可能越过缓冲区末尾，
            // 必须有 64 字节零填充尾部，否则可能 AccessViolation 或数据损坏。
            // av_new_packet 通过 pkt->buf 引用计数管理内存，av_packet_unref 自动释放。
            int allocRet = ffmpeg.av_new_packet(pkt, packet.Data.Length);
            if (allocRet < 0)
                throw new InvalidOperationException($"av_new_packet 失败: {GetErrorString(allocRet)} (code={allocRet})");
            packet.Data.Span.CopyTo(new Span<byte>(pkt->data, packet.Data.Length));
            // 防御 time_base.num==0 导致 av_q2d 返回 0 → 除以零产生 Infinity/NaN
            double timeBase = ffmpeg.av_q2d(ctx->time_base);
            pkt->pts = timeBase > 0
                ? (long)(packet.Timestamp.TotalSeconds / timeBase)
                : ffmpeg.AV_NOPTS_VALUE;
            pkt->dts = pkt->pts;
            if (packet.KeyFrame)
                pkt->flags |= ffmpeg.AV_PKT_FLAG_KEY;

            // 发送数据包到解码器
            int ret = ffmpeg.avcodec_send_packet(ctx, pkt);
            if (ret < 0 && ret != EAGAIN)
            {
                if (ret != ffmpeg.AVERROR_EOF)
                    _logger.LogWarning("avcodec_send_packet 返回 {Ret}: {Error}", ret, GetErrorString(ret));
                return null;
            }

            // 接收解码帧
            AVFrame* avFrame = ffmpeg.av_frame_alloc();
            if (avFrame == null)
                throw new InvalidOperationException("av_frame_alloc 失败");

            try
            {
                ret = ffmpeg.avcodec_receive_frame(ctx, avFrame);
                if (ret == EAGAIN || ret == ffmpeg.AVERROR_EOF) // EAGAIN or EOF
                    return null; // 需要更多数据或流结束
                if (ret < 0)
                {
                    _logger.LogWarning("avcodec_receive_frame 返回 {Ret}: {Error}", ret, GetErrorString(ret));
                    return null;
                }

                // V2-15 B6: 检查 D3D11VA 硬解输出格式
                if ((AVPixelFormat)avFrame->format == AVPixelFormat.AV_PIX_FMT_D3D11VA_VLD)
                {
                    return CreateHardwareFrameFromAVFrame(avFrame);
                }

                return CreateVideoFrameFromAVFrame(avFrame);
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
    /// <remarks>热路径异步：刷新缓冲取出剩余帧，同步完成。</remarks>
    public unsafe ValueTask<VideoFrame?> FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new InvalidOperationException("视频解码器尚未初始化");

        VideoFrame? frame = FlushCore();
        return ValueTask.FromResult(frame);
    }

    /// <summary>FlushAsync 的核心逻辑：发送 null packet 刷新解码器。</summary>
    private unsafe VideoFrame? FlushCore()
    {
        AVCodecContext* ctx = (AVCodecContext*)_codecContextHandle!.DangerousGetHandle();

        // 发送 null packet 刷新解码器
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

            // V2-15: Flush 时同样需检查 D3D11VA 硬解输出格式（与 DecodeCore 一致）
            if ((AVPixelFormat)avFrame->format == AVPixelFormat.AV_PIX_FMT_D3D11VA_VLD)
            {
                return CreateHardwareFrameFromAVFrame(avFrame);
            }

            return CreateVideoFrameFromAVFrame(avFrame);
        }
        finally
        {
            AVFrame* p = avFrame;
            ffmpeg.av_frame_free(&p);
        }
    }

    /// <inheritdoc/>
    public void SetFramePool(IFramePool<VideoFrame>? pool)
    {
        _framePool = pool;
    }

    /// <inheritdoc/>
    public unsafe void Reset()
    {
        if (!_initialized || _codecContextHandle == null) return;
        AVCodecContext* ctx = (AVCodecContext*)_codecContextHandle.DangerousGetHandle();
        ffmpeg.avcodec_flush_buffers(ctx);
        _logger.LogDebug("视频解码器已重置");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hwDeviceCtx?.Dispose();
        _hwDeviceCtx = null;
        _codecContextHandle?.Dispose();
        _codecContextHandle = null;
        _initialized = false;
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：原生释放为快速同步操作。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ── 辅助方法 ──

    /// <summary>从 AVFrame 创建 VideoFrame。</summary>
    /// <remarks>
    /// <para>V2 池化：若 _framePool 可用，从池中 Rent 帧壳并调用 Reset 填充数据，复用 VideoFrame 实例减少 GC。</para>
    /// <para>V2-05 安全零拷贝（A2）：packed BGRA/RGBA 帧走 av_frame_clone 引用计数路径——
    /// 克隆帧共享原生 buffer（内部对所有 buf 做 av_buffer_ref），SoftwareFrameResource
    /// 直接映射 data[0] 并携带实际 stride，Dispose 时经 SafeAVFrameHandle 释放引用。
    /// YUV/planar 格式渲染层无法直接消费（Skia 仅支持 BGRA/RGBA），保持既有拷贝路径。</para>
    /// </remarks>
    private unsafe VideoFrame CreateVideoFrameFromAVFrame(AVFrame* avFrame)
    {
        int width = avFrame->width;
        int height = avFrame->height;
        AVPixelFormat pixFmt = (AVPixelFormat)avFrame->format;
        PixelFormat format = MapPixelFormatFromFFmpeg(pixFmt);

        // V2-05: packed 4 字节格式优先零拷贝；失败（非引用计数帧/OOM）回退拷贝
        SoftwareFrameResource? resource = null;
        if (pixFmt is AVPixelFormat.AV_PIX_FMT_BGRA or AVPixelFormat.AV_PIX_FMT_RGBA)
        {
            resource = TryCreateZeroCopyResource(avFrame, width, height, format);
        }
        resource ??= CreateCopyResource(avFrame, width, height, pixFmt, format);

        TimeSpan timestamp = avFrame->pts != ffmpeg.AV_NOPTS_VALUE
            ? TimeSpan.FromTicks((long)(avFrame->pts * ffmpeg.av_q2d(avFrame->time_base) * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;
        TimeSpan duration = avFrame->duration > 0
            ? TimeSpan.FromTicks((long)(avFrame->duration * ffmpeg.av_q2d(avFrame->time_base) * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;
        bool keyFrame = (avFrame->flags & ffmpeg.AV_FRAME_FLAG_KEY) != 0;

        // V2 池化：从池中 Rent 帧壳并 Reset 填充数据，复用 VideoFrame 实例
        var frame = _framePool?.Rent() ?? new VideoFrame();
        frame.Reset(width, height, format, resource, timestamp, duration, keyFrame);
        return frame;
    }

    /// <summary>
    /// V2-05 零拷贝路径：av_frame_clone 引用计数共享原生 buffer，避免整帧拷贝。
    /// </summary>
    /// <returns>零拷贝资源；克隆失败（非引用计数帧/内存不足/异常布局）返回 null 由调用方回退拷贝。</returns>
    private static unsafe SoftwareFrameResource? TryCreateZeroCopyResource(
        AVFrame* avFrame, int width, int height, PixelFormat format)
    {
        if (width <= 0 || height <= 0)
            return null; // 异常尺寸交由拷贝路径统一报错

        // av_frame_clone = av_frame_alloc + av_frame_ref：共享所有 buf（引用计数 +1），不拷贝像素
        AVFrame* clone = ffmpeg.av_frame_clone(avFrame);
        if (clone == null)
            return null;

        var owner = new SafeAVFrameHandle((IntPtr)clone);

        int stride = clone->linesize[0];
        if (stride <= 0 || clone->data[0] == null)
        {
            // 负 stride（自底向上布局）或空数据：不支持零拷贝，释放克隆回退
            owner.Dispose();
            return null;
        }

        // packed 4 字节格式：精确长度 = stride*(height-1) + 行有效载荷，绝不越界读取
        int rowPayload = width * 4;
        int length = stride * (height - 1) + rowPayload;
        var memory = new NativeBufferMemoryManager((IntPtr)clone->data[0], length).Memory;
        return new SoftwareFrameResource(width, height, format, memory, stride, owner);
    }

    /// <summary>
    /// 拷贝路径（YUV/planar 及零拷贝回退）：av_image_copy_to_buffer 到 ArrayPool buffer。
    /// </summary>
    private static unsafe SoftwareFrameResource CreateCopyResource(
        AVFrame* avFrame, int width, int height, AVPixelFormat pixFmt, PixelFormat format)
    {
        // 使用 FFmpeg av_image_get_buffer_size 计算所需缓冲区大小
        int bufSize = ffmpeg.av_image_get_buffer_size(pixFmt, width, height, 1);
        if (bufSize <= 0)
            throw new InvalidOperationException(
                $"av_image_get_buffer_size 返回 {bufSize}（format={pixFmt}, {width}x{height}）");

        // V2 L12: 使用 ArrayPool 租借内存，减少 GC 压力（60fps 每秒 60 个帧）
        var resource = new SoftwareFrameResource(width, height, format, bufSize);

        // AVFrame.data/linesize 是 Array8，av_image_copy_to_buffer 需要 Array4，需转换
        var srcData = new byte_ptrArray4();
        srcData[0] = avFrame->data[0];
        srcData[1] = avFrame->data[1];
        srcData[2] = avFrame->data[2];
        srcData[3] = avFrame->data[3];

        var srcLinesize = new int_array4();
        srcLinesize[0] = avFrame->linesize[0];
        srcLinesize[1] = avFrame->linesize[1];
        srcLinesize[2] = avFrame->linesize[2];
        srcLinesize[3] = avFrame->linesize[3];

        // 使用 av_image_copy_to_buffer 正确处理所有像素格式（YUV420P/YUV422P/YUV444P/NV12 等），
        // 避免手动计算色度平面高度导致的非 YUV420 格式数据损坏。
        // Pin Memory<byte> 获取原始指针供 FFmpeg 互操作（using var 确保方法返回前释放 GCHandle）
        using var pin = resource.Data.Pin();
        ffmpeg.av_image_copy_to_buffer(
            (byte*)pin.Pointer, bufSize,
            srcData, srcLinesize,
            pixFmt, width, height, 1);

        return resource;
    }

    // ── V2-15 B6: D3D11VA 硬件解码 ──

    /// <summary>
    /// 初始化 D3D11VA 硬件解码设备上下文（使用渲染器共享的 D3D11 设备，实现零拷贝）。
    /// </summary>
    /// <remarks>
    /// <para>同步操作（sync 分类）：FFmpeg hwdevice_ctx API 和 COM 操作均为同步原生调用，无 I/O await。</para>
    /// <para>共享设备零拷贝链路：渲染器 ID3D11Device → FFmpeg D3D11VA → 硬解纹理 → D3D11Renderer CopySubresourceRegion。</para>
    /// </remarks>
    /// <param name="ctx">FFmpeg 编解码上下文（设置其 hw_device_ctx 字段）。</param>
    private unsafe void InitializeD3D11VA(AVCodecContext* ctx)
    {
        var gpuCtx = _gpuContext!;
        if (gpuCtx.DeviceHandle == IntPtr.Zero)
            throw new InvalidOperationException("GPU 设备句柄无效");
        if (gpuCtx.ContextHandle == IntPtr.Zero)
            throw new InvalidOperationException("GPU 设备上下文句柄无效");

        // 1. 分配 D3D11VA 硬件设备上下文
        AVBufferRef* hwRef = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (hwRef == null)
            throw new InvalidOperationException("av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA) 返回 null");

        try
        {
            // 2. 设置共享 D3D11 设备（通过原始指针操作 AVD3D11VADeviceContext）
            // AVBufferRef.data → AVHWDeviceContext → hwctx → AVD3D11VADeviceContext
            // AVD3D11VADeviceContext 布局：device(void*), device_context(void*), lock_ctx(uint), lock(fn*), unlock(fn*)
            AVHWDeviceContext* hwCtx = (AVHWDeviceContext*)hwRef->data;
            IntPtr* hwctxPtrs = (IntPtr*)hwCtx->hwctx;
            hwctxPtrs[0] = gpuCtx.DeviceHandle;    // device (ID3D11Device*)
            hwctxPtrs[1] = gpuCtx.ContextHandle;    // device_context (ID3D11DeviceContext*)

            // 3. 初始化设备上下文（FFmpeg 检测 device 已设 → 使用共享设备，创建默认 lock/unlock）
            int ret = ffmpeg.av_hwdevice_ctx_init(hwRef);
            if (ret < 0)
                throw new InvalidOperationException($"av_hwdevice_ctx_init 失败: {GetErrorString(ret)} (code={ret})");

            // 4. 设置到编解码上下文（av_buffer_ref 增加引用计数）
            AVBufferRef* ctxRef = ffmpeg.av_buffer_ref(hwRef);
            if (ctxRef == null)
                throw new InvalidOperationException("av_buffer_ref 返回 null（内存不足）");
            ctx->hw_device_ctx = ctxRef;

            // 5. 创建 SafeHandle 管理 hwRef 生命周期（在 try 内——OOM 时 catch 可释放）
            _hwDeviceCtx = new SafeAVBufferRefHandle((IntPtr)hwRef);
        }
        catch
        {
            // 失败时释放 hwRef（av_buffer_unref 通过 SafeHandle 在 Dispose 中处理）
            AVBufferRef* p = hwRef;
            ffmpeg.av_buffer_unref(&p);
            throw;
        }

        _logger.LogInformation("D3D11VA 硬件解码已初始化（共享 D3D11 设备）");
    }

    /// <summary>
    /// 从 D3D11VA 硬解输出的 AVFrame 创建 VideoFrame（零拷贝 GPU 纹理路径）。
    /// </summary>
    /// <remarks>
    /// <para>D3D11VA 帧布局：data[0] = ID3D11Texture2D*（纹理数组），data[1] = 纹理数组索引。</para>
    /// <para>输出 PixelFormat.NV12（D3D11VA 标准输出格式）。</para>
    /// <para>同步操作（hot 路径）：COM AddRef + 对象构造，无 I/O。</para>
    /// </remarks>
    private unsafe VideoFrame CreateHardwareFrameFromAVFrame(AVFrame* avFrame)
    {
        int width = avFrame->width;
        int height = avFrame->height;

        // D3D11VA 帧：data[0] = ID3D11Texture2D*，data[1] = 纹理数组索引
        IntPtr texturePtr = (IntPtr)avFrame->data[0];
        int subresourceIndex = (int)(IntPtr)avFrame->data[1];

        if (texturePtr == IntPtr.Zero)
            throw new InvalidOperationException("D3D11VA 帧纹理指针为空");

        // D3D11VA 标准输出 NV12 格式
        var resource = new D3D11HardwareFrameResource(texturePtr, width, height, PixelFormat.NV12, subresourceIndex);

        TimeSpan timestamp = avFrame->pts != ffmpeg.AV_NOPTS_VALUE
            ? TimeSpan.FromTicks((long)(avFrame->pts * ffmpeg.av_q2d(avFrame->time_base) * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;
        TimeSpan duration = avFrame->duration > 0
            ? TimeSpan.FromTicks((long)(avFrame->duration * ffmpeg.av_q2d(avFrame->time_base) * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;
        bool keyFrame = (avFrame->flags & ffmpeg.AV_FRAME_FLAG_KEY) != 0;

        var frame = _framePool?.Rent() ?? new VideoFrame();
        frame.Reset(width, height, PixelFormat.NV12, resource, timestamp, duration, keyFrame);
        return frame;
    }

    private static AVCodecID MapVideoCodecToFFmpeg(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => AVCodecID.AV_CODEC_ID_H264,
        VideoCodec.H265 => AVCodecID.AV_CODEC_ID_HEVC,
        VideoCodec.AV1 => AVCodecID.AV_CODEC_ID_AV1,
        VideoCodec.VP9 => AVCodecID.AV_CODEC_ID_VP9,
        VideoCodec.MPEG2 => AVCodecID.AV_CODEC_ID_MPEG2VIDEO,
        VideoCodec.MPEG4 => AVCodecID.AV_CODEC_ID_MPEG4,
        _ => throw new NotSupportedException($"不支持的视频编解码器: {codec}")
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

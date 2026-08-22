using System.Runtime.InteropServices;
using Android.Graphics;
using Android.Media;
using Java.Nio;
// 本后端命名空间段为 ...MediaCodec，会遮蔽类型 Android.Media.MediaCodec → 用不撞名的别名。
using AndroidMediaCodec = Android.Media.MediaCodec;
// Android.Graphics.PixelFormat 与 Abstractions 全局冲突 → 别名锁定契约层像素格式。
using PixelFormat = LingFan.Media.Abstractions.PixelFormat;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// 基于托管 <see cref="AndroidMediaCodec"/> 的视频解码器（Surface + <see cref="ImageReader"/> 主路径，ByteBuffer 软件输出兜底）。
/// net-android 内置绑定，非手写 P/Invoke。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：<see cref="DecodeAsync"/> / <see cref="FlushAsync"/> 为热路径，内部同步托管调用，
/// 返回 <see cref="ValueTask.FromResult{TResult}"/>（与 FFmpegVideoDecoder 同构）。<see cref="Initialize"/> 为同步初始化。</para>
/// <para><b>Surface 主路径</b>：硬件解码器.<c>AndroidMediaCodec.Configure</c> 到 <see cref="ImageReader"/>（
/// <see cref="ImageFormatType.Yuv420888"/>，API 26+）的 Surface，帧经 <see cref="ImageReader.AcquireNextImage"/>
/// 取 <see cref="Image"/>，由 <see cref="Image.Plane"/> 的 <see cref="ByteBuffer"/> 经 JNI memcpy 提获得标准
/// 三平面 I420 —— 规避 ByteBuffer 模式部分厂商（天玑/高通）输出私有格式 UV 读不全，以及手写裸指针读取导致的
/// <c>BUS_ADRALN</c> SIGBUS。输出仍为 CPU 帧（本重构默认；GPU 零拷贝暂缓，见设计文档 §5.2）。</para>
/// <para><b>色彩随帧透传</b>：<see cref="ReadOutputFormat"/> 读 <c>KEY_COLOR_STANDARD/RANGE/TRANSFER</c> 填
/// <see cref="VideoColorInfo"/>，随帧交给渲染端选正确的 YUV→RGB 矩阵（治骁龙偏绿）。</para>
/// <para><b>ByteBuffer 兜底</b>：<see cref="ImageReader"/> 创建失败回落；输出按 AOSP <c>stride/slice-height/crop-*</c>
/// 布局由托管 <see cref="ByteBuffer"/> 提取（逻辑复用既有 NV12/I420，去裸指针）。</para>
/// <para><b>仅 Android 可用</b>：非 Android 运行时 <see cref="Initialize"/> 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// </remarks>
internal sealed class AndroidVideoDecoder : IVideoDecoder
{
    private readonly AndroidBackend _backend;
    private readonly ILogger<AndroidVideoDecoder> _logger;

    private AndroidMediaCodec? _codec;
    private ImageReader? _imageReader;         // Surface 主路径的取帧 reader
    private MediaFormat? _outputFormat;        // 当前输出格式（FORMAT_CHANGED 时更新）
    private PixelFormat _outputPixelFormat = PixelFormat.YUV420P;
    private bool _zeroCopyMode;                // true = 解码器输出到 ImageReader Surface
    private VideoColorInfo _colorInfo;         // 当前输出色彩空间（KEY_COLOR_*，透传渲染端）

    private readonly Queue<MediaPacket> _pendingInput = new();
    private readonly Queue<VideoFrame> _pendingFrames = new();

    // Surface 取帧诊断计数（Dispose 汇总，定位零产帧各分支分布）
    private long _drainCalls, _drainDequeued, _drainTryAgain, _drainReleased, _drainAcquireNull;
    private bool _surfaceAcquireTimeoutLogged;
    private bool _surfacePlaneExtractFailedLogged;

    // 诊断节流：收包/产帧计数
    private int _packetsFed;
    private int _framesProduced;
    private const int LogInterval = 64;

    private VideoCodec _codecType = VideoCodec.Unknown;
    private VideoSettings _settings = null!;
    private bool _initialized;
    private bool _disposed;

    // 行拷贝复用缓冲（Image.Plane 行复制）
    private byte[] _rowScratch = Array.Empty<byte>();

    // ImageReader：容纳管线缓冲深度并留解码器渲染余量。
    private const int ReaderMaxImages = 6;
    // release(render=true) 后图像经 BufferQueue 异步到达 reader 的有界等待（毫秒）。
    private const int AcquireWaitMs = 30;

    // MediaCodec dequeue 返回码 / flags 位（公开 AOSP 值）。
    private const int InfoTryAgainLater = -1;
    private const int InfoOutputFormatChanged = -2;
    private const int InfoOutputBuffersChanged = -3;
    private const int FlagKeyFrame = 1;
    private const int FlagEndOfStream = 4;

    public AndroidVideoDecoder(AndroidBackend backend, ILogger<AndroidVideoDecoder> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public VideoCodec Codec => _codecType;

    /// <inheritdoc/>
    /// <remarks>仅 Surface 模式为真（硬件解码直出 GPU 帧）；ByteBuffer 模式可能落在软件解码器，保守报假。</remarks>
    public bool IsHardwareAccelerated => _zeroCopyMode;

    /// <inheritdoc/>
    public void Initialize(VideoCodec codec, VideoSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("AndroidVideoDecoder 已初始化，不可重复 Initialize。");

        if (!OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException(
                "Android 视频解码器仅支持 Android 运行时。请使用 FFmpeg 作为跨平台后端。");

        string? mime = AndroidCodecMaps.VideoCodecToMime(codec);
        if (mime is null)
            throw new NotSupportedException($"Android MediaCodec 不支持的视频编解码器: {codec}");

        _codecType = codec;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // ── Surface 输出主路径（权威 Android 视频流程）──
        // 硬件解码器输出到 ImageReader（YUV_420_888），经托管 Image.Plane 提取得标准 YUV，
        // 规避 ByteBuffer 模式部分厂商输出私有格式、取不全 UV 的兼容问题。
        // reader 创建失败（API<26 / gralloc 拒绝）→ 回落 ByteBuffer + 软解。
        ImageReader? reader = null;
        if (settings.Width is > 0 && settings.Height is > 0)
        {
            try
            {
                reader = ImageReader.NewInstance(settings.Width!.Value, settings.Height!.Value,
                    ImageFormatType.Yuv420888, ReaderMaxImages);
            }
            catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException)
            {
                // 非法参数（含 gralloc 拒绝用途组合）→ 优雅回落 ByteBuffer 路径。
                reader = null;
                _logger.LogWarning("[ANDROID-VID] ImageReader 创建失败（{Reason}），回落 ByteBuffer 输出路径", ex.Message);
            }
        }

        // ByteBuffer（软解兜底）路径优先软件解码器；Surface 路径恒硬件解码器。
        var codecObj = CreateVideoCodec(mime, codec, preferSoftwareDecoder: reader is null);
        try
        {
            using var fmt = new MediaFormat();
            fmt.SetString(MediaFormat.KeyMime, mime);

            // 部分解码器 configure 要求显式 width/height，仅 csd 推导不足会报错。
            if (settings.Width is > 0)
                fmt.SetInteger(MediaFormat.KeyWidth, settings.Width.Value);
            if (settings.Height is > 0)
                fmt.SetInteger(MediaFormat.KeyHeight, settings.Height.Value);

            var csd = settings.CodecConfiguration;
            if (csd.Length > 0)
                fmt.SetByteBuffer("csd-0", ByteBuffer.Wrap(csd.ToArray())); // 键 "csd-0"（AOSP KEY_CSD0）

            codecObj.Configure(fmt, reader?.Surface, null, 0); // surface=null → ByteBuffer 输出
            codecObj.Start();

            _imageReader = reader;
            _zeroCopyMode = reader is not null;

            // 读取输出格式：Surface 模式 color-format 键无意义（帧不经 ByteBuffer），仅读尺寸与色彩键。
            _outputFormat?.Dispose();
            _outputFormat = codecObj.OutputFormat; // getOutputFormat 无参重载 → OutputFormat 属性
            ReadOutputFormat(_outputFormat);
        }
        catch
        {
            reader?.Dispose();
            _imageReader = null;
            _zeroCopyMode = false;
            codecObj.Release();
            throw;
        }

        _codec = codecObj;
        _initialized = true;
        _logger.LogInformation("[ANDROID-VID] 初始化完成: {Codec} → {Mime}, 输出像素格式 {Fmt}, Surface={Surface}, csd长度={CsdLen}",
            codec, mime, _outputPixelFormat, _zeroCopyMode, settings.CodecConfiguration.Length);
    }

    /// <summary>创建视频解码器。ByteBuffer 路径下 H264 优先软件解码器（c2.android.avc.decoder），失败回退硬件。
    /// Surface 路径恒硬件解码器。</summary>
    private static AndroidMediaCodec CreateVideoCodec(string mime, VideoCodec codec, bool preferSoftwareDecoder)
    {
        if (preferSoftwareDecoder && codec == VideoCodec.H264)
        {
            try
            {
                return AndroidMediaCodec.CreateByCodecName("c2.android.avc.decoder");
            }
            catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or Java.Lang.IllegalStateException)
            {
                // 低版本/名字不存在：回退硬件解码器。
                return AndroidMediaCodec.CreateDecoderByType(mime);
            }
        }
        return AndroidMediaCodec.CreateDecoderByType(mime);
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // 实际初始化已在 Initialize 完成（同步托管调用）。
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ReadOutputFormat(MediaFormat fmt)
    {
        // Surface 模式：帧不经 ByteBuffer（color-format 键无意义），语义像素格式恒 NV12。
        if (_zeroCopyMode)
        {
            _outputPixelFormat = PixelFormat.NV12;
        }
        else
        {
            int cf = fmt.ContainsKey(MediaFormat.KeyColorFormat) ? fmt.GetInteger(MediaFormat.KeyColorFormat) : 0;
            var pf = AndroidCodecMaps.ColorFormatToPixelFormat(cf);
            if (pf is null)
                throw new NotSupportedException(
                    $"Android 视频解码器不支持的输出颜色格式 0x{cf:X}（当前仅支持 NV12 / I420 两类 YUV420）。");
            _outputPixelFormat = pf.Value;
        }

        // 色彩空间（可选键，API 24+）：渲染端据以选择 YUV→RGB 矩阵。低版本/缺失时回退 Unspecified。
        int cs = -1, cr = -1, ct = -1;
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
        {
            if (fmt.ContainsKey(MediaFormat.KeyColorStandard)) cs = fmt.GetInteger(MediaFormat.KeyColorStandard);
            if (fmt.ContainsKey(MediaFormat.KeyColorRange)) cr = fmt.GetInteger(MediaFormat.KeyColorRange);
            if (fmt.ContainsKey(MediaFormat.KeyColorTransfer)) ct = fmt.GetInteger(MediaFormat.KeyColorTransfer);
        }
        _colorInfo = new VideoColorInfo(
            AndroidCodecMaps.ColorStandardFromNdk(cs),
            AndroidCodecMaps.ColorRangeFromNdk(cr),
            AndroidCodecMaps.ColorTransferFromNdk(ct));
    }

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        if (packet is null) return new ValueTask<VideoFrame?>(ReadOutput());

        // 诊断节流：收包节奏
        if ((_packetsFed % LogInterval) == 0)
            _logger.LogInformation("[ANDROID-VID] 收包 #{Count} size={Size} pts={Pts:g} key={Key}",
                _packetsFed, packet.Data.Length, packet.Timestamp, packet.KeyFrame);
        _packetsFed++;

        _pendingInput.Enqueue(packet);
        FeedInput();
        return new ValueTask<VideoFrame?>(ReadOutput());
    }

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        // 先排空待喂入队列
        FeedInput();

        // 发送 EOS 以迫使解码器吐出剩余帧；若暂无输入槽则跳过（直接排空输出）
        int inIdx = _codec!.DequeueInputBuffer(1000);
        if (inIdx >= 0)
            _codec.QueueInputBuffer(inIdx, 0, 0, 0, (MediaCodecBufferFlags)FlagEndOfStream);

        return new ValueTask<VideoFrame?>(DrainOutput(10_000));
    }

    /// <inheritdoc/>
    public void Reset()
    {
        if (_codec is null) return;
        _codec.Flush();
        DrainAndDispose(_pendingInput);
        DrainAndDispose(_pendingFrames);
    }

    /// <summary>尽可能把待喂入包拷入解码器输入槽。</summary>
    private void FeedInput()
    {
        while (_pendingInput.Count > 0)
        {
            int idx = _codec!.DequeueInputBuffer(0);
            if (idx < 0) break; // 暂无输入槽，保留包待下次

            var pkt = _pendingInput.Dequeue();
            try
            {
                ByteBuffer? buf = _codec.GetInputBuffer(idx);
                if (buf is null) continue;

                int len = Math.Min(pkt.Data.Length, buf.Remaining());
                if (len != pkt.Data.Length)
                    _logger.LogWarning("[ANDROID-VID] 输入 buffer 容量({Cap})小于包大小({Len})，截断喂入",
                        buf.Remaining(), pkt.Data.Length);

                var mem = pkt.Data;
                if (MemoryMarshal.TryGetArray(mem, out ArraySegment<byte> seg) && seg.Array is not null)
                    buf.Put(seg.Array, seg.Offset, len);
                else
                    buf.Put(mem.ToArray(), 0, len);

                long ptsUs = pkt.Timestamp.Ticks > 0 ? pkt.Timestamp.Ticks / 10 : 0;
                _codec.QueueInputBuffer(idx, 0, len, ptsUs, (MediaCodecBufferFlags)0);
            }
            finally
            {
                pkt.Dispose();
            }
        }
    }

    /// <summary>读出一个已解出帧（先返回 FIFO 余帧，再尝试从解码器申领）。</summary>
    private VideoFrame? ReadOutput()
    {
        if (_pendingFrames.Count > 0) return _pendingFrames.Dequeue();
        return DrainOutput(0);
    }

    /// <summary>排空解码器输出，将可用帧入 FIFO，返回队首（超时内无帧返回 null）。</summary>
    private VideoFrame? DrainOutput(long timeoutUs)
    {
        // Surface 模式：帧渲染到 ImageReader 后从图像取帧。
        if (_zeroCopyMode && _imageReader is not null)
            return DrainOutputSurface(timeoutUs);

        return DrainOutputByteBuffer(timeoutUs);
    }

    /// <summary>ByteBuffer 兜底路径：dequeueOutputBuffer → 获 ByteBuffer → 按 stride/crop 提取 → release。</summary>
    private VideoFrame? DrainOutputByteBuffer(long timeoutUs)
    {
        while (true)
        {
            var info = new AndroidMediaCodec.BufferInfo();
            int idx = _codec!.DequeueOutputBuffer(info, timeoutUs);
            if (idx == InfoTryAgainLater) break;
            if (idx == InfoOutputFormatChanged)
            {
                _outputFormat?.Dispose();
                _outputFormat = _codec.OutputFormat;
                ReadOutputFormat(_outputFormat);
                continue;
            }
            if (idx == InfoOutputBuffersChanged) continue;
            if (idx < 0) break;

            if (((int)info.Flags & FlagEndOfStream) != 0)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                break;
            }
            if (info.Size <= 0)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                continue;
            }

            var frame = ExtractFrame(idx, info);
            _codec.ReleaseOutputBuffer(idx, false);

            if ((_framesProduced % LogInterval) == 0)
                _logger.LogInformation("[ANDROID-VID] 产帧 #{Count} {W}x{H} {Fmt} pts={Pts:g}",
                    _framesProduced, frame.Width, frame.Height, frame.Format, frame.Timestamp);
            _framesProduced++;
            _pendingFrames.Enqueue(frame);
        }

        return _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;
    }

    /// <summary>Surface 主路径排空：release(render=true) 渲染进 reader → 申领图像 → CPU 提取得 I420 帧。</summary>
    private VideoFrame? DrainOutputSurface(long timeoutUs)
    {
        _drainCalls++;
        const int DrainBatch = 8;
        for (int i = 0; i < DrainBatch; i++)
        {
            long waitUs = timeoutUs > 0 ? timeoutUs : 10_000;
            var info = new AndroidMediaCodec.BufferInfo();
            int idx = _codec!.DequeueOutputBuffer(info, waitUs);
            if (idx == InfoTryAgainLater) { _drainTryAgain++; break; }
            if (idx == InfoOutputFormatChanged)
            {
                _outputFormat?.Dispose();
                _outputFormat = _codec.OutputFormat;
                ReadOutputFormat(_outputFormat);
                continue;
            }
            if (idx == InfoOutputBuffersChanged) continue;
            if (idx < 0) { _drainTryAgain++; break; }

            if (((int)info.Flags & FlagEndOfStream) != 0)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                break;
            }

            // 关键：render=true 把该帧渲染进 reader 的 Surface（帧内容进入 BufferQueue）；随后 acquire 取图。
            _codec.ReleaseOutputBuffer(idx, true);
            _drainReleased++;

            bool keyFrame = ((int)info.Flags & FlagKeyFrame) != 0;
            var frame = TryCreateFrameFromReader(info.PresentationTimeUs, keyFrame);
            if (frame is null)
            {
                _drainAcquireNull++;
                break; // 图像未在界内到达：停止本轮，下轮再取（FIFO 保序）
            }

            if ((_framesProduced % LogInterval) == 0)
                _logger.LogInformation("[ANDROID-VID] 产帧 #{Count} {W}x{H} {Fmt} pts={Pts:g}",
                    _framesProduced, frame.Width, frame.Height, frame.Format, frame.Timestamp);
            _framesProduced++;
            _pendingFrames.Enqueue(frame);
            _drainDequeued++;
        }

        return _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;
    }

    /// <summary>从 ImageReader 申领一帧图像并构造 <see cref="VideoFrame"/>（托管 CPU 平面提取）。
    /// 申领到的 <see cref="Image"/> 必须在 <c>finally</c> 内 <see cref="Image.Close"/> 归还缓冲（防 reader 耗尽）。</summary>
    private VideoFrame? TryCreateFrameFromReader(long infoPtsUs, bool keyFrame)
    {
        Image? image = AcquireImageWithTimeout();
        if (image is null) return null;

        try
        {
            // PTS：优先 Image 时间戳（纳秒，随帧走、与申领时机解耦）；无则回退 dequeue 的 info PTS。
            TimeSpan pts = image.Timestamp > 0
                ? TimeSpan.FromTicks(image.Timestamp / 100) // ns → ticks
                : infoPtsUs >= 0 ? TimeSpan.FromTicks(infoPtsUs * 10) : TimeSpan.Zero;

            int w = image.Width, h = image.Height;
            if (w <= 0 || h <= 0)
            {
                if (!_surfacePlaneExtractFailedLogged) { _surfacePlaneExtractFailedLogged = true; _logger.LogWarning("[ANDROID-VID] Image 尺寸非法（{W}x{H}），帧产出跳过", w, h); }
                return null;
            }

            var crop = image.CropRect;
            int vw = crop is not null && crop.Right > crop.Left && crop.Bottom > crop.Top
                ? crop.Right - crop.Left : w;
            int vh = crop is not null && crop.Right > crop.Left && crop.Bottom > crop.Top
                ? crop.Bottom - crop.Top : h;
            if (vw <= 0 || vh <= 0) { vw = w; vh = h; }

            var planes = image.GetPlanes();
            if (planes is null || planes.Length < 3)
            {
                if (!_surfacePlaneExtractFailedLogged) { _surfacePlaneExtractFailedLogged = true; _logger.LogWarning("[ANDROID-VID] Image 平面数不足（{N}），帧产出跳过", planes?.Length ?? 0); }
                return null;
            }

            int cw = (vw + 1) / 2, ch = (vh + 1) / 2;
            var resource = new SoftwareFrameResource(vw, vh, PixelFormat.YUV420P, checked(vw * vh + 2 * cw * ch));
            if (!ExtractI420(planes, crop, vw, vh, resource.Data.Span))
            {
                if (!_surfacePlaneExtractFailedLogged) { _surfacePlaneExtractFailedLogged = true; _logger.LogWarning("[ANDROID-VID] Image 平面提取失败，帧产出跳过"); }
                return null;
            }

            resource.ColorInfo = _colorInfo;
            return new VideoFrame(vw, vh, PixelFormat.YUV420P, resource, pts, TimeSpan.Zero, keyFrame, _colorInfo);
        }
        finally
        {
            image.Close(); // 归还缓冲（等价 AImage_delete）
        }
    }

    /// <summary>有界等待申领一张图像；超时返回 null（一次性日志节流）。</summary>
    private Image? AcquireImageWithTimeout()
    {
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp()
            + AcquireWaitMs * System.Diagnostics.Stopwatch.Frequency / 1000;
        while (true)
        {
            Image? image = _imageReader?.AcquireNextImage();
            if (image is not null) return image;
            if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
            {
                if (!_surfaceAcquireTimeoutLogged)
                {
                    _surfaceAcquireTimeoutLogged = true;
                    _logger.LogWarning("[ANDROID-VID] AcquireNextImage 在 {Ms}ms 内未取到图像（render=true 后帧未发布到 reader），本次无帧",
                        AcquireWaitMs);
                }
                return null;
            }
            Thread.Sleep(1);
        }
    }

    /// <summary>按 YUV_420_888 三平面 + crop/stride/pixelStride 提取为紧密 I420（Y+U+V）。
    /// 走托管 <see cref="ByteBuffer"/>（JNI memcpy / 绝对读），无原生裸指针。</summary>
    private bool ExtractI420(Image.Plane[] planes, Rect? crop, int w, int h, Span<byte> dst)
    {
        var pY = planes[0];
        var pU = planes[1];
        var pV = planes[2];

        int cropL = crop is not null ? Math.Max(crop.Left, 0) : 0;
        int cropT = crop is not null ? Math.Max(crop.Top, 0) : 0;
        int cw = (w + 1) / 2, ch = (h + 1) / 2;

        if (_rowScratch.Length < Math.Max(w, cw)) _rowScratch = new byte[Math.Max(w, cw)];

        // ── Y（pixelStride 恒 1，行拷）──
        if (!CopyPlaneRows(pY, cropL, cropT, w, h, dst, 0, w, 1))
            return false;

        int ySize = w * h;
        int uStrideOff = ySize;
        int vStrideOff = ySize + cw * ch;

        // ── U / V（支持 planar[pixelStride=1] 行拷 与 semi-planar[pixelStride=2] 绝对读）──
        if (pU.PixelStride == 1 && pV.PixelStride == 1)
        {
            if (!CopyPlaneRows(pU, cropL / 2, cropT / 2, cw, ch, dst, uStrideOff, cw, 1)
                || !CopyPlaneRows(pV, cropL / 2, cropT / 2, cw, ch, dst, vStrideOff, cw, 1))
                return false;
        }
        else
        {
            // interleaved：平面自身经绝对读定位（plane1=U、plane2=V），索引格式一致。
            for (int r = 0; r < ch; r++)
            {
                int uRow = (cropT / 2 + r) * pU.RowStride;
                    int vRow = (cropT / 2 + r) * pV.RowStride;
                    for (int c = 0; c < cw; c++)
                    {
                        int uIdx = uRow + (cropL / 2 + c) * pU.PixelStride;
                        int vIdx = vRow + (cropL / 2 + c) * pV.PixelStride;
                        dst[uStrideOff + r * cw + c] = (byte)pU.Buffer!.Get(uIdx); // Get(int) 返回 sbyte，显式转 byte
                        dst[vStrideOff + r * cw + c] = (byte)pV.Buffer!.Get(vIdx);
                    }
            }
        }
        return true;
    }

    /// <summary>按行步长+crop 把单个平面逐行拷入目标（pixelStride=1 的紧凑采样）。</summary>
    private bool CopyPlaneRows(Image.Plane plane, int srcCol, int srcRow, int count, int rows,
        Span<byte> dst, int dstOffset, int dstRowBytes, int _ /*unused pixelStride, 恒1*/)
    {
        var buf = plane.Buffer;
        if (buf is null) return false;
        int rowStride = Math.Max(plane.RowStride, 0);
        if (rowStride == 0) rowStride = count;
        try
        {
            for (int r = 0; r < rows; r++)
            {
                int pos = (srcRow + r) * rowStride + srcCol;
                if (pos + count > buf.Remaining()) return false;
                buf.Position(pos);
                buf.Get(_rowScratch, 0, count);
                _rowScratch.AsSpan(0, count).CopyTo(dst.Slice(dstOffset + r * dstRowBytes, count));
            }
        }
        catch (Java.Lang.IndexOutOfBoundsException)
        {
            return false; // 越界读（缓冲不足/非法 stride）：跳过该帧，不崩溃
        }
        return true;
    }

    /// <summary>从输出 buffer 按 stride/slice-height/crop 布局提取 YUV 帧（ByteBuffer 兜底路径）。</summary>
    private unsafe VideoFrame ExtractFrame(int idx, AndroidMediaCodec.BufferInfo info)
    {
        var buf = _codec!.GetOutputBuffer(idx);
        if (buf is null)
            throw new InvalidOperationException("[ANDROID-VID] getOutputBuffer 返回 null");

        // 读取整个输出缓冲到托管数组（含 stride 对齐填充），再按布局索引。
        int cap = buf.Capacity();
        var raw = new byte[cap];
        buf.Rewind();
        buf.Get(raw);
        int baseOff = Math.Max(info.Offset, 0);

        int fullW = 0, fullH = 0, stride = 0, sliceH = 0;
        int cropL = 0, cropT = 0, cropR = 0, cropB = 0;
        bool hasCrop = false;
        if (_outputFormat is not null)
        {
            if (_outputFormat.ContainsKey(MediaFormat.KeyWidth)) fullW = _outputFormat.GetInteger(MediaFormat.KeyWidth);
            if (_outputFormat.ContainsKey(MediaFormat.KeyHeight)) fullH = _outputFormat.GetInteger(MediaFormat.KeyHeight);
            // KeyStride/KeySliceHeight 为 API 23+、KeyCrop* 为 API 33+；低版本无这些键，按默认处理。
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
            {
                if (_outputFormat.ContainsKey(MediaFormat.KeyStride)) stride = _outputFormat.GetInteger(MediaFormat.KeyStride);
                if (_outputFormat.ContainsKey(MediaFormat.KeySliceHeight)) sliceH = _outputFormat.GetInteger(MediaFormat.KeySliceHeight);
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(33)
                && _outputFormat.ContainsKey(MediaFormat.KeyCropRight) && _outputFormat.ContainsKey(MediaFormat.KeyCropBottom)
                && _outputFormat.ContainsKey(MediaFormat.KeyCropLeft) && _outputFormat.ContainsKey(MediaFormat.KeyCropTop))
            {
                cropR = _outputFormat.GetInteger(MediaFormat.KeyCropRight);
                cropB = _outputFormat.GetInteger(MediaFormat.KeyCropBottom);
                cropL = _outputFormat.GetInteger(MediaFormat.KeyCropLeft);
                cropT = _outputFormat.GetInteger(MediaFormat.KeyCropTop);
                hasCrop = true;
            }
        }

        if (stride <= 0) stride = fullW;
        if (sliceH <= 0) sliceH = fullH;
        if (fullW <= 0 || fullH <= 0)
            throw new InvalidOperationException("[ANDROID-VID] 输出格式缺少 width/height");

        // MediaCodec 的 crop 右/下为开区间；可见 = right-left / bottom-top。无 crop 时回退源几何。
        int displayW = hasCrop ? (cropR - cropL) : (_settings.Width is > 0 ? _settings.Width.Value : fullW);
        int displayH = hasCrop ? (cropB - cropT) : (_settings.Height is > 0 ? _settings.Height.Value : fullH);
        int w = Math.Max(displayW, 1), h = Math.Max(displayH, 1);
        if (!hasCrop) { cropL = 0; cropT = 0; }

        int cW420 = (w + 1) / 2, cH420 = (h + 1) / 2;
        int ySize = w * h;
        int uvSize = _outputPixelFormat == PixelFormat.NV12
            ? w * ((h + 1) / 2)
            : 2 * cW420 * cH420;

        var resource = new SoftwareFrameResource(w, h, _outputPixelFormat, checked(ySize + uvSize));
        Span<byte> dst = resource.Data.Span;

        bool key = ((int)info.Flags & FlagKeyFrame) != 0;
        var pts = info.PresentationTimeUs >= 0
            ? TimeSpan.FromTicks(info.PresentationTimeUs * 10)
            : TimeSpan.Zero;

        if (_outputPixelFormat == PixelFormat.NV12)
            ExtractNV12(raw, baseOff, stride, sliceH, cropL, cropT, w, h, dst);
        else
            ExtractI420Packed(raw, baseOff, stride, sliceH, cropL, cropT, w, h, dst);

        resource.ColorInfo = _colorInfo;
        return new VideoFrame(w, h, _outputPixelFormat, resource, pts, TimeSpan.Zero, key, _colorInfo);
    }

    /// <summary>从 NV12 半平面布局提取（Y + 交织 UV），src 为整个输出缓冲，base 为数据起始偏移。</summary>
    private static void ExtractNV12(byte[] src, int baseOff, int stride, int sliceH, int cropL, int cropT, int w, int h, Span<byte> dst)
    {
        int yPlaneSize = stride * sliceH;
        for (int row = 0; row < h; row++)
        {
            int srcOff = baseOff + (cropT + row) * stride + cropL;
            int dstOff = row * w;
            CopyRow(src, srcOff, w, dst, dstOff);
        }
        int chromaH = (h + 1) / 2;
        int chromaSrcRow0 = cropT / 2;
        int uvCropByte = cropL & ~1;
        for (int cRow = 0; cRow < chromaH; cRow++)
        {
            int srcOff = baseOff + yPlaneSize + (chromaSrcRow0 + cRow) * stride + uvCropByte;
            int dstOff = w * h + cRow * w;
            int need = Math.Min(w, Math.Max(stride - uvCropByte, 0));
            if (need > 0)
            {
                int cnt = Math.Min(need, w);
                CopyRow(src, srcOff, cnt, dst, dstOff);
                if (cnt < w) dst.Slice(dstOff + cnt, w - cnt).Fill(dst[dstOff + cnt - 1]); // 余量填充
            }
            else
            {
                dst.Slice(dstOff, w).Fill(128); // 无有效源：中性灰
            }
        }
    }

    /// <summary>从 I420 三平面布局提取（Y + U + V）。</summary>
    private static void ExtractI420Packed(byte[] src, int baseOff, int stride, int sliceH, int cropL, int cropT, int w, int h, Span<byte> dst)
    {
        int yPlaneSize = stride * sliceH;
        int chromaStride = stride / 2;
        if (chromaStride <= 0) chromaStride = (w + 1) / 2;
        int chromaPlaneSize = chromaStride * (sliceH / 2);
        int cw = (w + 1) / 2, ch = (h + 1) / 2;
        int cCropL = cropL / 2, cCropT = cropT / 2;
        int copyCols = Math.Min(cw, Math.Max(((w + 1) - (cropL & 1) + 1) / 2, 0));
        if (copyCols <= 0) copyCols = cw;

        // Y
        for (int row = 0; row < h; row++)
        {
            int srcOff = baseOff + (cropT + row) * stride + cropL;
            int dstOff = row * w;
            if (srcOff >= 0 && srcOff + w <= src.Length)
                new ReadOnlySpan<byte>(src, srcOff, w).CopyTo(dst.Slice(dstOff, w));
        }

        // U / V（源为三平面；V 平面紧随 U，偏置 chromaPlaneSize）
        int uDst = w * h;
        int vDst = uDst + cw * ch;
        int uBase = baseOff + yPlaneSize + cCropL;
        int vBase = baseOff + yPlaneSize + chromaPlaneSize + cCropL;
        int cnt = Math.Min(copyCols, cw);
        for (int row = 0; row < ch; row++)
        {
            int uSrc = uBase + (cCropT + row) * chromaStride;
            int uOff = uDst + row * cw;
            if (cnt > 0 && uSrc + cnt <= src.Length)
            {
                new ReadOnlySpan<byte>(src, uSrc, cnt).CopyTo(dst.Slice(uOff, cnt));
                if (cnt < cw) dst.Slice(uOff + cnt, cw - cnt).Fill(dst[uOff + cnt - 1]);
            }
            else dst.Slice(uOff, cw).Fill(128);

            int vSrc = vBase + (cCropT + row) * chromaStride;
            int vOff = vDst + row * cw;
            if (cnt > 0 && vSrc + cnt <= src.Length)
            {
                new ReadOnlySpan<byte>(src, vSrc, cnt).CopyTo(dst.Slice(vOff, cnt));
                if (cnt < cw) dst.Slice(vOff + cnt, cw - cnt).Fill(dst[vOff + cnt - 1]);
            }
            else dst.Slice(vOff, cw).Fill(128);
        }
    }

    private static void CopyRow(byte[] src, int srcOff, int count, Span<byte> dst, int dstOff)
    {
        if (srcOff < 0 || srcOff + count > src.Length) return; // 越界：跳过该行（不抛不尾）
        new ReadOnlySpan<byte>(src, srcOff, count).CopyTo(dst.Slice(dstOff, count));
    }

    private void EnsureInitialized()
    {
        if (!_initialized || _codec is null)
            throw new InvalidOperationException("AndroidVideoDecoder 尚未 Initialize。");
    }

    private static void DrainAndDispose(Queue<MediaPacket> q)
    {
        while (q.Count > 0) q.Dequeue().Dispose();
    }

    private static void DrainAndDispose(Queue<VideoFrame> q)
    {
        while (q.Count > 0) q.Dequeue().Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCore();
    }

    private void DisposeCore()
    {
        DrainAndDispose(_pendingInput);
        DrainAndDispose(_pendingFrames);
        // 先释放编解码器，再释放 reader（窗口须活得比编解码器久）。
        _codec?.Release();
        _codec = null;
        _outputFormat?.Dispose();
        _outputFormat = null;
        _imageReader?.Dispose();
        _imageReader = null;
        _zeroCopyMode = false;

        // Surface 取帧分支汇总（无条件：确证 drain 是否被调用、各分支分布）
        if (_drainCalls > 0)
        {
            _logger.LogWarning(
                "[ANDROID-VID] Surface 取帧汇总：drainCalls={Calls} dequeued={Dq} release(render)=={Rel} tryAgain={Ta} acquireNull={An}",
                _drainCalls, _drainDequeued, _drainReleased, _drainTryAgain, _drainAcquireNull);
        }
    }
}
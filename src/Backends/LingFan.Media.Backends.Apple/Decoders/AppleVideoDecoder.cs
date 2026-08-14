using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Apple.Shared;

namespace LingFan.Media.Backends.Apple.Decoders;

/// <summary>
/// 基于 Apple VideoToolbox 的视频解码器（<see cref="IVideoDecoder"/>）。
/// </summary>
/// <remarks>
/// <para><b>架构</b>：demuxer 经 <see cref="MediaPacket"/> 传入<b>压缩</b>样本（avcC/hvcC 长度前缀 NAL），
/// 解码器在 <see cref="Initialize"/> 用 SPS/PPS/VPS 构建 <c>CMVideoFormatDescription</c>，
/// 创建 <c>VTDecompressionSession</c>，逐包喂入解出 <c>CVPixelBuffer</c>。</para>
/// <para><b>先软解后硬解</b>（B0/B1）：默认软件路径——把 CVPixelBuffer（NV12）拷贝进
/// <see cref="SoftwareFrameResource"/>。零拷贝 IOSurface→Metal 由 <c>EnableVideoToolboxZeroCopy</c> 门控，
/// 需 C0 Metal 消费侧生产者注入，当前未接线，开启即诚实抛 <see cref="NotSupportedException"/>。</para>
/// <para><b>同步解码</b>：不清算 <c>kVTDecodeFrame_EnableAsynchronousDecompression</c>，回调在
/// <c>VTDecompressionSessionDecodeFrame</c> 返回前同步触发（Apple 文档保证），故热路径返回
/// <see cref="ValueTask.FromResult{TResult}"/>，无伪异步。</para>
/// <para><b>仅 Apple 可用</b>：非 Apple 运行时 <see cref="Initialize"/> 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2050",
    Justification = "无 [ComImport]，使用原始 [LibraryImport] P/Invoke，不会被裁剪器移除。仅 Apple 运行时使用。")]
internal sealed unsafe class AppleVideoDecoder : IVideoDecoder
{
    private readonly AppleBackend _backend;
    private readonly ILogger<AppleVideoDecoder> _logger;

    private readonly Queue<MediaPacket> _pendingInput = new();
    private readonly Queue<VideoFrame> _pendingFrames = new();

    private VideoCodec _codecType = VideoCodec.Unknown;
    private VideoSettings _settings = null!;

    private nint _formatDescription;  // CMVideoFormatDescription（+1）
    private nint _session;            // VTDecompressionSession（+1）
    private GCHandle _gcHandle;       // 回调 refCon → this

    // 同步解码：回调在 DecodeFrame 内触发，用实例字段传递当前帧时间戳（单管线线程，无并发）
    private TimeSpan _currentPts;
    private TimeSpan _currentDur;
    private bool _currentKey;

    private bool _initialized;
    private bool _disposed;

    public AppleVideoDecoder(AppleBackend backend, ILogger<AppleVideoDecoder> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public VideoCodec Codec => _codecType;

    /// <inheritdoc/>
    // 与 AndroidVideoDecoder 同构：当前为「软件回读」路径——VideoToolbox 解出 CVPixelBuffer 后经
    // CVPixelBufferLockBaseAddress 强制 CPU 读回、拷贝进 SoftwareFrameResource，不交付 GPU/零拷贝帧。
    // 本仓约定（见 MF 设计文档「修复 IsHardwareAccelerated 假回显 bug」与 AndroidVideoDecoder）：
    // 仅当确能交付 GPU 纹理/零拷贝帧才报 true。此路径未达，故报 false——
    // 高复杂度内容（4K/8K HDR）将正确触发 MediaPlayer 的「可能无法实时」告警，而非静默假绿。
    // 零拷贝 IOSurface→Metal（C0）落地、能交付 GPU 纹理帧后，此处方可改 true。
    public bool IsHardwareAccelerated => false;

    /// <inheritdoc/>
    public void Initialize(VideoCodec codec, VideoSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("AppleVideoDecoder 已初始化，不可重复 Initialize。");

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS())
            throw new PlatformNotSupportedException(
                "Apple 视频解码器仅支持 Apple 运行时（macOS / iOS）。请使用 FFmpeg 作为跨平台后端。");

        if (_backend.Options.EnableVideoToolboxZeroCopy)
            throw new NotSupportedException(
                "[APPLE-VID] 零拷贝上屏需 C0 Metal 消费侧 IOSurface→MTLTexture 生产者（IGpuFrameProducer）注入，" +
                "当前未接线；请关闭 EnableVideoToolboxZeroCopy 或待 C0 完成后启用。");

        if (codec != VideoCodec.H264 && codec != VideoCodec.H265)
            throw new NotSupportedException($"Apple 视频解码器仅支持 H264 / HEVC，收到 {codec}。");

        _codecType = codec;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        bool isHevc = codec == VideoCodec.H265;
        var cfg = AvcConfig.Parse(settings.CodecConfiguration, isHevc);
        if (cfg is null || cfg.Sps.Count == 0 || cfg.Pps.Count == 0)
            throw new NotSupportedException(
                "[APPLE-VID] 轨道缺少有效 SPS/PPS（CodecConfiguration 为空或非标准 avcC/hvcC），无法构建 VideoToolbox 格式描述。");

        BuildSession(cfg, isHevc);
        _initialized = true;
        _logger.LogInformation("[APPLE-VID] 初始化完成: {Codec}, SPS={Sps} PPS={Pps} VPS={Vps}, NAL长度={Nal}",
            codec, cfg.Sps.Count, cfg.Pps.Count, cfg.Vps.Count, cfg.NalLengthSize);
    }

    private void BuildSession(AvcConfig cfg, bool isHevc)
    {
        // 参数集顺序：H264=[SPS,PPS]；HEVC=[VPS,SPS,PPS]
        var allSets = isHevc
            ? cfg.Vps.Concat(cfg.Sps).Concat(cfg.Pps).ToList()
            : cfg.Sps.Concat(cfg.Pps).ToList();

        var handles = new GCHandle[allSets.Count];
        var ptrs = new nint[allSets.Count];
        var sizes = new nuint[allSets.Count];
        try
        {
            for (int i = 0; i < allSets.Count; i++)
            {
                handles[i] = GCHandle.Alloc(allSets[i], GCHandleType.Pinned);
                ptrs[i] = handles[i].AddrOfPinnedObject();
                sizes[i] = (nuint)allSets[i].Length;
            }

            nint fmtDesc;
            fixed (nint* pp = ptrs)
            fixed (nuint* ps = sizes)
            {
                int st = isHevc
                    ? AppleRuntime.CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                        AppleRuntime.kCFAllocatorNull, (nuint)allSets.Count, (nint)pp, (nint)ps,
                        cfg.NalLengthSize, nint.Zero, out fmtDesc)
                    : AppleRuntime.CMVideoFormatDescriptionCreateFromH264ParameterSets(
                        AppleRuntime.kCFAllocatorNull, (nuint)allSets.Count, (nint)pp, (nint)ps,
                        cfg.NalLengthSize, out fmtDesc);
                if (st != AppleRuntime.noErr || fmtDesc == nint.Zero)
                    throw new NotSupportedException($"[APPLE-VID] CMVideoFormatDescription 创建失败 (status={st})。");
            }

            _formatDescription = fmtDesc;

            // 目标像素格式：NV12（VideoToolbox 默认；与 SW 路径 SoftwareFrameResource.NV12 一致）
            nint destAttrs = AppleAvFoundation.CreatePixelBufferAttributes(
                AppleRuntime.kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange, iosurface: false);

            _gcHandle = GCHandle.Alloc(this);
            var cb = new AppleRuntime.VTDecompressionOutputCallbackRecord
            {
                decompressionOutputCallback =
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, uint, nint, nint, nint, void>)&OnDecompressed,
                decompressionOutputRefCon = GCHandle.ToIntPtr(_gcHandle),
            };

            int vst;
            unsafe
            {
                var cp = &cb;
                vst = AppleRuntime.VTDecompressionSessionCreate(
                    AppleRuntime.kCFAllocatorNull, fmtDesc, nint.Zero, destAttrs, (nint)cp, out _session);
            }

            AppleRuntime.CFRelease(destAttrs); // session 已按需在内部 retain

            if (vst != AppleRuntime.noErr || _session == nint.Zero)
                throw new NotSupportedException($"[APPLE-VID] VTDecompressionSession 创建失败 (status={vst})。");
        }
        finally
        {
            foreach (var h in handles)
                if (h.IsAllocated) h.Free();
        }
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask; // 实际初始化已在 Initialize(VideoCodec, VideoSettings) 完成
    }

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        if (packet is null) return new ValueTask<VideoFrame?>(ReadOutput());

        _pendingInput.Enqueue(packet);
        FeedAndDecode();
        return new ValueTask<VideoFrame?>(ReadOutput());
    }

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        FeedAndDecode(); // 排空剩余输入（同步解码下通常已空）
        return new ValueTask<VideoFrame?>(ReadOutput());
    }

    /// <inheritdoc/>
    public void Reset()
    {
        if (_session == nint.Zero) return;
        DrainAndDispose(_pendingInput);
        DrainAndDispose(_pendingFrames);
        _pendingInput.Clear();
        _pendingFrames.Clear();
    }

    private void FeedAndDecode()
    {
        while (_pendingInput.Count > 0)
        {
            var pkt = _pendingInput.Dequeue();
            try
            {
                DecodeOne(pkt);
            }
            finally
            {
                pkt.Dispose();
            }
        }
    }

    private void DecodeOne(MediaPacket pkt)
    {
        // 同步解码：回调在 DecodeFrame 内触发，借实例字段把当前包时间戳带给回调
        _currentPts = pkt.Timestamp;
        _currentDur = pkt.Duration;
        _currentKey = pkt.KeyFrame;

        byte[] data = pkt.Data.ToArray();
        int total = data.Length;
        if (total == 0) return;

        fixed (byte* p = data)
        {
            int st = AppleRuntime.CMBlockBufferCreateWithMemoryBlock(
                AppleRuntime.kCFAllocatorNull, (nint)p, (nuint)total, AppleRuntime.kCFAllocatorNull,
                nint.Zero, 0, (nuint)total, 0, out nint blockBuffer);
            if (st != AppleRuntime.noErr || blockBuffer == nint.Zero) return;

            try
            {
                var timing = new AppleRuntime.CMSampleTimingInfo
                {
                    duration = AppleRuntime.CMTime.FromTicks(pkt.Duration.Ticks),
                    presentationTimeStamp = AppleRuntime.CMTime.FromTicks(pkt.Timestamp.Ticks),
                    decodeTimeStamp = AppleRuntime.CMTime.Invalid(),
                };
                long size = total;
                int st2;
                nint sbuf;
                unsafe
                {
                    var tp = &timing;
                    var sp = &size;
                    st2 = AppleRuntime.CMSampleBufferCreateReady(
                        AppleRuntime.kCFAllocatorNull, blockBuffer, _formatDescription,
                        (nint)1, (nint)1, (nint)tp, (nint)1, (nint)sp, out sbuf);
                }
                if (st2 != AppleRuntime.noErr || sbuf == nint.Zero) return;

                try
                {
                    // 不清算异步/时域标志 → 回调在返回前同步触发
                    _ = AppleRuntime.VTDecompressionSessionDecodeFrame(_session, sbuf, 0, nint.Zero, nint.Zero);
                }
                finally
                {
                    AppleRuntime.CFRelease(sbuf);
                }
            }
            finally
            {
                AppleRuntime.CFRelease(blockBuffer);
            }
        }
    }

    private VideoFrame? ReadOutput()
        => _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDecompressed(nint refCon, nint sourceFrameRefCon, int status, uint infoFlags,
        nint imageBuffer, nint ptsPtr, nint durPtr)
    {
        if (refCon == nint.Zero) return;
        var dec = GCHandle.FromIntPtr(refCon).Target as AppleVideoDecoder;
        dec?.OnFrameDecoded(status, imageBuffer);
    }

    private void OnFrameDecoded(int status, nint imageBuffer)
    {
        // imageBuffer 是 session 借出的引用（回调期间有效），不在此释放；仅同步拷贝后丢弃
        if (status != AppleRuntime.noErr || imageBuffer == nint.Zero) return;
        VideoFrame? frame = CopyToSoftwareFrame(imageBuffer);
        if (frame is not null) _pendingFrames.Enqueue(frame);
    }

    private unsafe VideoFrame? CopyToSoftwareFrame(nint imageBuffer)
    {
        uint fmt = AppleRuntime.CVPixelBufferGetPixelFormatType(imageBuffer);
        int w = (int)AppleRuntime.CVPixelBufferGetWidth(imageBuffer);
        int h = (int)AppleRuntime.CVPixelBufferGetHeight(imageBuffer);

        int lockSt = AppleRuntime.CVPixelBufferLockBaseAddress(imageBuffer, 0);
        if (lockSt != AppleRuntime.noErr) return null;
        try
        {
            if (fmt == AppleRuntime.kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
                || fmt == AppleRuntime.kCVPixelFormatType_420YpCbCr8BiPlanarFullRange)
            {
                int yStride = (int)AppleRuntime.CVPixelBufferGetBytesPerRowOfPlane(imageBuffer, 0);
                int uvStride = (int)AppleRuntime.CVPixelBufferGetBytesPerRowOfPlane(imageBuffer, 1);
                nint yPtr = AppleRuntime.CVPixelBufferGetBaseAddressOfPlane(imageBuffer, 0);
                nint uvPtr = AppleRuntime.CVPixelBufferGetBaseAddressOfPlane(imageBuffer, 1);

                int ySize = w * h;
                int uvSize = w * h / 2;
                var res = new SoftwareFrameResource(w, h, PixelFormat.NV12, ySize + uvSize);
                Span<byte> dst = res.Data.Span;
                for (int r = 0; r < h; r++)
                    new ReadOnlySpan<byte>((void*)(yPtr + r * yStride), w).CopyTo(dst.Slice(r * w, w));
                for (int r = 0; r < h / 2; r++)
                    new ReadOnlySpan<byte>((void*)(uvPtr + r * uvStride), w).CopyTo(dst.Slice(ySize + r * w, w));

                return new VideoFrame(w, h, PixelFormat.NV12, res, _currentPts, _currentDur, _currentKey);
            }

            if (fmt == AppleRuntime.kCVPixelFormatType_32BGRA)
            {
                int stride = (int)AppleRuntime.CVPixelBufferGetBytesPerRow(imageBuffer);
                nint basePtr = AppleRuntime.CVPixelBufferGetBaseAddress(imageBuffer);
                int size = w * h * 4;
                var res = new SoftwareFrameResource(w, h, PixelFormat.BGRA32, size);
                Span<byte> dst = res.Data.Span;
                for (int r = 0; r < h; r++)
                    new ReadOnlySpan<byte>((void*)(basePtr + r * stride), w * 4).CopyTo(dst.Slice(r * w * 4, w * 4));

                return new VideoFrame(w, h, PixelFormat.BGRA32, res, _currentPts, _currentDur, _currentKey);
            }

            _logger.LogWarning("[APPLE-VID] 不支持的 CVPixelBuffer 像素格式 0x{Format:X}，丢弃帧", fmt);
            return null;
        }
        finally
        {
            AppleRuntime.CVPixelBufferUnlockBaseAddress(imageBuffer, 0);
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized || _session == nint.Zero)
            throw new InvalidOperationException("AppleVideoDecoder 尚未 Initialize。");
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

        if (_session != nint.Zero)
        {
            AppleRuntime.VTDecompressionSessionInvalidate(_session);
            AppleRuntime.CFRelease(_session);
            _session = nint.Zero;
        }
        if (_formatDescription != nint.Zero)
        {
            AppleRuntime.CFRelease(_formatDescription);
            _formatDescription = nint.Zero;
        }
        if (_gcHandle.IsAllocated) _gcHandle.Free();
    }
}

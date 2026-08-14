using System.Runtime.Versioning;
using LingFan.Media.Backends.MediaCodec.Interop;
using LingFan.Media.Backends.MediaCodec.Wrappers;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// 基于 Android NDK <c>AMediaCodec</c> 的视频解码器（ByteBuffer 软件输出路径）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：<see cref="DecodeAsync"/> / <see cref="FlushAsync"/> 为热路径，内部为同步原生调用，
/// 返回 <see cref="ValueTask.FromResult{TResult}"/>（与 FFmpegVideoDecoder 同构）。<see cref="Initialize"/> 为同步原生初始化。</para>
/// <para><b>解码循环</b>：与 FFmpeg send/receive 同构——待喂入包队列 <c>_pendingInput</c> + 已解出帧 FIFO
/// <c>_pendingFrames</c>；单包可能解出 0/1/多帧（B 帧重排），多出的帧入 FIFO 留待下次返回，绝不丢弃。</para>
/// <para><b>输入格式</b>：仅依赖 <see cref="VideoSettings.CodecConfiguration"/>（csd-0，来自轨道 extradata）构造解码格式；
/// 不设置 width/height（由解码器从 csd-0 推导，符合 Android 标准 pattern）。若设备要求显式宽高而未提供，
/// <c>configure</c> 会抛错，由调用方捕获（诚实失败，绝不假绿）。</para>
/// <para><b>输出像素格式</b>：声明支持 NV12（半平面）与 I420（三平面）两类 YUV420。ByteBuffer 模式下按 AOSP
/// 文档以 <c>stride</c> / <c>slice-height</c> / <c>crop-*</c> 键描述的布局提取（Google 软件解码器与多数设备遵循）；
/// 偏离该规范的厂商解码器需 AMediaImage（列为未来增强，不在本后端范围）。零拷贝 AHardwareBuffer/Surface 路径尚未落地，
/// 由 <see cref="AndroidOptions.EnableHardwareBufferZeroCopy"/> 门控，当前恒回落软件路径。</para>
/// <para><b>仅 Android 可用</b>：非 Android 运行时 <see cref="Initialize"/> 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2050",
    Justification = "无 [ComImport]，使用原始 [LibraryImport] P/Invoke，不会被裁剪器移除。仅 Android 运行时使用。")]
internal sealed class AndroidVideoDecoder : IVideoDecoder
{
    private readonly AndroidBackend _backend;
    private readonly ILogger<AndroidVideoDecoder> _logger;

    private AndroidMediaCodec? _codec;
    private AndroidMediaFormat? _outputFormat;       // 当前输出格式（OUTPUT_FORMAT_CHANGED 时更新）
    private PixelFormat _outputPixelFormat = PixelFormat.YUV420P;
    private int _colorFormat;

    private readonly Queue<MediaPacket> _pendingInput = new();
    private readonly Queue<VideoFrame> _pendingFrames = new();

    private VideoCodec _codecType = VideoCodec.Unknown;
    private VideoSettings _settings = null!;
    private bool _initialized;
    private bool _disposed;

    public AndroidVideoDecoder(AndroidBackend backend, ILogger<AndroidVideoDecoder> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public VideoCodec Codec => _codecType;

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false; // 当前仅软件 ByteBuffer 路径；零拷贝 Surface 未落地

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

        var codecObj = AndroidMediaCodec.CreateDecoderByType(mime);
        try
        {
            // 构造输入格式：mime + csd-0（来自轨道 extradata）。不设置 width/height（由 csd 推导）。
            var fmt = new AndroidMediaFormat();
            fmt.SetString(AndroidMediaConstants.KEY_MIME, mime);

            var csd = settings.CodecConfiguration;
            if (csd.Length > 0)
                fmt.SetBuffer(AndroidMediaConstants.KEY_CSD_0, csd.ToArray());

            codecObj.Configure(fmt, nint.Zero, nint.Zero, 0); // surface=0 → ByteBuffer 路径
            fmt.Dispose();

            codecObj.Start();

            // 读取输出格式，校验支持的像素格式（不支持则快速诚实失败）
            _outputFormat = codecObj.GetOutputFormat();
            ReadOutputFormat(_outputFormat);
        }
        catch
        {
            codecObj.Dispose();
            throw;
        }

        _codec = codecObj;
        _initialized = true;
        _logger.LogInformation("[ANDROID-VID] 初始化完成: {Codec} → {Mime}, 输出像素格式 {Fmt}", codec, mime, _outputPixelFormat);
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // 实际初始化已在 Initialize(VideoCodec, VideoSettings) 完成（原生同步调用）。
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ReadOutputFormat(AndroidMediaFormat fmt)
    {
        if (fmt.TryGetInt32(AndroidMediaConstants.KEY_COLOR_FORMAT, out int cf))
            _colorFormat = cf;
        else
            _colorFormat = AndroidMediaConstants.COLOR_FormatYUV420Flexible; // 兜底

        var pf = AndroidCodecMaps.ColorFormatToPixelFormat(_colorFormat);
        if (pf is null)
            throw new NotSupportedException(
                $"Android 视频解码器不支持的输出颜色格式 0x{_colorFormat:X}（当前仅支持 NV12 / I420 两类 YUV420）。");
        _outputPixelFormat = pf.Value;
    }

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        if (packet is null) return new ValueTask<VideoFrame?>(ReadOutput());

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
        nint inIdx = _codec!.DequeueInputBuffer(1000);
        if (inIdx >= 0)
        {
            _codec.QueueInputBuffer((nuint)inIdx, 0, 0, 0, AndroidMediaConstants.AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM);
        }

        // 排空输出（带超时，允许解码器产出尾帧）
        return new ValueTask<VideoFrame?>(DrainOutput(10_000));
    }

    /// <inheritdoc/>
    public void Reset()
    {
        if (_codec is null) return;
        _codec.Flush();
        DrainAndDispose(_pendingInput);
        DrainAndDispose(_pendingFrames);
        _pendingInput.Clear();
        _pendingFrames.Clear();
    }

    /// <summary>尽可能把待喂入包拷入解码器输入槽。</summary>
    private void FeedInput()
    {
        while (_pendingInput.Count > 0)
        {
            nint idx = _codec!.DequeueInputBuffer(0);
            if (idx < 0) break; // 暂无输入槽，保留包待下次

            var pkt = _pendingInput.Dequeue();
            try
            {
                nint buf = _codec.GetInputBuffer((nuint)idx, out nuint cap);
                if (buf == nint.Zero) continue; // 异常：跳过该包

                int len = (int)Math.Min(pkt.Data.Length, (long)cap);
                if (len != pkt.Data.Length)
                    _logger.LogWarning("[ANDROID-VID] 输入 buffer 容量({Cap})小于包大小({Len})，截断喂入", (long)cap, pkt.Data.Length);

                // 托管只读内存 → 原生输入 buffer（4 参托管重载，无需 unsafe）
                if (MemoryMarshal.TryGetArray(pkt.Data, out ArraySegment<byte> seg) && seg.Array is not null)
                    Marshal.Copy(seg.Array, seg.Offset, buf, len);
                else
                    Marshal.Copy(pkt.Data.ToArray(), 0, buf, len);

                ulong ptsUs = pkt.Timestamp.Ticks > 0 ? (ulong)(pkt.Timestamp.Ticks / 10) : 0;
                _codec.QueueInputBuffer((nuint)idx, 0, (nuint)len, ptsUs, 0);
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
        if (_pendingFrames.Count > 0)
            return _pendingFrames.Dequeue();
        return DrainOutput(0);
    }

    /// <summary>排空解码器输出，将可用帧入 FIFO，返回队首（超时内无帧返回 null）。</summary>
    private VideoFrame? DrainOutput(long timeoutUs)
    {
        while (true)
        {
            nint idx = _codec!.DequeueOutputBuffer(out AMediaCodecBufferInfo info, timeoutUs);
            if (idx == AndroidMediaConstants.AMEDIACODEC_INFO_TRY_AGAIN_LATER)
                break;
            if (idx == AndroidMediaConstants.AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED)
            {
                _outputFormat?.Dispose();
                _outputFormat = _codec.GetOutputFormat();
                ReadOutputFormat(_outputFormat);
                continue;
            }
            if (idx == AndroidMediaConstants.AMEDIACODEC_INFO_OUTPUT_BUFFERS_CHANGED)
                continue; // NDK 下无需处理
            if (idx < 0)
                break; // 其他负值：保守返回 null

            if ((info.flags & AndroidMediaConstants.AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM) != 0)
            {
                _codec.ReleaseOutputBuffer((nuint)idx, 0);
                break;
            }

            // 0 字节 buffer 仅承载 EOS/标记（NDK 明确），无有效帧数据，丢弃以免按 crop 尺寸越界拷贝
            if (info.size <= 0)
            {
                _codec.ReleaseOutputBuffer((nuint)idx, 0);
                continue;
            }

            var frame = ExtractFrame((nuint)idx, info);
            _codec.ReleaseOutputBuffer((nuint)idx, 0); // render=0：仅 CPU 拷贝，不送显存
            _pendingFrames.Enqueue(frame);
        }

        return _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;
    }

    /// <summary>从输出 buffer 按 AOSP stride/slice-height/crop 布局提取 YUV 帧。</summary>
    private unsafe VideoFrame ExtractFrame(nuint idx, AMediaCodecBufferInfo info)
    {
        nint buf = _codec!.GetOutputBuffer(idx, out nuint _);
        if (buf == nint.Zero)
            throw new InvalidOperationException("[ANDROID-VID] getOutputBuffer 返回 null");

        int fullW = 0, fullH = 0, stride = 0, sliceH = 0;
        int cropL = 0, cropT = 0, cropR = 0, cropB = 0;
        bool hasCrop = false;

        if (_outputFormat is not null)
        {
            _outputFormat.TryGetInt32(AndroidMediaConstants.KEY_WIDTH, out fullW);
            _outputFormat.TryGetInt32(AndroidMediaConstants.KEY_HEIGHT, out fullH);
            _outputFormat.TryGetInt32(AndroidMediaConstants.KEY_STRIDE, out stride);
            _outputFormat.TryGetInt32(AndroidMediaConstants.KEY_SLICE_HEIGHT, out sliceH);
            if (_outputFormat.TryGetInt32(AndroidMediaConstants.KEY_CROP_RIGHT, out cropR)
                && _outputFormat.TryGetInt32(AndroidMediaConstants.KEY_CROP_BOTTOM, out cropB)
                && _outputFormat.TryGetInt32(AndroidMediaConstants.KEY_CROP_LEFT, out cropL)
                && _outputFormat.TryGetInt32(AndroidMediaConstants.KEY_CROP_TOP, out cropT))
            {
                hasCrop = true;
            }
        }

        // 兜底：缺失 stride/slice 时按紧密打包处理
        if (stride <= 0) stride = fullW;
        if (sliceH <= 0) sliceH = fullH;
        if (fullW <= 0 || fullH <= 0)
            throw new InvalidOperationException("[ANDROID-VID] 输出格式缺少 width/height");

        int cropW = hasCrop ? (cropR - cropL + 1) : fullW;
        int cropH = hasCrop ? (cropB - cropT + 1) : fullH;
        cropW = Math.Max(cropW, 1);
        cropH = Math.Max(cropH, 1);

        int ySize = cropW * cropH;
        int uvSize = (_outputPixelFormat == PixelFormat.NV12)
            ? cropW * cropH          // NV12：UV 交织单平面，同 Y 尺寸
            : (cropW / 2) * (cropH / 2) * 2; // I420：U + V 两平面
        int total = ySize + uvSize;

        var resource = new SoftwareFrameResource(cropW, cropH, _outputPixelFormat, total);
        Span<byte> dst = resource.Data.Span;

        bool key = (info.flags & AndroidMediaConstants.AMEDIACODEC_BUFFER_FLAG_KEY_FRAME) != 0;
        var pts = info.presentationTimeUs >= 0
            ? TimeSpan.FromTicks(info.presentationTimeUs * 10)
            : TimeSpan.Zero;

        unsafe
        {
            // info.offset 是帧数据在输出 buffer 内的起始偏移（NDK 规范），须加偏移再取数据
            byte* src = (byte*)(buf + info.offset);
            if (_outputPixelFormat == PixelFormat.NV12)
                ExtractNV12(src, stride, sliceH, cropL, cropT, cropW, cropH, dst);
            else
                ExtractI420(src, stride, sliceH, cropL, cropT, cropW, cropH, dst);
        }

        return new VideoFrame(cropW, cropH, _outputPixelFormat, resource, pts, TimeSpan.Zero, key);
    }

    /// <summary>从 NV12 半平面布局提取（Y 平面 + 交织 UV 半平面）。</summary>
    private static unsafe void ExtractNV12(byte* src, int stride, int sliceH, int cropL, int cropT, int w, int h, Span<byte> dst)
    {
        int yPlaneSize = stride * sliceH;
        // Y
        for (int row = 0; row < h; row++)
        {
            int srcRow = cropT + row;
            int srcOff = srcRow * stride + cropL;
            int dstOff = row * w;
            new ReadOnlySpan<byte>(src + srcOff, w).CopyTo(dst.Slice(dstOff, w));
        }
        // UV（半平面；4:2:0 垂直 2:1 子采样，色度行 = (cropT+row)/2；每行 w 字节，行步长 = stride）
        int uvSrcBase = yPlaneSize; // UV 起点
        for (int row = 0; row < h; row++)
        {
            int chromaRow = (cropT + row) / 2;
            int srcOff = uvSrcBase + chromaRow * stride + cropL;
            int dstOff = w * h + row * w;
            new ReadOnlySpan<byte>(src + srcOff, w).CopyTo(dst.Slice(dstOff, w));
        }
    }

    /// <summary>从 I420 三平面布局提取（Y + U + V）。</summary>
    private static unsafe void ExtractI420(byte* src, int stride, int sliceH, int cropL, int cropT, int w, int h, Span<byte> dst)
    {
        int yPlaneSize = stride * sliceH;
        int chromaStride = stride / 2;
        int chromaSlice = sliceH / 2;
        int chromaPlaneSize = chromaStride * chromaSlice;
        int uvBase = yPlaneSize;

        // Y
        for (int row = 0; row < h; row++)
        {
            int srcOff = (cropT + row) * stride + cropL;
            int dstOff = row * w;
            new ReadOnlySpan<byte>(src + srcOff, w).CopyTo(dst.Slice(dstOff, w));
        }
        // U
        int cw = w / 2, ch = h / 2;
        for (int row = 0; row < ch; row++)
        {
            int srcOff = uvBase + (cropT / 2 + row) * chromaStride + cropL / 2;
            int dstOff = w * h + row * cw;
            new ReadOnlySpan<byte>(src + srcOff, cw).CopyTo(dst.Slice(dstOff, cw));
        }
        // V
        int vBase = uvBase + chromaPlaneSize;
        for (int row = 0; row < ch; row++)
        {
            int srcOff = vBase + (cropT / 2 + row) * chromaStride + cropL / 2;
            int dstOff = w * h + cw * ch + row * cw;
            new ReadOnlySpan<byte>(src + srcOff, cw).CopyTo(dst.Slice(dstOff, cw));
        }
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
        _codec?.Dispose();
        _codec = null;
        _outputFormat?.Dispose();
        _outputFormat = null;
    }
}

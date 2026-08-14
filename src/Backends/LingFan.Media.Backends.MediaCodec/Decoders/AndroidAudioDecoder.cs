using System.Runtime.Versioning;
using LingFan.Media.Backends.MediaCodec.Interop;
using LingFan.Media.Backends.MediaCodec.Wrappers;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// 基于 Android NDK <c>AMediaCodec</c> 的音频解码器（ByteBuffer 软件输出路径，输出 PCM）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：<see cref="DecodeAsync"/> / <see cref="FlushAsync"/> 为热路径，内部同步原生调用，
/// 返回 <see cref="ValueTask.FromResult{TResult}"/>（与 FFmpegAudioDecoder 同构）。</para>
/// <para><b>解码循环</b>：与视频解码器同构——<c>_pendingInput</c> + <c>_pendingFrames</c> FIFO；单包解出 0/1 帧。</para>
/// <para><b>输出 PCM</b>：MediaCodec 在 ByteBuffer 模式直接吐解码后 PCM；采样格式由输出格式
/// <c>pcm-encoding</c> 决定（16-bit / float / 32-bit → S16/F32/S32）。8-bit PCM 与 24-bit 打包（3 字节/样本）
/// 无对应枚举，按“绝不假绿”原则拒绝（<see cref="NotSupportedException"/>）。</para>
/// <para><b>仅 Android 可用</b>：非 Android 运行时 <see cref="Initialize"/> 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2050",
    Justification = "无 [ComImport]，使用原始 [LibraryImport] P/Invoke，不会被裁剪器移除。仅 Android 运行时使用。")]
internal sealed class AndroidAudioDecoder : IAudioDecoder
{
    private readonly AndroidBackend _backend;
    private readonly ILogger<AndroidAudioDecoder> _logger;

    private AndroidMediaCodec? _codec;
    private readonly Queue<MediaPacket> _pendingInput = new();
    private readonly Queue<AudioFrame> _pendingFrames = new();

    private AudioCodec _codecType = AudioCodec.Unknown;
    private AudioSettings _settings = null!;
    private SampleFormat _sampleFormat = SampleFormat.S16;
    private int _outputSampleRate;
    private int _outputChannels;
    private bool _initialized;
    private bool _disposed;

    public AndroidAudioDecoder(AndroidBackend backend, ILogger<AndroidAudioDecoder> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public AudioCodec Codec => _codecType;

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <inheritdoc/>
    public int OutputSampleRate => _outputSampleRate;

    /// <inheritdoc/>
    public int OutputChannels => _outputChannels;

    /// <inheritdoc/>
    public void Initialize(AudioCodec codec, AudioSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("AndroidAudioDecoder 已初始化，不可重复 Initialize。");

        if (!OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException(
                "Android 音频解码器仅支持 Android 运行时。请使用 FFmpeg 作为跨平台后端。");

        string? mime = AndroidCodecMaps.AudioCodecToMime(codec);
        if (mime is null)
            throw new NotSupportedException($"Android MediaCodec 不支持的音频编解码器: {codec}");

        _codecType = codec;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        var codecObj = AndroidMediaCodec.CreateDecoderByType(mime);
        try
        {
            var fmt = new AndroidMediaFormat();
            fmt.SetString(AndroidMediaConstants.KEY_MIME, mime);

            var csd = settings.CodecConfiguration;
            if (csd.Length > 0)
                fmt.SetBuffer(AndroidMediaConstants.KEY_CSD_0, csd.ToArray());

            codecObj.Configure(fmt, nint.Zero, nint.Zero, 0);
            fmt.Dispose();
            codecObj.Start();

            // 输出格式：采样率 / 声道数 / PCM 编码（采样格式）
            var outFmt = codecObj.GetOutputFormat();
            try
            {
                if (outFmt.TryGetInt32(AndroidMediaConstants.KEY_SAMPLE_RATE, out int sr) && sr > 0)
                    _outputSampleRate = sr;
                if (outFmt.TryGetInt32(AndroidMediaConstants.KEY_CHANNEL_COUNT, out int ch) && ch > 0)
                    _outputChannels = ch;
                if (outFmt.TryGetInt32(AndroidMediaConstants.KEY_PCM_ENCODING, out int enc))
                {
                    var sf = AndroidCodecMaps.PcmEncodingToSampleFormat(enc);
                    if (sf is null)
                        throw new NotSupportedException(
                            $"Android 音频解码器不支持的 pcm-encoding {enc}（当前仅支持 16-bit / float / 24-bit-packed / 32-bit，不含 8-bit）。");
                    _sampleFormat = sf.Value;
                }
                else
                {
                    _sampleFormat = SampleFormat.S16; // 缺省兜底（绝大多数设备输出 16-bit）
                }
            }
            finally
            {
                outFmt.Dispose();
            }
        }
        catch
        {
            codecObj.Dispose();
            throw;
        }

        _codec = codecObj;
        _initialized = true;
        _logger.LogInformation("[ANDROID-AUD] 初始化完成: {Codec} → {Mime}, {Rate}Hz/{Ch}ch, {Fmt}",
            codec, mime, _outputSampleRate, _outputChannels, _sampleFormat);
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        if (packet is null) return new ValueTask<AudioFrame?>(ReadOutput());

        _pendingInput.Enqueue(packet);
        FeedInput();
        return new ValueTask<AudioFrame?>(ReadOutput());
    }

    /// <inheritdoc/>
    public ValueTask<AudioFrame?> FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        FeedInput();
        nint inIdx = _codec!.DequeueInputBuffer(1000);
        if (inIdx >= 0)
            _codec.QueueInputBuffer((nuint)inIdx, 0, 0, 0, AndroidMediaConstants.AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM);

        return new ValueTask<AudioFrame?>(DrainOutput(10_000));
    }

    /// <inheritdoc/>
    public void Reset()
    {
        if (_codec is null) return;
        _codec.Flush();
        while (_pendingInput.Count > 0) _pendingInput.Dequeue().Dispose();
        while (_pendingFrames.Count > 0) _pendingFrames.Dequeue().Dispose();
        _pendingInput.Clear();
        _pendingFrames.Clear();
    }

    private void FeedInput()
    {
        while (_pendingInput.Count > 0)
        {
            nint idx = _codec!.DequeueInputBuffer(0);
            if (idx < 0) break;

            var pkt = _pendingInput.Dequeue();
            try
            {
                nint buf = _codec.GetInputBuffer((nuint)idx, out nuint cap);
                if (buf == nint.Zero) continue;

                int len = (int)Math.Min(pkt.Data.Length, (long)cap);

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

    private AudioFrame? ReadOutput()
    {
        if (_pendingFrames.Count > 0) return _pendingFrames.Dequeue();
        return DrainOutput(0);
    }

    private AudioFrame? DrainOutput(long timeoutUs)
    {
        while (true)
        {
            nint idx = _codec!.DequeueOutputBuffer(out AMediaCodecBufferInfo info, timeoutUs);
            if (idx == AndroidMediaConstants.AMEDIACODEC_INFO_TRY_AGAIN_LATER) break;
            if (idx == AndroidMediaConstants.AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED)
            {
                // 音频采样率/声道可能随流变化；重新读取输出格式
                var outFmt = _codec.GetOutputFormat();
                try { RefreshOutputFormat(outFmt); } finally { outFmt.Dispose(); }
                continue;
            }
            if (idx == AndroidMediaConstants.AMEDIACODEC_INFO_OUTPUT_BUFFERS_CHANGED) continue;
            if (idx < 0) break;

            if ((info.flags & AndroidMediaConstants.AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM) != 0)
            {
                _codec.ReleaseOutputBuffer((nuint)idx, 0);
                break;
            }

            // 0 字节 buffer 仅承载 EOS/标记，无有效 PCM，丢弃
            if (info.size <= 0)
            {
                _codec.ReleaseOutputBuffer((nuint)idx, 0);
                continue;
            }

            var frame = ExtractFrame((nuint)idx, info);
            _codec.ReleaseOutputBuffer((nuint)idx, 0);
            _pendingFrames.Enqueue(frame);
        }

        return _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;
    }

    private void RefreshOutputFormat(AndroidMediaFormat outFmt)
    {
        if (outFmt.TryGetInt32(AndroidMediaConstants.KEY_SAMPLE_RATE, out int sr) && sr > 0)
            _outputSampleRate = sr;
        if (outFmt.TryGetInt32(AndroidMediaConstants.KEY_CHANNEL_COUNT, out int ch) && ch > 0)
            _outputChannels = ch;
        if (outFmt.TryGetInt32(AndroidMediaConstants.KEY_PCM_ENCODING, out int enc))
        {
            var sf = AndroidCodecMaps.PcmEncodingToSampleFormat(enc);
            if (sf is not null) _sampleFormat = sf.Value;
        }
    }

    private AudioFrame ExtractFrame(nuint idx, AMediaCodecBufferInfo info)
    {
        nint buf = _codec!.GetOutputBuffer(idx, out nuint _);
        if (buf == nint.Zero)
            throw new InvalidOperationException("[ANDROID-AUD] getOutputBuffer 返回 null");

        int validBytes = info.size <= 0 ? 0 : (int)info.size;
        var pcm = new byte[validBytes];
        if (validBytes > 0)
            // info.offset 是 PCM 数据在输出 buffer 内的起始偏移（NDK 规范），须加偏移再拷贝
            Marshal.Copy(buf + info.offset, pcm, 0, validBytes);

        int bps = AndroidCodecMaps.BytesPerSample(_sampleFormat);
        int frameCount = bps > 0 && _outputChannels > 0
            ? validBytes / (bps * _outputChannels)
            : 0;

        var ts = info.presentationTimeUs >= 0
            ? TimeSpan.FromTicks(info.presentationTimeUs * 10)
            : TimeSpan.Zero;

        return new AudioFrame(pcm, _outputSampleRate, _outputChannels, _sampleFormat, ts, TimeSpan.Zero, frameCount);
    }

    private void EnsureInitialized()
    {
        if (!_initialized || _codec is null)
            throw new InvalidOperationException("AndroidAudioDecoder 尚未 Initialize。");
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
        while (_pendingInput.Count > 0) _pendingInput.Dequeue().Dispose();
        while (_pendingFrames.Count > 0) _pendingFrames.Dequeue().Dispose();
        _codec?.Dispose();
        _codec = null;
    }
}

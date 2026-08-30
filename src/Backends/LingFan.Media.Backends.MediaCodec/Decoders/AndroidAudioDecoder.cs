using System.Runtime.InteropServices;
using Android.Media;
using Java.Nio;
// 本后端命名空间段为 ...MediaCodec，会遮蔽类型 Android.Media.MediaCodec → 用不撞名的别名。
using AndroidMediaCodec = Android.Media.MediaCodec;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// 基于托管 <see cref="AndroidMediaCodec"/> 的音频解码器（ByteBuffer 软件输出路径，输出 PCM；net-android 内置，非手写 P/Invoke）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：<see cref="DecodeAsync"/> / <see cref="FlushAsync"/> 为热路径，内部同步托管调用，
/// 返回 <see cref="ValueTask.FromResult{TResult}"/>（与 FFmpegAudioDecoder 同构）。</para>
/// <para><b>解码循环</b>：<c>_pendingInput</c> + <c>_pendingFrames</c> FIFO；单包解出 0/1 帧。</para>
/// <para><b>输出 PCM</b>：ByteBuffer 模式直接吐解码后 PCM；采样格式由输出格式 <c>pcm-encoding</c> 决定
/// （16-bit / float / 32-bit → S16/F32/S32）。8-bit 与 24-bit 打包（3 字节/样本）无对应枚举，按「绝不假绿」
/// 原则拒绝（<see cref="NotSupportedException"/>）。</para>
/// <para><b>仅 Android 可用</b>：非 Android 运行时 <see cref="Initialize"/> 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// </remarks>
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

    // MediaCodec dequeue 返回码（int）：-1=TRY_AGAIN、-2=FORMAT_CHANGED、-3=BUFFERS_CHANGED。
    // BufferInfo 的 flags 位（公开 AOSP 值）：1=KEY_FRAME、2=CODEC_CONFIG、4=END_OF_STREAM。
    private const int InfoTryAgainLater = -1;
    private const int InfoOutputFormatChanged = -2;
    private const int InfoOutputBuffersChanged = -3;
    private const int FlagEndOfStream = 4;
    private const int LogInterval = 64;

    // 诊断计数（周期性日志定位 dequeue 是否恒 TRY_AGAIN）
    private long _drainCalls, _drainTryAgain, _drainProduced;
    private int _packetsFed;
    private bool _eosQueued;     // EOS 已入队（FlushAsync 重试语义，Reset 清零）
    private bool _eosOutputSeen; // 解码器已回报输出 EOS（DRAIN 完成判据）

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
            using var fmt = new MediaFormat();
            fmt.SetString(MediaFormat.KeyMime, mime);

            // 部分解码器 configure 要求显式 sample-rate/channel-count，仅 csd-0 推导不足会报错；
            // MediaPlayer 已从轨道信息透传（见 AudioSettings.SourceSampleRate/Channels）。
            if (settings.SourceSampleRate is > 0)
                fmt.SetInteger(MediaFormat.KeySampleRate, settings.SourceSampleRate.Value);
            if (settings.SourceChannels is > 0)
                fmt.SetInteger(MediaFormat.KeyChannelCount, settings.SourceChannels.Value);

            var csd = settings.CodecConfiguration;
            if (csd.Length > 0)
                fmt.SetByteBuffer("csd-0", ByteBuffer.Wrap(csd.ToArray())); // 键 "csd-0"（AOSP KEY_CSD0）

            codecObj.Configure(fmt, null, null, 0); // surface/crypto=null → ByteBuffer 输出
            codecObj.Start();

            // 输出格式：采样率 / 声道数 / PCM 编码（采样格式）。
            // 注意：getOutputFormat 可能返回推测值——HE-AAC v2 码流参数 22050Hz/1ch，start 后初始报
            // 22050Hz/1ch，首帧 FORMAT_CHANGED 后才上报真实 44100Hz/2ch（SBR+PS 上采样）。FORMAT_CHANGED
            // 只在有输入包后触发，真实格式由首帧后的 FORMAT_CHANGED 经 RefreshOutputFormat 刷新，输出端
            // 按帧格式运行时重协商，此处无需也无法探测。
            // getOutputFormat 无参重载在 .NET 绑定映射为 OutputFormat 属性（GetOutputFormat(int) 为按输出缓冲索引的重载）
            using var outFmt = codecObj.OutputFormat;
            ReadOutputParams(outFmt);

            // HE-AAC（SBR/PS）真实输出格式提前纠正：容器/初始 OutputFormat 只暴露核心 LC 参数
            // （如 22050Hz/1ch），SBR 翻倍采样率、PS 上混立体声，首帧 FORMAT_CHANGED 才上报真实值。
            // 此处从 AudioSpecificConfig（csd-0）提前推断，供音频输出以正确格式一次性初始化，
            // 避免首帧重建播放器 + 主时钟重开（起播死区）。推断失败保持原值，帧级重协商兜底（零回归）。
            if (codec == AudioCodec.AAC
                && TryParseAacOutputFormat(settings.CodecConfiguration, out int sbrRate, out int sbrChannels))
            {
                _outputSampleRate = sbrRate;
                _outputChannels = sbrChannels;
            }
        }
        catch
        {
            codecObj.Release();
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

        // 诊断节流：音频收包节奏（确认喂入是否到达解码器）
        if ((_packetsFed % LogInterval) == 0)
            _logger.LogInformation("[ANDROID-AUD] 收包 #{Count} size={Size} pts={Pts:g}", _packetsFed, packet.Data.Length, packet.Timestamp);
        _packetsFed++;

        _pendingInput.Enqueue(packet);
        FeedInput();
        return new ValueTask<AudioFrame?>(ReadOutput());
    }

    /// <inheritdoc/>
    public ValueTask<AudioFrame?> FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        // 先排空待喂入队列
        FeedInput();

        // EOS 入队：输入槽满时先排空输出（释放输出→解码器消费输入→槽释放），带重试。
        // 旧实现单次 1ms 尝试：槽满即放弃 EOS → 尾段帧滞留（与视频解码器同 bug）。
        if (!_eosQueued)
        {
            for (int attempt = 0; attempt < 16 && !_eosQueued; attempt++)
            {
                FeedInput();
                int inIdx = _codec!.DequeueInputBuffer(2_000);
                if (inIdx >= 0)
                {
                    _codec.QueueInputBuffer(inIdx, 0, 0, 0, (MediaCodecBufferFlags)FlagEndOfStream);
                    _eosQueued = true;
                    break;
                }
                _ = DrainOutput(5_000); // 排空输出解锁解码器（产帧入 FIFO）
            }
        }

        return new ValueTask<AudioFrame?>(DrainOutput(_eosQueued && !_eosOutputSeen ? 40_000 : 10_000));
    }

    /// <inheritdoc/>
    public void Reset()
    {
        if (_codec is null) return;
        _codec.Flush();
        _eosQueued = false;
        _eosOutputSeen = false;
        while (_pendingInput.Count > 0) _pendingInput.Dequeue().Dispose();
        while (_pendingFrames.Count > 0) _pendingFrames.Dequeue().Dispose();
        _pendingInput.Clear();
        _pendingFrames.Clear();
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
                    _logger.LogWarning("[ANDROID-AUD] 输入 buffer 容量({Cap})小于包大小({Len})，截断喂入",
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

    private AudioFrame? ReadOutput()
    {
        if (_pendingFrames.Count > 0) return _pendingFrames.Dequeue();
        return DrainOutput(0);
    }

    private AudioFrame? DrainOutput(long timeoutUs)
    {
        _drainCalls++;
        // 规范：dequeue 用阻塞超时。非阻塞（0）会被解码器视为「当前无输出就绪」而恒返回 TRY_AGAIN，
        // 打断帧/采样交付（AAC 软解亦受影响）。与视频 Surface 路径同构，给一个正阻塞预算。
        if (timeoutUs <= 0) timeoutUs = 5_000;
        while (true)
        {
            var info = new AndroidMediaCodec.BufferInfo();
            int idx = _codec!.DequeueOutputBuffer(info, timeoutUs);
            if (idx == InfoTryAgainLater) { _drainTryAgain++; break; }
            if (idx == InfoOutputFormatChanged)
            {
                // 音频采样率/声道可能随流变化；重新读取输出格式（无参重载为 OutputFormat 属性）
                using var outFmt = _codec.OutputFormat;
                ReadOutputParams(outFmt);
                continue;
            }
            if (idx == InfoOutputBuffersChanged) continue;
            if (idx < 0) { _drainTryAgain++; break; }

            if (((int)info.Flags & FlagEndOfStream) != 0)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                _eosOutputSeen = true; // DRAIN 完成判据
                break;
            }

            // 0 字节 buffer 仅承载 EOS/标记，无有效 PCM，丢弃
            if (info.Size <= 0)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                continue;
            }

            var frame = ExtractFrame(idx, info);
            _codec.ReleaseOutputBuffer(idx, false);
            _drainProduced++;
            _pendingFrames.Enqueue(frame);
        }

        // 周期性诊断（定位 audio 是否同样 dequeue 恒 TRY_AGAIN）
        if ((_drainCalls % LogInterval) == 0)
            _logger.LogInformation("[ANDROID-AUD] 诊断: 排空={Calls} tryAgain={Try} 累计产帧={Frames}",
                _drainCalls, _drainTryAgain, _drainProduced);

        return _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;
    }

    private void ReadOutputParams(MediaFormat outFmt)
    {
        if (outFmt.ContainsKey(MediaFormat.KeySampleRate))
            _outputSampleRate = outFmt.GetInteger(MediaFormat.KeySampleRate);
        if (outFmt.ContainsKey(MediaFormat.KeyChannelCount))
            _outputChannels = outFmt.GetInteger(MediaFormat.KeyChannelCount);
        // KeyPcmEncoding 为 API 24+：低版本无此键，保持当前 _sampleFormat。
        if (OperatingSystem.IsAndroidVersionAtLeast(24) && outFmt.ContainsKey(MediaFormat.KeyPcmEncoding))
        {
            var sf = AndroidCodecMaps.PcmEncodingToSampleFormat(outFmt.GetInteger(MediaFormat.KeyPcmEncoding));
            if (sf is not null) _sampleFormat = sf.Value;
        }
    }

    /// <summary>
    /// 从 AAC AudioSpecificConfig（csd-0）前 13 位推断 HE-AAC（SBR/PS）的真实输出采样率/声道数。
    /// 仅处理 audioObjectType=5（SBR，采样率×2）与 29（SBR+PS，采样率×2 且上混立体声）；
    /// 逃逸对象类型（31）、显式/逃逸采样率、PCE 声道配置（0）等罕见情况保守返回 false（不纠正）。
    /// </summary>
    private static bool TryParseAacOutputFormat(ReadOnlyMemory<byte> csd, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 0;
        var s = csd.Span;
        if (s.Length < 2) return false;

        // AudioSpecificConfig 位布局（MSB 优先）：
        //   audioObjectType(5) | samplingFrequencyIndex(4) | channelConfiguration(4) | …
        int audioObjectType = s[0] >> 3;
        if (audioObjectType == 31) return false; // 逃逸对象类型：罕见，保守放弃
        int freqIndex = ((s[0] & 0x07) << 1) | (s[1] >> 7);
        int channelConfig = (s[1] >> 3) & 0x0F;

        int coreRate = freqIndex switch
        {
            0 => 96000, 1 => 88200, 2 => 64000, 3 => 48000, 4 => 44100, 5 => 32000,
            6 => 24000, 7 => 22050, 8 => 16000, 9 => 12000, 10 => 11025, 11 => 8000,
            12 => 7350, _ => 0, // 13/14 显式频率、15 逃逸：保守放弃
        };
        if (coreRate <= 0) return false;

        bool sbr = audioObjectType is 5 or 29;
        if (!sbr) return false; // 非 HE-AAC：无需纠正

        sampleRate = coreRate * 2;
        // SBR+PS(29) 上混为立体声；纯 SBR(5) 保持核心声道数（0=PCE 在带内，保守放弃）。
        channels = audioObjectType == 29
            ? 2
            : channelConfig switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 5, 6 => 6, 7 => 8, _ => 0 };
        if (channels <= 0) return false;
        return true;
    }

    private AudioFrame ExtractFrame(int idx, AndroidMediaCodec.BufferInfo info)
    {
        var buf = _codec!.GetOutputBuffer(idx);
        if (buf is null)
            throw new InvalidOperationException("[ANDROID-AUD] getOutputBuffer 返回 null");

        int validBytes = info.Size <= 0 ? 0 : info.Size;
        var pcm = new byte[validBytes];
        if (validBytes > 0)
        {
            // getOutputBuffer 返回的 buffer position 指向数据起点（info.Offset）；Get 拷贝 Remaining 字节。
            buf.Position(info.Offset);
            buf.Get(pcm);
        }

        int bps = AndroidCodecMaps.BytesPerSample(_sampleFormat);
        int frameCount = bps > 0 && _outputChannels > 0
            ? validBytes / (bps * _outputChannels)
            : 0;

        var ts = info.PresentationTimeUs >= 0
            ? TimeSpan.FromTicks(info.PresentationTimeUs * 10)
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
        _codec?.Release();
        _codec = null;
    }
}
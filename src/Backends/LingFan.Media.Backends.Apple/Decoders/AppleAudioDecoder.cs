using System.Runtime.CompilerServices;
using LingFan.Media.Apple.Shared;

namespace LingFan.Media.Backends.Apple.Decoders;

/// <summary>
/// 基于 Apple AudioToolbox <c>AudioConverter</c> 的音频解码器（<see cref="IAudioDecoder"/>）。
/// </summary>
/// <remarks>
/// <para><b>架构</b>：demuxer 经 <see cref="MediaPacket"/> 传入<b>压缩</b>样本（AAC/MP3 等），
/// 解码器在 <see cref="Initialize"/> 用 <see cref="AudioSettings.CodecConfiguration"/>（AudioSpecificConfig / 私有配置）
/// 作为 magic cookie 构造 <c>AudioConverter</c>，逐包经 <c>AudioConverterFillComplexBuffer</c> 解出 LPCM（S16，原生端序）。</para>
/// <para><b>包驱动（push→pull）</b>：<see cref="DecodeAsync"/> 把压缩包塞入 <c>_pendingInput</c>，
/// 调用 <c>AudioConverterFillComplexBuffer</c>；该 API 经 <see cref="InputProc"/> 回调向本端拉取压缩数据
/// （<c>AudioStreamPacketDescription</c> 描述每个 VBR 包偏移/长度），转换出 PCM 后入 <c>_pendingFrames</c>。</para>
/// <para><b>同步策略（防伪异步）</b>：<see cref="DecodeAsync"/> / <see cref="FlushAsync"/> 为热路径，内部同步原生调用，
/// 返回 <see cref="ValueTask.FromResult{TResult}"/>（与 <c>AndroidAudioDecoder</c> / <c>FFmpegAudioDecoder</c> 同构）。
/// <see cref="InitializeAsync"/> 无真实 I/O，返回 <see cref="Task.CompletedTask"/>。</para>
/// <para><b>输出格式</b>：AAC 由 AudioSpecificConfig 解析采样率/声道；MP3 于首包懒解析 MPEG 帧头回填
/// <see cref="OutputSampleRate"/> / <see cref="OutputChannels"/>；其余编码（Opus/FLAC/AC3）当前诚实拒绝，待扩展。</para>
/// <para><b>仅 Apple 可用</b>：非 Apple 运行时 <see cref="Initialize"/> 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2050",
    Justification = "无 [ComImport]，使用原始 [LibraryImport] P/Invoke，不会被裁剪器移除。仅 Apple 运行时使用。")]
internal sealed unsafe class AppleAudioDecoder : IAudioDecoder
{
    private readonly AppleBackend _backend;
    private readonly ILogger<AppleAudioDecoder> _logger;

    private nint _converter;       // AudioConverterRef（+1）
    private readonly Queue<MediaPacket> _pendingInput = new();
    private readonly Queue<AudioFrame> _pendingFrames = new();

    private AudioCodec _codecType = AudioCodec.Unknown;
    private AudioSettings _settings = null!;

    private int _outputSampleRate;
    private int _outputChannels;
    private int _bytesPerOutputPacket;   // S16：2 * 声道
    private int _framesPerPacket;        // AAC=1024；写入包描述的提示

    // 输出缓冲（固定容量，单包解出的 PCM 远小于此；HE-AAC 多声道亦覆盖）
    private byte[] _outBuffer = Array.Empty<byte>();
    private int _outCapacity;
    private AppleRuntime.AudioBufferList _outList;

    // 输入回调（AudioConverterComplexInputDataProc）相关状态
    private GCHandle _gcHandle;          // refCon → this
    private nint _inputProcPtr;          // 静态回调的函数指针
    private bool _inputHasData;          // 当前包是否仍有未供应的压缩数据
    private nint _inputPtr;              // 当前包数据指针（fixed 块内有效）
    private int _inputLength;
    private AppleRuntime.AudioStreamPacketDescription _pd; // 单包描述（fixed 块内被引用）
    private nint _pdPtr;

    private bool _initialized;
    private bool _disposed;

    public AppleAudioDecoder(AppleBackend backend, ILogger<AppleAudioDecoder> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public AudioCodec Codec => _codecType;

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false; // AudioConverter 为 CPU 软件解码/格式转换

    /// <inheritdoc/>
    public int OutputSampleRate => _outputSampleRate;

    /// <inheritdoc/>
    public int OutputChannels => _outputChannels;

    /// <inheritdoc/>
    public void Initialize(AudioCodec codec, AudioSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("AppleAudioDecoder 已初始化，不可重复 Initialize。");

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS())
            throw new PlatformNotSupportedException(
                "Apple 音频解码器仅支持 Apple 运行时（macOS / iOS）。请使用 FFmpeg 作为跨平台后端。");

        _codecType = codec;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        uint inFmt = codec switch
        {
            AudioCodec.AAC => AppleRuntime.kAudioFormatAAC,
            AudioCodec.MP3 => AppleRuntime.kAudioFormatMPEGLayer3,
            _ => 0,
        };
        if (inFmt == 0)
            throw new NotSupportedException(
                $"[APPLE-AUD] 当前仅支持 AAC / MP3，收到 {codec}（Opus/FLAC/AC3 待扩展）。");

        // 解析输出采样率/声道：AAC 由 AudioSpecificConfig 直接得出；MP3 占位 0（转换器按输入推导，首包回填）
        DetermineOutputFormat(codec, settings);

        var inAsbd = new AppleRuntime.AudioStreamBasicDescription
        {
            mFormatID = inFmt,
            mSampleRate = _outputSampleRate,
            mChannelsPerFrame = (uint)_outputChannels,
            mFramesPerPacket = (uint)(codec == AudioCodec.AAC ? 1024 : 0),
        };
        var outAsbd = new AppleRuntime.AudioStreamBasicDescription
        {
            mFormatID = AppleRuntime.kAudioFormatLinearPCM,
            mFormatFlags = AppleRuntime.kAudioFormatFlagIsSignedInteger
                         | AppleRuntime.kAudioFormatFlagIsPacked
                         | AppleRuntime.kAudioFormatFlagNativeEndian,
            mSampleRate = _outputSampleRate,
            mChannelsPerFrame = (uint)_outputChannels,
            mBitsPerChannel = 16,
            mFramesPerPacket = 1,
            mBytesPerFrame = (uint)(2 * _outputChannels),
            mBytesPerPacket = (uint)(2 * _outputChannels),
        };

        int st = AppleRuntime.AudioConverterNew(in inAsbd, in outAsbd, out nint conv);
        if (st != AppleRuntime.noErr || conv == nint.Zero)
            throw new NotSupportedException($"[APPLE-AUD] AudioConverter 创建失败 (status={st})。");
        _converter = conv;

        // 设置 magic cookie（AAC 的 AudioSpecificConfig；MP3 通常为空跳过）
        var cookie = settings.CodecConfiguration;
        if (cookie.Length > 0)
        {
            var cspan = cookie.Span;
            fixed (byte* pc = cspan)
            {
                int setSt = AppleRuntime.AudioConverterSetProperty(
                    _converter, AppleRuntime.kAudioConverterDecompressionMagicCookie,
                    (uint)cspan.Length, (nint)pc);
                if (setSt != AppleRuntime.noErr)
                    _logger.LogWarning("[APPLE-AUD] 设置 magic cookie 失败 (status={Status})，解码可能异常", setSt);
            }
        }

        _gcHandle = GCHandle.Alloc(this);
        _inputProcPtr = (nint)(delegate* unmanaged[Cdecl]<nint, uint*, nint, nint*, nint, int>)&InputProc;
        _outBuffer = new byte[65536];
        _outCapacity = _outBuffer.Length;
        _bytesPerOutputPacket = 2 * Math.Max(1, _outputChannels);
        _framesPerPacket = codec == AudioCodec.AAC ? 1024 : 1152;

        _initialized = true;
        _logger.LogInformation("[APPLE-AUD] 初始化完成: {Codec}, {Rate}Hz/{Ch}ch",
            codec, _outputSampleRate, _outputChannels);
    }

    private void DetermineOutputFormat(AudioCodec codec, AudioSettings settings)
    {
        if (codec == AudioCodec.AAC
            && TryParseAacAudioSpecificConfig(settings.CodecConfiguration, out int sr, out int ch))
        {
            _outputSampleRate = sr;
            _outputChannels = ch;
            return;
        }
        // MP3/其他：占位（转换器按输入推导，Output* 在首包后回填）；尊重调用方显式指定的输出率/声道
        _outputSampleRate = settings.OutputSampleRate ?? 0;
        _outputChannels = settings.OutputChannels ?? 0;
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask; // 实际初始化已在 Initialize(AudioCodec, AudioSettings) 完成
    }

    /// <inheritdoc/>
    public ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        if (packet is null) return new ValueTask<AudioFrame?>(ReadOutput());

        _pendingInput.Enqueue(packet);
        FeedAndDecode();
        return new ValueTask<AudioFrame?>(ReadOutput());
    }

    /// <inheritdoc/>
    public ValueTask<AudioFrame?> FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        FeedAndDecode();
        DrainConverter(); // 排空转换器内已缓冲的 PCM（不再供应新输入）
        return new ValueTask<AudioFrame?>(ReadOutput());
    }

    /// <inheritdoc/>
    public void Reset()
    {
        if (_converter == nint.Zero) return;
        while (_pendingInput.Count > 0) _pendingInput.Dequeue().Dispose();
        while (_pendingFrames.Count > 0) _pendingFrames.Dequeue().Dispose();
        _pendingInput.Clear();
        _pendingFrames.Clear();
        _inputHasData = false;
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

    private unsafe void DecodeOne(MediaPacket pkt)
    {
        byte[] data = pkt.Data.ToArray();
        int total = data.Length;
        if (total == 0) return;

        // MP3 等未知采样率：首包懒解析 MPEG 帧头回填 Output*（转换器输出率按输入推导，无需重建）
        if (_codecType == AudioCodec.MP3 && _outputSampleRate == 0
            && TryParseMp3FrameHeader(data, out int sr, out int ch))
        {
            _outputSampleRate = sr;
            _outputChannels = ch;
            _bytesPerOutputPacket = 2 * _outputChannels;
        }

        _inputLength = total;
        _inputHasData = true;
        _pd.mStartOffset = 0;
        _pd.mDataByteSize = (uint)total;
        _pd.mVariableFramesInPacket = (uint)_framesPerPacket;

        nint refCon = GCHandle.ToIntPtr(_gcHandle);
        fixed (byte* pIn = data)
        fixed (AppleRuntime.AudioStreamPacketDescription* pPd = &_pd)
        fixed (byte* pOut = _outBuffer)
        fixed (AppleRuntime.AudioBufferList* pList = &_outList)
        {
            _inputPtr = (nint)pIn;
            _pdPtr = (nint)pPd;
            while (true)
            {
                pList->mNumberBuffers = 1;
                pList->mBuffers.mNumberChannels = (uint)_outputChannels;
                pList->mBuffers.mData = (nint)pOut;
                pList->mBuffers.mDataByteSize = (uint)_outCapacity;

                int req = _outCapacity / _bytesPerOutputPacket;
                int outPackets = req;
                _ = AppleRuntime.AudioConverterFillComplexBuffer(
                    _converter, _inputProcPtr, refCon, ref outPackets, (nint)pList, nint.Zero);
                if (outPackets <= 0) break;

                int produced = outPackets * _bytesPerOutputPacket;
                var pcm = new byte[produced];
                new ReadOnlySpan<byte>(pOut, produced).CopyTo(pcm);
                int frameCount = produced / (2 * _outputChannels);
                _pendingFrames.Enqueue(new AudioFrame(
                    pcm, _outputSampleRate, _outputChannels, SampleFormat.S16,
                    pkt.Timestamp, TimeSpan.Zero, frameCount));

                if (!_inputHasData) break; // 当前包已耗尽且输出排空
            }
        }
        _inputHasData = false;
    }

    private unsafe void DrainConverter()
    {
        if (_converter == nint.Zero) return;
        _inputHasData = false;
        nint refCon = GCHandle.ToIntPtr(_gcHandle);
        fixed (byte* pOut = _outBuffer)
        fixed (AppleRuntime.AudioBufferList* pList = &_outList)
        {
            while (true)
            {
                pList->mNumberBuffers = 1;
                pList->mBuffers.mNumberChannels = (uint)_outputChannels;
                pList->mBuffers.mData = (nint)pOut;
                pList->mBuffers.mDataByteSize = (uint)_outCapacity;

                int req = _outCapacity / _bytesPerOutputPacket;
                int outPackets = req;
                _ = AppleRuntime.AudioConverterFillComplexBuffer(
                    _converter, _inputProcPtr, refCon, ref outPackets, (nint)pList, nint.Zero);
                if (outPackets <= 0) break;

                int produced = outPackets * _bytesPerOutputPacket;
                var pcm = new byte[produced];
                new ReadOnlySpan<byte>(pOut, produced).CopyTo(pcm);
                int frameCount = produced / (2 * _outputChannels);
                _pendingFrames.Enqueue(new AudioFrame(
                    pcm, _outputSampleRate, _outputChannels, SampleFormat.S16,
                    TimeSpan.Zero, TimeSpan.Zero, frameCount));
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int InputProc(nint converter, uint* ioNumberDataPackets, nint ioData,
        nint* outDataPacketDescription, nint userData)
    {
        var dec = GCHandle.FromIntPtr(userData).Target as AppleAudioDecoder;
        if (dec is null) return AppleRuntime.noErr;

        // 无更多压缩数据：告知转换器停止拉取（其将输出已缓冲 PCM 或返回）
        if (!dec._inputHasData)
        {
            *ioNumberDataPackets = 0;
            return AppleRuntime.noErr;
        }

        var abl = (AppleRuntime.AudioBufferList*)ioData;
        abl->mNumberBuffers = 1;
        abl->mBuffers.mNumberChannels = (uint)dec._outputChannels;
        abl->mBuffers.mData = dec._inputPtr;
        abl->mBuffers.mDataByteSize = (uint)dec._inputLength;

        *ioNumberDataPackets = 1;
        *outDataPacketDescription = dec._pdPtr;
        dec._inputHasData = false; // 当前包已交付，下次拉取返回 0
        return AppleRuntime.noErr;
    }

    private AudioFrame? ReadOutput()
        => _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;

    private void EnsureInitialized()
    {
        if (!_initialized || _converter == nint.Zero)
            throw new InvalidOperationException("AppleAudioDecoder 尚未 Initialize。");
    }

    // ── AAC AudioSpecificConfig（ISO/IEC 14496-3 §1.6.2.1）解析 ──
    private static readonly int[] AacSampleRates =
        { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

    private static bool TryParseAacAudioSpecificConfig(ReadOnlyMemory<byte> asc, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 0;
        var s = asc.Span;
        if (s.Length < 2) return false;

        int b0 = s[0];
        int b1 = s[1];
        int srIndex = ((b0 & 0x07) << 1) | (b1 >> 7);
        int chConfig = (b1 >> 3) & 0x0F;
        if (srIndex >= AacSampleRates.Length) return false;

        sampleRate = AacSampleRates[srIndex];
        channels = chConfig switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            5 => 5,
            6 => 6,
            7 => 8,
            11 => 7,
            _ => 0,
        };
        return sampleRate > 0 && channels > 0;
    }

    // ── MP3 帧头（MPEG1 Layer III 为主）解析，用于懒回填 Output* ──
    private static bool TryParseMp3FrameHeader(ReadOnlyMemory<byte> data, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 0;
        var s = data.Span;
        if (s.Length < 4) return false;
        if (s[0] != 0xFF || (s[1] & 0xE0) != 0xE0) return false;

        int version = (s[1] >> 3) & 0x03;   // 11=MPEG1, 10=MPEG2, 00=MPEG2.5
        int srIndex = (s[2] >> 2) & 0x03;
        int chMode = (s[3] >> 6) & 0x03;      // 11=单声道

        int ver = version switch
        {
            3 => 1,   // MPEG1
            2 => 2,   // MPEG2
            _ => 25,  // MPEG2.5
        };
        int[] sr1 = { 44100, 48000, 32000, 0 };
        int[] sr2 = { 22050, 24000, 16000, 0 };
        sampleRate = ver == 1 ? sr1[srIndex] : sr2[srIndex];
        channels = chMode == 3 ? 1 : 2;
        return sampleRate > 0;
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

        if (_converter != nint.Zero)
        {
            AppleRuntime.AudioConverterDispose(_converter);
            _converter = nint.Zero;
        }
        if (_gcHandle.IsAllocated) _gcHandle.Free();
    }
}

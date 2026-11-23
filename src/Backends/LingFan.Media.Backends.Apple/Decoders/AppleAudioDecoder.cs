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
/// <para><b>分部划分</b>：Convert 分部管转换通路（FillComplexBuffer/输入回调/排空）；
/// Bitstream 分部管 AAC/MP3 码流头解析。本文件为初始化、契约入口与生命周期。</para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2050",
    Justification = "无 [ComImport]，使用原始 [LibraryImport] P/Invoke，不会被裁剪器移除。仅 Apple 运行时使用。")]
internal sealed unsafe partial class AppleAudioDecoder : IAudioDecoder
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

    private AudioFrame? ReadOutput()
        => _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;

    private void EnsureInitialized()
    {
        if (!_initialized || _converter == nint.Zero)
            throw new InvalidOperationException("AppleAudioDecoder 尚未 Initialize。");
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

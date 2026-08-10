using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.VLCNative;

/// <summary>
/// 基于自写 Apache-2.0 P/Invoke 的 <see cref="IMediaDemuxer"/> 实现（零 LibVLCSharp）。
/// </summary>
/// <remarks>
/// <para><b>VLC 架构适配</b>：VLC 内部一体化处理解封装+解码，不暴露原始压缩包。
/// 本实现将 VLC 的回调式帧交付适配为拉取式 IMediaDemuxer 接口：
/// VLC 内部线程经回调把解码帧写入 Channel，<see cref="ReadPacketAsync"/> 从 Channel 读取。</para>
/// <para>因此产出的 <see cref="MediaPacket"/> 携带<b>已解码帧数据</b>（VLC 直通）；
/// VLCVideoDecoder/VLCNativeAudioDecoder 为直通解码器（pass-through）。</para>
/// <para><b>ABI 修正（根治 LibVLCSharp 三处不符，见 .memory 规划文档 §2.3 A/B/C）</b>：</para>
/// <list type="bullet">
/// <item>A 音频 setup 的 <c>format</c> 原生是 <c>char*</c>（按值）→ 声明 <c>IntPtr</c>，<b>绝不触碰</b>；
/// 强制 <c>:amem-format=s16l</c> 使 S16N 假设恒成立。</item>
/// <item>B 视频 <c>pitches</c>/<c>lines</c> 原生是数组（每平面一项）→ 声明 <c>IntPtr</c>，自行 <c>Marshal</c> 读写。</item>
/// <item>C 视频 cleanup 的 <c>opaque</c> 原生是 <c>void*</c>（按值）→ 声明 <c>IntPtr</c>。</item>
/// </list>
/// <para><b>帧戳根因（H7 收口）</b>：音频 play 回调 pts 是 libvlc 绝对时钟域(µs)，弃用之，改按
/// 「累计样本数 / 采样率」合成流内相对 PTS（与视频「帧计数 × 单帧时长」共用 _ptsBaseTicks 基准）。</para>
/// <para>🔴 视频帧 PTS 用 CFR 合成（lock/unlock/display 不传帧 PTS，mediaPlayer.Time 在 unlock 取值失真）；
/// VLC 内存回调不向回调传递帧 PTS，这是本后端固定采用的帧戳策略。</para>
/// <para><b>AOT 兼容</b>：sealed 类；12 个回调委托存字段防 GC；句柄用 nint；无反射。</para>
/// </remarks>
internal sealed class VLCNativeDemuxer : IMediaDemuxer
{
    private readonly VLCNativeBackend _backend;
    private readonly ILogger<VLCNativeDemuxer> _logger;

    // VLC 资源（原生句柄）
    private nint _media;
    private nint _mediaPlayer;
    private VLCNativeMediaStreamInput? _mediaInput;
    private IMediaStream? _sourceStream; // 仅地址式打开时持有，Close 时关闭

    // 帧交付 Channel
    private readonly Channel<MediaPacket> _frameChannel;

    // 视频回调委托（存字段防 GC 回收）
    private readonly LibVlcTypes.VideoFormatCb _videoFormatCb;
    private readonly LibVlcTypes.VideoCleanupCb _videoCleanupCb;
    private readonly LibVlcTypes.VideoLockCb _videoLockCb;
    private readonly LibVlcTypes.VideoUnlockCb _videoUnlockCb;
    private readonly LibVlcTypes.VideoDisplayCb _videoDisplayCb;

    // 音频回调委托
    private readonly LibVlcTypes.AudioSetupCb _audioSetupCb;
    private readonly LibVlcTypes.AudioCleanupCb _audioCleanupCb;
    private readonly LibVlcTypes.AudioPlayCb _audioPlayCb;
    private readonly LibVlcTypes.AudioPauseCb _audioPauseCb;
    private readonly LibVlcTypes.AudioResumeCb _audioResumeCb;
    private readonly LibVlcTypes.AudioFlushCb _audioFlushCb;
    private readonly LibVlcTypes.AudioDrainCb _audioDrainCb;

    // 视频缓冲区管理
    private IntPtr _videoBuffer = IntPtr.Zero;
    private int _videoWidth;
    private int _videoHeight;
    private int _videoPitch;
    private int _videoTrackIndex = -1;
    private long _videoFrameCounter;
    private long _videoFrameDurationTicks;

    // 音频格式
    private int _audioSampleRate;
    private int _audioChannels;
    private int _audioBytesPerSample = 2;        // S16N（每样本 2 字节）
    private SampleFormat _audioSampleFormat = SampleFormat.S16;
    private int _audioTrackIndex = -1;
    private long _audioSampleCounter;

    // 格式协商回调触发标志：作为「视频/音频轨确实存在」的权威信号，
    // 弥补播放前 tracks_get 漏列视频轨的缺陷（详见 BuildTracksAfterPlayback）。
    private bool _videoFormatReceived;
    private bool _audioSetupReceived;

    // 时间轴基准（音视频合成 PTS 共同起点）
    private long _ptsBaseTicks;

    // 状态
    private bool _opened;
    private bool _disposed;
    private IReadOnlyList<MediaTrack> _tracks = Array.Empty<MediaTrack>();
    private MediaMetadata _metadata = new();

    /// <summary>
    /// 初始化 <see cref="VLCNativeDemuxer"/> 的新实例。
    /// </summary>
    public VLCNativeDemuxer(VLCNativeBackend backend, ILogger<VLCNativeDemuxer> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _frameChannel = Channel.CreateBounded<MediaPacket>(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        _videoFormatCb = OnVideoFormat;
        _videoCleanupCb = OnVideoCleanup;
        _videoLockCb = OnVideoLock;
        _videoUnlockCb = OnVideoUnlock;
        _videoDisplayCb = OnVideoDisplay;
        _audioSetupCb = OnAudioSetup;
        _audioCleanupCb = OnAudioCleanup;
        _audioPlayCb = OnAudioPlay;
        _audioPauseCb = OnAudioPause;
        _audioResumeCb = OnAudioResume;
        _audioFlushCb = OnAudioFlush;
        _audioDrainCb = OnAudioDrain;
    }

    /// <inheritdoc/>
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <inheritdoc/>
    public MediaMetadata Metadata => _metadata;

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task OpenAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ct.ThrowIfCancellationRequested();

        await stream.ConnectAsync(ct).ConfigureAwait(false);

        nint instance = _backend.Instance.Handle;

        // ── 地址式打开优先（修复 imem 错误，与原 LibVLCSharp 后端同策略）──
        string? location = stream.Location;
        if (!string.IsNullOrEmpty(location))
        {
            _sourceStream = stream;
            // 🔴 关键：libvlc_media_new_location 要求合法 MRL（file:///...、http://...），
            // 裸 Windows 路径（E:\x.mp4）会被 VLC 拒开（"unable to open the MRL"）。
            // 本地文件系统路径必须走 libvlc_media_new_path，由 VLC 内部转 MRL；
            // 仅当 location 是格式良好的 URL（http/rtsp/mms/file:// 等）才用 new_location。
            bool isWellFormedUrl = Uri.TryCreate(location, UriKind.Absolute, out var uri)
                                   && uri.IsWellFormedOriginalString();
            _media = isWellFormedUrl
                ? LibVlcNative.libvlc_media_new_location(instance, location)
                : LibVlcNative.libvlc_media_new_path(instance, location);
        }
        else
        {
            // 无地址（内存/透传流）：回退 imem 路径。VLC 3.x 下 imem 仍受 get/release 指针校验限制，罕见路径。
            _mediaInput = new VLCNativeMediaStreamInput(stream);
            _media = _mediaInput.CreateMedia(instance);
        }

        if (_media == IntPtr.Zero)
            throw new InvalidOperationException($"libvlc_media_new_* 失败: {LibVlcInstance.LastErrorMessage() ?? "unknown"}");

        // 🔴 强制 amem 音频输出为 S16N（小端 16 位交织 PCM）：使得 OnAudioSetup 写死的 2 字节/样本、
        // SampleFormat.S16 假设恒成立，消除 VLC 某些源/版本以 FL32 交付导致的音质失真与主时钟 2× 漂移。
        LibVlcNative.libvlc_media_add_option(_media, ":amem-format=s16l");

        // 解析媒体（轮询 parsed_status，替代 LibVLCSharp 的 await Parse）
        int parseRc = LibVlcNative.libvlc_media_parse_with_options(
            _media, LibVlcTypes.ParseLocal | LibVlcTypes.FetchLocal, -1);
        if (parseRc != 0)
            _logger.LogWarning("libvlc_media_parse_with_options 返回 {Rc}", parseRc);

        int status = LibVlcTypes.ParsedStatusNone;
        for (int i = 0; i < 250; i++) // 上限 ~5s
        {
            status = LibVlcNative.libvlc_media_get_parsed_status(_media);
            if (status is LibVlcTypes.ParsedStatusDone or LibVlcTypes.ParsedStatusFailed or LibVlcTypes.ParsedStatusTimeout)
                break;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        if (status != LibVlcTypes.ParsedStatusDone)
            _logger.LogWarning("VLC 媒体解析未完成（status={Status}），轨道/时长可能不完整", status);

        _metadata = ParseMetadata(_media);

        // 🔴 固定轨道索引标签：视频=0，音频=1。
        // 不依赖「播放前 tracks_get」的枚举结果——VLC 在播放前解析常漏列视频轨（尤其硬解/--vout=dummy 路径），
        // 导致 _videoTrackIndex 恒为 -1、视频回调不被注册、视频帧全丢（本项目「音频全到+视频全丢」表象之一）。
        // 内存回调直接交付解码帧，索引仅作包标签，与下方播放后重取的 Tracks 元数据保持一致即可。
        _videoTrackIndex = 0;
        _audioTrackIndex = 1;

        // 默认 30fps 合成步长；播放后按真实视频轨帧率修正（避免首段帧 PTS 全 0）。
        _videoFrameDurationTicks = (long)(TimeSpan.TicksPerSecond / 30.0);

        // 伪异步：libvlc_media_player_play 为同步原生调用，Task.Run 仅卸载到线程池避免阻塞调用线程。
        // 视频/音频回调<b>无条件注册</b>——只要存在对应轨，VLC 即经回调交付解码帧。
        await Task.Run(() => StartPlayback(instance), ct).ConfigureAwait(false);

        // 等待 media player 进入 playing 并触发格式协商回调（OnVideoFormat/OnAudioSetup）。
        // 此后 tracks_get 才完整列出视频轨（含正确 codec/帧率），用于补全 Tracks 元数据。
        await Task.Delay(600, ct).ConfigureAwait(false);

        _tracks = BuildTracksAfterPlayback(_media);

        // 计算单帧时长（ticks），供 OnVideoUnlock 合成单调递增的真实视频 PTS（CFR 近似）。
        // 优先采用播放后重取到的视频轨帧率。
        double fps = 30.0;
        foreach (var t in _tracks)
        {
            if (t.Type == TrackType.Video && t.VideoInfo != null && t.VideoInfo.FrameRate > 0)
            {
                fps = t.VideoInfo.FrameRate;
                break;
            }
        }
        _videoFrameDurationTicks = (long)(TimeSpan.TicksPerSecond / fps);

        _opened = true;
        _logger.LogInformation("VLC Native 打开成功: {TrackCount} 条轨道, 时长 {Duration}",
            _tracks.Count, _metadata.Duration);
    }

    private void StartPlayback(nint instance)
    {
        _mediaPlayer = LibVlcNative.libvlc_media_player_new(instance);
        LibVlcNative.libvlc_media_player_set_media(_mediaPlayer, _media);

        // 🔴 无条件注册视频/音频内存回调：索引已固定（视频=0/音频=1），
        // 回调仅在存在对应轨时由 VLC 触发；缺失对应轨时回调不触发，零副作用。
        LibVlcNative.libvlc_video_set_format_callbacks(
            _mediaPlayer,
            Marshal.GetFunctionPointerForDelegate(_videoFormatCb),
            Marshal.GetFunctionPointerForDelegate(_videoCleanupCb));
        LibVlcNative.libvlc_video_set_callbacks(
            _mediaPlayer,
            Marshal.GetFunctionPointerForDelegate(_videoLockCb),
            Marshal.GetFunctionPointerForDelegate(_videoUnlockCb),
            Marshal.GetFunctionPointerForDelegate(_videoDisplayCb),
            IntPtr.Zero);

        LibVlcNative.libvlc_audio_set_format_callbacks(
            _mediaPlayer,
            Marshal.GetFunctionPointerForDelegate(_audioSetupCb),
            Marshal.GetFunctionPointerForDelegate(_audioCleanupCb));
        LibVlcNative.libvlc_audio_set_callbacks(
            _mediaPlayer,
            Marshal.GetFunctionPointerForDelegate(_audioPlayCb),
            Marshal.GetFunctionPointerForDelegate(_audioPauseCb),
            Marshal.GetFunctionPointerForDelegate(_audioResumeCb),
            Marshal.GetFunctionPointerForDelegate(_audioFlushCb),
            Marshal.GetFunctionPointerForDelegate(_audioDrainCb),
            IntPtr.Zero);

        LibVlcNative.libvlc_media_player_play(_mediaPlayer);
    }

    /// <inheritdoc/>
    public async ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        if (await _frameChannel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (_frameChannel.Reader.TryRead(out var packet))
                return packet;
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _mediaPlayer == IntPtr.Zero)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        // 伪异步：libvlc_media_player_set_time 为同步原生调用，Task.Run 仅卸载到线程池。
        return await Task.Run(() =>
        {
            // 时间轴重定位：先置基准再 set_time，使 seek 后新帧接在正确时间轴上（flush 触发新帧回调）。
            _ptsBaseTicks = position.Ticks;
            _videoFrameCounter = 0;
            _audioSampleCounter = 0;
            LibVlcNative.libvlc_media_player_set_time(_mediaPlayer, (long)position.TotalMilliseconds);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (!_opened) return;
        _opened = false;

        if (_mediaPlayer != IntPtr.Zero)
        {
            try { LibVlcNative.libvlc_media_player_stop(_mediaPlayer); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "VLC MediaPlayer 停止异常");
            }
        }

        _frameChannel.Writer.TryComplete();

        _sourceStream?.Close();

        if (_videoBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_videoBuffer);
            _videoBuffer = IntPtr.Zero;
        }

        if (_mediaPlayer != IntPtr.Zero)
        {
            LibVlcNative.libvlc_media_player_release(_mediaPlayer);
            _mediaPlayer = IntPtr.Zero;
        }

        if (_media != IntPtr.Zero)
        {
            LibVlcNative.libvlc_media_release(_media);
            _media = IntPtr.Zero;
        }

        _mediaInput?.Dispose();
        _mediaInput = null;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    // ── VLC 视频回调（按值指针参数，自行 Marshal）──

    private uint OnVideoFormat(IntPtr opaque, IntPtr chroma, IntPtr width, IntPtr height, IntPtr pitches, IntPtr lines)
    {
        _videoFormatReceived = true; // 视频轨存在的权威信号（弥补播放前 tracks_get 漏列）
        // VLC 期望通过 chroma 指针写入 FourCC
        Marshal.WriteInt32(chroma, (int)VlcCodecMapping.FourCC("BGRA"));
        uint w = (uint)Marshal.ReadInt32(width);
        uint h = (uint)Marshal.ReadInt32(height);
        uint pitch = w * 4;
        Marshal.WriteInt32(pitches, (int)pitch);
        Marshal.WriteInt32(lines, (int)h);

        _videoWidth = (int)w;
        _videoHeight = (int)h;
        _videoPitch = (int)pitch;
        _videoFrameCounter = 0; // 新格式/新流起点：重置 PTS 合成计数

        if (_videoBuffer != IntPtr.Zero)
            Marshal.FreeHGlobal(_videoBuffer);
        _videoBuffer = Marshal.AllocHGlobal((int)(pitch * h));

        return 1;
    }

    private void OnVideoCleanup(IntPtr opaque)
    {
        if (_videoBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_videoBuffer);
            _videoBuffer = IntPtr.Zero;
        }
    }

    private IntPtr OnVideoLock(IntPtr opaque, IntPtr planes)
    {
        if (_videoBuffer != IntPtr.Zero)
            Marshal.WriteIntPtr(planes, _videoBuffer);
        return IntPtr.Zero;
    }

    private void OnVideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        if (_videoBuffer == IntPtr.Zero || _videoTrackIndex < 0) return;

        int dataSize = _videoPitch * _videoHeight;
        byte[] data = new byte[dataSize];
        Marshal.Copy(_videoBuffer, data, 0, dataSize);

        // 视频帧 PTS 用「帧计数 × 单帧时长」合成流内相对 PTS（CFR 近似），自 _ptsBaseTicks 起。
        // 🔴 关键：VLC 内存回调不向回调传递帧 PTS；mediaPlayer.Time 在 unlock 取值不可靠（抖动致帧戳失真、卡顿）。
        var pts = TimeSpan.FromTicks(_ptsBaseTicks + _videoFrameCounter * _videoFrameDurationTicks);
        _videoFrameCounter++;

        var packet = new MediaPacket(
            _videoTrackIndex, data,
            pts,
            TimeSpan.Zero, keyFrame: true,
            width: _videoWidth, height: _videoHeight, stride: _videoPitch);

        _frameChannel.Writer.TryWrite(packet);
    }

    // display 回调：帧已在 OnVideoUnlock 交付通道，此处无需动作（勿在 VLC 回调线程上 sleep）。
    private void OnVideoDisplay(IntPtr opaque, IntPtr picture) { }

    // ── VLC 音频回调 ──

    private int OnAudioSetup(IntPtr opaque, IntPtr format, IntPtr rate, IntPtr channels)
    {
        _audioSetupReceived = true; // 音频轨存在的权威信号
        // 🔴 关键 ABI 约束：format 原生是 char*（按值），绝不可作为 ref IntPtr 读写（栈破坏/野指针崩溃）。
        // 我们【绝不触碰 format】，按 VLC 默认 S16N 消费（已用 :amem-format=s16l 强制）。
        _audioSampleRate = (int)Marshal.ReadInt32(rate);
        _audioChannels = (int)Marshal.ReadInt32(channels);
        _audioBytesPerSample = 2;            // S16N：16 位有符号，每样本 2 字节
        _audioSampleFormat = SampleFormat.S16;
        _audioSampleCounter = 0;
        _logger.LogInformation("VLC amem 音频格式协商: rate={Rate}Hz channels={Channels}（已强制 S16N；消费侧固定 2 字节/样本）",
            _audioSampleRate, _audioChannels);
        return 0;
    }

    private void OnAudioCleanup(IntPtr opaque) { }

    private void OnAudioPause(IntPtr data, long pts) { }

    private void OnAudioResume(IntPtr data, long pts) { }

    // 选择性出队：仅丢弃音频包，保留视频包（修复 H7）。
    private void OnAudioFlush(IntPtr data, long pts)
    {
        var videoPackets = new List<MediaPacket>(capacity: 4);
        while (_frameChannel.Reader.TryRead(out var packet))
        {
            if (packet.TrackIndex == _audioTrackIndex)
                packet.Dispose();
            else
                videoPackets.Add(packet);
        }
        foreach (var f in videoPackets)
            _frameChannel.Writer.TryWrite(f);
    }

    private void OnAudioDrain(IntPtr data) { }

    // 🔴 根因（VLC 后端「视频 0 交付 / 全丢、音频却全到」的真凶）：
    // audio play 回调形参 pts 是 libvlc 绝对时钟域(µs)，非流内相对时间。弃用之，改按
    // 「累计样本数 / 采样率」合成流内相对 PTS，与视频 CFR 合成共用 _ptsBaseTicks 基准，音视频天然对齐。
    private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        if (_audioTrackIndex < 0 || _audioSampleRate <= 0) return;

        int dataSize = (int)count * _audioChannels * _audioBytesPerSample;
        byte[] audioData = new byte[dataSize];
        Marshal.Copy(samples, audioData, 0, dataSize);

        // 先取值后累加：首包 PTS = _ptsBaseTicks（常态 0），与视频首帧同起点。
        var timestamp = TimeSpan.FromTicks(
            _ptsBaseTicks + _audioSampleCounter * TimeSpan.TicksPerSecond / _audioSampleRate);
        _audioSampleCounter += count;

        var packet = new MediaPacket(
            _audioTrackIndex, audioData,
            timestamp,
            TimeSpan.Zero, keyFrame: true,
            sampleRate: _audioSampleRate, channels: _audioChannels, format: _audioSampleFormat);

        _frameChannel.Writer.TryWrite(packet);
    }

    // ── 辅助方法 ──

    /// <summary>
    /// 播放后重取轨道元数据。
    /// </summary>
    /// <remarks>
    /// 🔴 关键修正：VLC 在「播放前」经 <c>libvlc_media_parse_with_options</c> 枚举的轨道常漏列视频轨
    /// （尤其启用硬解 <c>--avcodec-hw=any</c> 与无头 <c>--vout=dummy</c> 时），导致 <c>_videoTrackIndex</c> 恒为 -1、
    /// 视频内存回调不被注册、视频帧全丢（本项目「音频全到+视频全丢」表象之一）。
    /// 真实可靠的视频存在性信号是<b>播放后</b> <c>OnVideoFormat</c> 回调是否触发；此处以
    /// 「tracks_get 含视频轨 OR 格式回调已触发」为存在性判据，补全 <see cref="_tracks"/>。
    /// </remarks>
    private IReadOnlyList<MediaTrack> BuildTracksAfterPlayback(nint media)
    {
        LibVlcTypes.LibvlcMediaTrackT? videoTrack = null;
        LibVlcTypes.LibvlcMediaTrackT? audioTrack = null;
        var textTracks = new List<LibVlcTypes.LibvlcMediaTrackT>();

        uint count = LibVlcNative.libvlc_media_tracks_get(media, out nint ppTracks);
        try
        {
            if (ppTracks != IntPtr.Zero && count > 0)
            {
                for (uint i = 0; i < count; i++)
                {
                    nint trackPtr = Marshal.ReadIntPtr(ppTracks, (int)(i * nint.Size));
                    if (trackPtr == IntPtr.Zero) continue;
                    var t = Marshal.PtrToStructure<LibVlcTypes.LibvlcMediaTrackT>(trackPtr);
                    switch (t.i_type)
                    {
                        case LibVlcTypes.TrackTypeVideo: videoTrack ??= t; break;
                        case LibVlcTypes.TrackTypeAudio: audioTrack ??= t; break;
                        case LibVlcTypes.TrackTypeText: textTracks.Add(t); break;
                    }
                }
            }
        }
        finally
        {
            if (ppTracks != IntPtr.Zero)
                LibVlcNative.libvlc_media_tracks_release(ppTracks, count);
        }

        var tracks = new List<MediaTrack>();
        int idx = 0;
        // 存在性判据：tracks_get 枚举 OR 格式回调触发，任一成立即视为该轨存在，避免视频帧全丢。
        if (videoTrack is not null || _videoFormatReceived)
            tracks.Add(BuildVideoTrack(idx++, videoTrack, media));
        if (audioTrack is not null || _audioSetupReceived)
            tracks.Add(BuildAudioTrack(idx++, audioTrack, media));
        foreach (var tx in textTracks)
            tracks.Add(BuildTextTrack(idx++, tx));

        return tracks;
    }

    // 视频轨存在时 t 非 null；若 tracks_get 仍漏列但 OnVideoFormat 已触发，传 null 并回退到格式回调协商的宽高。
    private MediaTrack BuildVideoTrack(int index, LibVlcTypes.LibvlcMediaTrackT? t, nint media)
    {
        uint w = 0, h = 0, num = 0, den = 1, codec = 0, bitrate = 0;
        if (t is { } tr)
        {
            // i_codec 为 0 时的兜底：用 i_original_fourcc 保底，避免元数据未完整解析时 codec 落 Unknown。
            codec = tr.i_codec != 0 ? tr.i_codec : tr.i_original_fourcc;
            bitrate = tr.i_bitrate;
            if (tr.union_ptr != IntPtr.Zero)
            {
                var v = Marshal.PtrToStructure<LibVlcTypes.LibvlcVideoTrackT>(tr.union_ptr);
                w = v.i_width; h = v.i_height; num = v.i_frame_rate_num; den = v.i_frame_rate_den;
            }
        }
        else
        {
            // 回退：用 OnVideoFormat 协商到的宽高。
            w = (uint)_videoWidth; h = (uint)_videoHeight;
        }

        float frameRate = num > 0 && den > 0 ? (float)num / den : 0;
        long durationMs = LibVlcNative.libvlc_media_get_duration(media);

        var videoCodec = VlcCodecMapping.MapVideoCodec(VlcCodecMapping.FourCCToString(codec));
        if (videoCodec == VideoCodec.Unknown)
            _logger.LogInformation(
                "视频轨 codec 映射失败：原始 i_codec=0x{Codec:X8} (FourCC='{Fourcc}')，轨道元数据可能未完整解析",
                codec, VlcCodecMapping.FourCCToString(codec));

        return new MediaTrack
        {
            Index = index,
            Type = TrackType.Video,
            VideoCodec = videoCodec,
            BitRate = (long)bitrate,
            VideoInfo = new VideoTrackInfo
            {
                Width = (int)w,
                Height = (int)h,
                FrameRate = frameRate,
                Duration = durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : TimeSpan.Zero
            }
        };
    }

    // 音频轨存在时 t 非 null；若 tracks_get 仍漏列但 OnAudioSetup 已触发，传 null 并回退到协商的采样率/声道数。
    private MediaTrack BuildAudioTrack(int index, LibVlcTypes.LibvlcMediaTrackT? t, nint media)
    {
        uint rate = 0, channels = 0, codec = 0, bitrate = 0;
        if (t is { } tr)
        {
            // 与视频轨同理：i_codec 为 0 时兜底到 i_original_fourcc。
            codec = tr.i_codec != 0 ? tr.i_codec : tr.i_original_fourcc;
            bitrate = tr.i_bitrate;
            if (tr.union_ptr != IntPtr.Zero)
            {
                var a = Marshal.PtrToStructure<LibVlcTypes.LibvlcAudioTrackT>(tr.union_ptr);
                rate = a.i_rate; channels = a.i_channels;
            }
        }
        else
        {
            rate = (uint)_audioSampleRate; channels = (uint)_audioChannels;
        }

        long durationMs = LibVlcNative.libvlc_media_get_duration(media);

        var audioCodec = VlcCodecMapping.MapAudioCodec(VlcCodecMapping.FourCCToString(codec));
        if (audioCodec == AudioCodec.Unknown)
            _logger.LogInformation(
                "音频轨 codec 映射失败：原始 i_codec=0x{Codec:X8} (FourCC='{Fourcc}')，轨道元数据可能未完整解析",
                codec, VlcCodecMapping.FourCCToString(codec));

        return new MediaTrack
        {
            Index = index,
            Type = TrackType.Audio,
            AudioCodec = audioCodec,
            BitRate = (long)bitrate,
            AudioInfo = new AudioTrackInfo
            {
                SampleRate = (int)rate,
                Channels = (int)channels,
                BitsPerSample = 0,
                Duration = durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : TimeSpan.Zero
            }
        };
    }

    private static MediaTrack BuildTextTrack(int index, LibVlcTypes.LibvlcMediaTrackT t)
    {
        return new MediaTrack
        {
            Index = index,
            Type = TrackType.Subtitle,
            SubtitleCodec = VlcCodecMapping.MapSubtitleCodec(VlcCodecMapping.FourCCToString(t.i_codec))
        };
    }

    private static MediaMetadata ParseMetadata(nint media)
    {
        long durationMs = LibVlcNative.libvlc_media_get_duration(media);

        return new MediaMetadata
        {
            Title = GetMeta(media, LibVlcTypes.MetaTitle),
            Artist = GetMeta(media, LibVlcTypes.MetaArtist),
            Album = GetMeta(media, LibVlcTypes.MetaAlbum),
            Genre = GetMeta(media, LibVlcTypes.MetaGenre),
            Year = int.TryParse(GetMeta(media, LibVlcTypes.MetaDate), out int y) ? y : null,
            Duration = durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : TimeSpan.Zero,
            ContainerFormat = ContainerFormat.Unknown
        };
    }

    private static string? GetMeta(nint media, uint metaType)
    {
        nint p = LibVlcNative.libvlc_media_get_meta(media, metaType);
        if (p == IntPtr.Zero) return null;
        string? s = Marshal.PtrToStringUTF8(p);
        LibVlcNative.libvlc_free(p);
        return s;
    }
}

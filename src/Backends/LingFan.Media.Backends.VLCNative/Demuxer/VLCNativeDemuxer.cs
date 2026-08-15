using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LingFan.Media.Backends.VLCNative.Demuxer;

/// <summary>
/// 基于自写 Apache-2.0 P/Invoke 的 <see cref="IMediaDemuxer"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>VLC 架构适配</b>：VLC 内部一体化处理解封装+解码，不暴露原始压缩包。
/// 本实现将 VLC 的回调式帧交付适配为拉取式 IMediaDemuxer 接口：
/// VLC 内部线程经回调把解码帧写入 Channel，<see cref="ReadPacketAsync"/> 从 Channel 读取。</para>
/// <para>因此产出的 <see cref="MediaPacket"/> 携带<b>已解码帧数据</b>（VLC 直通）；
/// VLCVideoDecoder/VLCAudioDecoder 为直通解码器（pass-through）。</para>
/// <para><b>回调 ABI 要点（与 libvlc 原生声明对齐）：</b></para>
/// <list type="bullet">
/// <item>A 音频 setup 的 <c>format</c> 原生是 <c>char*</c>（按值）→ 声明 <c>IntPtr</c>，<b>绝不触碰</b>；
/// 强制 <c>:amem-format=s16l</c> 使 S16N 假设恒成立。</item>
/// <item>B 视频 <c>pitches</c>/<c>lines</c> 原生是数组（每平面一项）→ 声明 <c>IntPtr</c>，自行 <c>Marshal</c> 读写。</item>
/// <item>C 视频 cleanup 的 <c>opaque</c> 原生是 <c>void*</c>（按值）→ 声明 <c>IntPtr</c>。</item>
/// </list>
/// <para><b>帧戳策略</b>：音频 play 回调形参 pts 是 libvlc 绝对时钟域(µs)，弃用之，改按
/// 「累计样本数 / 采样率」合成流内相对 PTS（与视频共用 _ptsBaseTicks 基准）。</para>
/// <para>视频帧 PTS：VLC 内存回调不向回调传递帧 PTS、mediaPlayer.Time 在 unlock 取值失真，
/// 故以播放起始墙钟为锚、按真实投递时刻合成并对齐帧最小间距（CFR 抖动/突发下跟随主时钟，消除落后丢帧）。</para>
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
    private readonly Channel<MediaPacket> _videoChannel;
    private readonly Channel<MediaPacket> _audioChannel;

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
    private long _videoFrameDurationTicks;
    // 视频 PTS 合成（修复软解抖动丢帧）：以播放起始墙钟为锚、按真实投递时刻合成 PTS，
    // 并对齐帧最小间距，使 PTS 在抖动/突发下跟随主时钟、消除「落后丢帧」，保留正确帧序与间距。
    private bool _videoPtsAnchorStarted;
    private long _videoPtsAnchorTimestamp;
    private long _lastVideoPtsTicks;

    // 音频格式
    private int _audioSampleRate;
    private int _audioChannels;
    private int _audioBytesPerSample = 2;        // S16N（每样本 2 字节）
    private SampleFormat _audioSampleFormat = SampleFormat.S16;
    private int _audioTrackIndex = -1;
    private long _audioSampleCounter;

    // 音频抖动缓冲（治本修复 VLC amem 突发投递→WASAPI 渲染欠载）：
    // 生产者 OnAudioPlay 入队；专用释放线程按「共享通道水位」维持稳定释放到共享通道，
    // 突发被缓冲吸收、停产被缓冲覆盖，WASAPI 得连续供给、消除间隙。后端内作用域，不影响 MF/FFmpeg。
    private readonly ConcurrentQueue<MediaPacket> _audioJitter = new();
    private const int AudioJitterMaxPackets = 2000; // ~40s@44.1k 上限，防失控增长
    private CancellationTokenSource? _audioReleaseCts;
    private Task? _audioReleaseTask;
    private readonly object _audioReleaseLock = new();

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

        // 视频(可丢)与音频(不可丢)分走独立有界通道：音频绝不被视频的 DropOldest 误删，
        // 这是消除 VLC 音频间隙的根本修复（此前共享单通道时视频突发会把队首音频挤掉）。
        _videoChannel = Channel.CreateBounded<MediaPacket>(
            new BoundedChannelOptions(96)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        _audioChannel = Channel.CreateBounded<MediaPacket>(
            new BoundedChannelOptions(256)
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

        // ── 地址式打开优先（location 打开比 imem 内存源更可靠）──
        string? location = stream.Location;
        if (!string.IsNullOrEmpty(location))
        {
            _sourceStream = stream;
            // 关键：libvlc_media_new_location 要求合法 MRL（file:///...、http://...），
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
            // 无地址（内存/透传流）：回退 imem 路径。VLC 下 imem 仍受 get/release 指针校验限制，罕见路径。
            _mediaInput = new VLCNativeMediaStreamInput(stream);
            _media = _mediaInput.CreateMedia(instance);
        }

        if (_media == IntPtr.Zero)
            throw new InvalidOperationException($"libvlc_media_new_* 失败: {LibVlcInstance.LastErrorMessage() ?? "unknown"}");

        // 强制 amem 音频输出为 S16N（小端 16 位交织 PCM）：使得 OnAudioSetup 写死的 2 字节/样本、
        // SampleFormat.S16 假设恒成立，消除 VLC 某些源以 FL32 交付导致的音质失真与主时钟漂移。
        LibVlcNative.libvlc_media_add_option(_media, ":amem-format=s16l");

        // 解析媒体（轮询 parsed_status，而非依赖单次解析完成事件）
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

        // 固定轨道索引标签：视频=0，音频=1。
        // 不依赖「播放前 tracks_get」的枚举结果——VLC 在播放前解析常漏列视频轨（尤其硬解/--vout=dummy 路径），
        // 导致 _videoTrackIndex 恒为 -1、视频回调不被注册、视频帧全丢。
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

        // 无条件注册视频/音频内存回调：索引已固定（视频=0/音频=1），
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

        // 合并两独立通道：优先音频（不可丢，避免被视频淹没），两者皆空则等待任一可读。
        while (!ct.IsCancellationRequested)
        {
            if (_audioChannel.Reader.TryPeek(out _))
                return _audioChannel.Reader.TryRead(out var ap) ? ap : null;
            if (_videoChannel.Reader.TryPeek(out _))
                return _videoChannel.Reader.TryRead(out var vp) ? vp : null;

            if (_audioChannel.Reader.Completion.IsCompleted && _videoChannel.Reader.Completion.IsCompleted)
                return null;

            var audioWait = _audioChannel.Reader.WaitToReadAsync(ct).AsTask();
            var videoWait = _videoChannel.Reader.WaitToReadAsync(ct).AsTask();
            await Task.WhenAny(audioWait, videoWait).ConfigureAwait(false);
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
            _videoPtsAnchorStarted = false;
            _audioSampleCounter = 0;
            RestartAudioRelease(); // 重置音频释放实时速率锚点，避免 seek 后 PTS 时序错位
            LibVlcNative.libvlc_media_player_set_time(_mediaPlayer, (long)position.TotalMilliseconds);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (!_opened) return;
        _opened = false;

        // 取消音频释放线程并等待排空（避免与 VLC 回调并发写通道）
        try { _audioReleaseCts?.Cancel(); } catch { }
        try { _audioReleaseTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        try { _audioReleaseCts?.Dispose(); } catch { }
        _audioReleaseCts = null;
        _audioReleaseTask = null;
        while (_audioJitter.TryDequeue(out var p)) p.Dispose();

        if (_mediaPlayer != IntPtr.Zero)
        {
            try { LibVlcNative.libvlc_media_player_stop(_mediaPlayer); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "VLC MediaPlayer 停止异常");
            }
        }

        _videoChannel.Writer.TryComplete();
        _audioChannel.Writer.TryComplete();

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
        _videoPtsAnchorStarted = false; // 新格式/新流起点：重置 PTS 墙钟锚点
        RestartAudioRelease(); // 同步重置音频释放实时速率锚点（同生命周期）

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

        // 视频帧 PTS：原纯 CFR 计数（frame_counter × 单帧时长）不跟真实时钟对齐，
        // VLC 软解经 libvlc 回调突发/抖动投递时，帧实际到达墙钟常落后合成 PTS 超过 DropThreshold
        // → 被 Synchronizer 判「严重落后」丢弃（MF/FFmpeg 平稳投递故 0 丢）。
        // 改为以播放起始墙钟为锚、按真实投递时刻合成 PTS，并对齐帧最小间距：
        //   突发（preroll 多帧同刻到达）→ 取「上一 PTS + 单帧时长」保持正确帧序与间距；
        //   平稳/抖动迟到 → 取「墙钟时刻」使 PTS 跟随主时钟、消除落后丢帧。
        if (!_videoPtsAnchorStarted)
        {
            _videoPtsAnchorStarted = true;
            _videoPtsAnchorTimestamp = Stopwatch.GetTimestamp();
            _lastVideoPtsTicks = _ptsBaseTicks;
        }
        long elapsedTicks = Stopwatch.GetElapsedTime(_videoPtsAnchorTimestamp).Ticks;
        long candidateTicks = _ptsBaseTicks + elapsedTicks;
        long ptsTicks = candidateTicks > _lastVideoPtsTicks + _videoFrameDurationTicks
            ? candidateTicks
            : _lastVideoPtsTicks + _videoFrameDurationTicks;
        _lastVideoPtsTicks = ptsTicks;
        var pts = TimeSpan.FromTicks(ptsTicks);

        var packet = new MediaPacket(
            _videoTrackIndex, data,
            pts,
            TimeSpan.Zero, keyFrame: true,
            width: _videoWidth, height: _videoHeight, stride: _videoPitch);

        _videoChannel.Writer.TryWrite(packet);
    }

    // display 回调：帧已在 OnVideoUnlock 交付通道，此处无需动作（勿在 VLC 回调线程上 sleep）。
    private void OnVideoDisplay(IntPtr opaque, IntPtr picture) { }

    // ── VLC 音频回调 ──

    private int OnAudioSetup(IntPtr opaque, IntPtr format, IntPtr rate, IntPtr channels)
    {
        _audioSetupReceived = true; // 音频轨存在的权威信号
        // 关键 ABI 约束：format 原生是 char*（按值），绝不可作为 ref IntPtr 读写（栈破坏/野指针崩溃）。
        // 按 VLC 默认 S16N 消费（已用 :amem-format=s16l 强制）。
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

    // 选择性出队：仅丢弃音频包，保留视频包（flush 时音频需清空，视频帧保留）。
    private void OnAudioFlush(IntPtr data, long pts)
    {
        // 诊断（排查剩余音频间隙 H2 假设）：正常播放中途若触发，说明 VLC 主动丢弃已解码音频，
        // 会清空抖动缓冲与通道内音频、造成真实断音。若重跑仍见间隙且此日志在 ~1.5s 出现，则根因是 VLC flush 而非节流。
        int cleared = 0;
        while (_audioJitter.TryDequeue(out var stale)) { stale.Dispose(); cleared++; }
        if (cleared > 0)
            _logger.LogWarning("[VLC-AUDIO] OnAudioFlush 触发：抖动缓冲清空 {Count} 包（排查中途断音 H2 假设）", cleared);
        else
            _logger.LogDebug("[VLC-AUDIO] OnAudioFlush 触发：抖动缓冲已空（seek/启动常规路径）");
        // 清空音频通道内滞留音频：flush 后旧音频不可再释放，避免 seek 后串音。
        // 音频已独立通道，不会被视频 DropOldest 误删；视频通道不干预（保持原语义：仅清音频，视频帧后续自然接续）。
        int audioCleared = 0;
        while (_audioChannel.Reader.TryRead(out var staleAudio)) { staleAudio.Dispose(); audioCleared++; }
        if (audioCleared > 0)
            _logger.LogDebug("[VLC-AUDIO] OnAudioFlush 已丢弃通道内滞留音频 {Count} 包", audioCleared);
    }

    private void OnAudioDrain(IntPtr data) { }

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

        // 入队抖动缓冲（生产者）：释放线程按实时速率稳定释放到共享通道，吸收 VLC amem 突发投递，
        // 消除 WASAPI 渲染侧欠载间隙。超上限丢弃最旧音频（瞬断劣于长卡，防失控增长）。
        while (_audioJitter.Count >= AudioJitterMaxPackets && _audioJitter.TryDequeue(out var old)) old.Dispose();
        _audioJitter.Enqueue(packet);
        EnsureAudioReleaseStarted();
    }

    // 启动音频释放线程（首次 OnAudioPlay 懒启动并锁保护；已运行则幂等返回）。
    private void EnsureAudioReleaseStarted()
    {
        if (_audioReleaseTask is { IsCompleted: false }) return;
        lock (_audioReleaseLock)
        {
            if (_audioReleaseTask is { IsCompleted: false }) return;
            RestartAudioRelease();
        }
    }

    // 取消旧释放线程、清空缓冲、启动新线程（格式变更/Seek 时调用，重置释放状态）。
    private void RestartAudioRelease()
    {
        try { _audioReleaseCts?.Cancel(); } catch { }
        try { _audioReleaseCts?.Dispose(); } catch { }
        _audioReleaseCts = null;
        _audioReleaseTask = null;
        while (_audioJitter.TryDequeue(out var p)) p.Dispose();

        _audioReleaseCts = new CancellationTokenSource();
        var ct = _audioReleaseCts.Token;
        _audioReleaseTask = Task.Run(() => AudioReleaseLoop(ct), ct);
    }

    // 音频释放循环（治本修复 VLC amem 突发/停产→WASAPI 欠载）：
    // 抖动缓冲在生产者侧吸收 VLC 突发/停产；释放侧把音频写入独立的 _audioChannel。
    // 音频与视频分走独立通道后，视频的 DropOldest 再也无法误删队首音频，故释放侧无条件写入即可，
    // 无需 HighWater 节流（早期共享单通道时那样做反而让位视频、饿死音频，制造 462ms 间隙）。
    // 音频通道仅承载音频、几乎不溢出；通道暂满时 DropOldest 仅丢最旧音频（偶发微疵），不阻塞释放线程。
    // 后端内作用域，不影响 MF/FFmpeg。
    private async Task AudioReleaseLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_audioJitter.TryDequeue(out var pkt))
                {
                    // 无条件写入独立音频通道：该通道仅承载音频，DropOldest 仅丢最旧音频（偶发微疵），
                    // 视频突发绝不再能挤掉音频；抖动缓冲继续在生产者侧吸收后续音频。
                    await _audioChannel.Writer.WriteAsync(pkt, ct);
                }
                else
                {
                    await Task.Delay(2, ct); // 缓冲暂空：短暂让出，等待生产者
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (_audioJitter.TryDequeue(out var leftover)) leftover.Dispose();
        }
    }

    // ── 辅助方法 ──

    /// <summary>
    /// 播放后重取轨道元数据。
    /// </summary>
    /// <remarks>
    /// 关键修正：VLC 在「播放前」经 <c>libvlc_media_parse_with_options</c> 枚举的轨道常漏列视频轨
    /// （尤其启用硬解 <c>--avcodec-hw=any</c> 与无头 <c>--vout=dummy</c> 时），导致 <c>_videoTrackIndex</c> 恒为 -1、
    /// 视频内存回调不被注册、视频帧全丢。
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

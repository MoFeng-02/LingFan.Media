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
internal sealed partial class VLCNativeDemuxer : IMediaDemuxer
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
    // 同步 Close 等待音频释放线程排空的宽限上限：超时即放弃等待继续释放，
    // 避免 Close 被释放线程无限阻塞（同步边界兜底，正常排空远小于此值）。
    private const int AudioReleaseDrainTimeoutMs = 500;
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

        nint instance = _backend.Instance.Handle;

        // 地址式打开优先（location 打开比 imem 内存源更可靠）。
        // 地址式路径流本身不被读取（URL 交 VLC 网络栈打开），故不预建连（避免白建连接）；
        // imem 路径回调内同步读流，必须预建连（网络流）。
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
            await stream.ConnectAsync(ct).ConfigureAwait(false);
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
        try { _audioReleaseTask?.Wait(TimeSpan.FromMilliseconds(AudioReleaseDrainTimeoutMs)); } catch { }
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
}

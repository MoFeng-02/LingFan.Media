using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using LibVLCSharp.Shared;
using VLCMedia = LibVLCSharp.Shared.Media;

namespace LingFan.Media.Backends.VLC.Demuxer;

/// <summary>
/// 基于 LibVLCSharp 的 <see cref="IMediaDemuxer"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>VLC 架构适配</b>：VLC 内部一体化处理解封装+解码，不暴露原始压缩包。
/// 本实现将 VLC 的回调式帧交付适配为我们的拉取式 IMediaDemuxer 接口：
/// VLC 内部线程通过回调将解码帧写入 Channel，ReadPacketAsync 从 Channel 读取。</para>
/// <para>因此 VLCDemuxer 产出的 <see cref="MediaPacket"/> 携带的是<b>已解码帧数据</b>，
/// 而非压缩数据。VLCVideoDecoder/VLCAudioDecoder 为直通解码器（pass-through）。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><c>OpenAsync</c>：混合——<c>await stream.ConnectAsync</c>（真异步 I/O）+ <c>await media.Parse()</c>（VLC 内部真异步）+
/// <c>await Task.Run(StartPlayback)</c>（<b>伪异步</b>：VLC <c>MediaPlayer.Play</c> 为同步原生调用，Task.Run 仅卸载到线程池不阻塞调用线程，
/// 未来若 VLC 提供异步播放 API 应替换）。</item>
/// <item><c>ReadPacketAsync</c>：真异步——<c>await Channel.Reader.ReadAsync</c> 等待 VLC 回调交付帧（Channel 异步原语）。</item>
/// <item><c>SeekAsync</c>：<b>伪异步</b>——<c>await Task.Run</c> 卸载 <c>MediaPlayer.SeekTo</c>（同步原生调用）到线程池。
/// 未来若 VLC 提供异步 seek API 应替换。</item>
/// <item><c>InitializeAsync</c>：接口契约，返回 <c>Task.CompletedTask</c>。</item>
/// <item><c>Close</c> / <c>Dispose</c> / <c>DisposeAsync</c>：同步原生释放。</item>
/// </list>
/// <para><b>线程安全</b>：单线程使用（BufferManager 读取线程），VLC 回调从内部线程写入 Channel。</para>
/// <para><b>AOT 兼容</b>：sealed 类，委托存储为字段防 GC 回收，无反射。</para>
/// </remarks>
internal sealed class VLCDemuxer : IMediaDemuxer
{
    private readonly VLCBackend _backend;
    private readonly ILogger<VLCDemuxer> _logger;

    // VLC 资源
    private VLCMedia? _media;
    private MediaPlayer? _mediaPlayer;
    private MediaStreamInput? _mediaInput;
    private IMediaStream? _sourceStream; // 仅地址式打开时持有，Close 时关闭（MediaInput 路径由包装器关闭）

    // 帧交付 Channel
    private readonly Channel<MediaPacket> _frameChannel;

    // 视频回调委托（存储为字段防止 GC 回收）
    private readonly MediaPlayer.LibVLCVideoFormatCb _videoFormatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _videoCleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _videoLockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _videoUnlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _videoDisplayCb;

    // 音频回调委托
    private readonly MediaPlayer.LibVLCAudioSetupCb _audioSetupCb;
    private readonly MediaPlayer.LibVLCAudioCleanupCb _audioCleanupCb;
    private readonly MediaPlayer.LibVLCAudioPlayCb _audioPlayCb;
    private readonly MediaPlayer.LibVLCAudioPauseCb _audioPauseCb;
    private readonly MediaPlayer.LibVLCAudioResumeCb _audioResumeCb;
    private readonly MediaPlayer.LibVLCAudioFlushCb _audioFlushCb;
    private readonly MediaPlayer.LibVLCAudioDrainCb _audioDrainCb;

    // 视频缓冲区管理
    private IntPtr _videoBuffer = IntPtr.Zero;
    private int _videoWidth;
    private int _videoHeight;
    private int _videoPitch;
    private int _videoTrackIndex = -1;
    private long _videoFrameCounter;        // 自增帧计数，用于合成单调递增视频 PTS（VLC 内存回调不向 lock/unlock 传递帧 PTS）
    private long _videoFrameDurationTicks;  // 单帧时长(ticks)，来自视频轨帧率；OnVideoUnlock 据此合成真实 PTS

    // 音频格式
    private int _audioSampleRate;
    private int _audioChannels;
    private int _audioBytesPerSample = 2; // S16N（每样本 2 字节）——OnAudioSetup 固定为该格式
    private SampleFormat _audioSampleFormat = SampleFormat.S16; // 与固定格式 S16N 对齐
    private int _audioTrackIndex = -1;
    private long _audioSampleCounter;       // 累计已交付样本数(每声道)，用于合成流内相对音频 PTS（见 OnAudioPlay 根因注释）

    // 时间轴基准(ticks)：音视频合成 PTS 的共同起点。常态为 0；SeekAsync 后重定位到目标位置，
    // 使 seek 后新产出的帧仍处于与主时钟一致的流内时间轴上。
    private long _ptsBaseTicks;

    // 状态
    private bool _opened;
    private bool _disposed;
    private IReadOnlyList<LingFan.Media.Abstractions.MediaTrack> _tracks = Array.Empty<LingFan.Media.Abstractions.MediaTrack>();
    private MediaMetadata _metadata = new();

    /// <summary>
    /// 初始化 <see cref="VLCDemuxer"/> 的新实例。
    /// </summary>
    public VLCDemuxer(VLCBackend backend, ILogger<VLCDemuxer> logger)
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

        // 初始化回调委托
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
    public IReadOnlyList<LingFan.Media.Abstractions.MediaTrack> Tracks => _tracks;

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

        // ── 地址式打开优先（修复 imem 错误）──
        // stream.Location 对文件流返回完整路径、对网络流返回 URL，契约上本就「供需要按地址打开的 backend 使用」。
        // 直接交 VLC 原生访问+解封装（file/https/rtsp…），稳定且<b>不经 imem 模块</b>。
        // 旧路径用 MediaStreamInput 包 IMediaStream 走 imem access 模块，VLC 3.x 下 imem 的 get/release 指针校验失败，
        // 报 "[imem demux error: Invalid get/release function pointers]" → Parse 出 0 轨道/0 时长 →
        // 下方回调因 trackIndex<0 未注册 → 无帧捕获、音频经 VLC 默认输出逸出（用户听到声音但看不到可视化）。
        string? location = stream.Location;
        if (!string.IsNullOrEmpty(location))
        {
            _sourceStream = stream;
            _media = new VLCMedia(_backend.LibVLC, location);
        }
        else
        {
            // 无地址（内存/透传流）：回退 imem 路径。MediaStreamInput 实现 IMediaStream 同步读/定位边界，
            // 供无 Location 的字节流使用（此分支仍受 imem 3.x 限制，罕见路径）。
            _mediaInput = new MediaStreamInput(stream);
            _media = new VLCMedia(_backend.LibVLC, _mediaInput);
        }

        // 🔴 强制 amem 音频输出为 S16N（小端 16 位交织 PCM）：
        // OnAudioPlay 按固定 2 字节/样本、SampleFormat.S16 消费（见 OnAudioSetup 的 ABI 约束——不敢读
        // format 参数）。VLC amem 默认虽为 S16N，但某些源/版本会以 FL32(float32,4 字节/样本) 交付，
        // 导致 Marshal.Copy 只拷前半段 + WASAPI 按 S16 解读 float32 数据 → 音调偏高/失真（「声音不对」），
        // 且音频实际播放速度变 2× 使主时钟 2× 推进 → 视频相对「提前」。显式强制 S16N 使硬编码的 S16
        // 假设恒成立，消除该不确定性（已用 :amem-format=s16l；若 VLC 拒绝会在日志报错）。
        _media.AddOption(":amem-format=s16l");

        await _media.Parse(MediaParseOptions.ParseLocal | MediaParseOptions.FetchLocal, -1, ct).ConfigureAwait(false);

        _tracks = ParseTracks(_media);
        _metadata = ParseMetadata(_media);

        foreach (var track in _tracks)
        {
            if (track.Type == LingFan.Media.Abstractions.TrackType.Video && _videoTrackIndex < 0)
                _videoTrackIndex = track.Index;
            else if (track.Type == LingFan.Media.Abstractions.TrackType.Audio && _audioTrackIndex < 0)
                _audioTrackIndex = track.Index;
        }

        // 计算单帧时长（ticks），供 OnVideoUnlock 合成单调递增的真实视频 PTS。
        // VLC 内存回调(SetVideoCallbacks)的 lock/unlock/display 均不向回调传递帧 PTS，
        // 旧代码用 _mediaPlayer.Time（VLC 播放游标，解码时刻取值，与帧本身无关）打戳 → 帧戳失真。
        // 改用「帧计数 × 单帧时长」合成流内相对 PTS（CFR 近似）：单调递增、从 0 起，
        // 与 OnAudioPlay 的「累计样本 / 采样率」处于同一时间轴基准，可直接与主时钟比对。
        double fps = 30.0;
        foreach (var t in _tracks)
        {
            if (t.Type == LingFan.Media.Abstractions.TrackType.Video && t.VideoInfo != null && t.VideoInfo.FrameRate > 0)
            {
                fps = t.VideoInfo.FrameRate;
                break;
            }
        }
        _videoFrameDurationTicks = (long)(TimeSpan.TicksPerSecond / fps);

        // 伪异步：VLC MediaPlayer.Play 为同步原生调用，Task.Run 仅卸载到线程池避免阻塞调用线程。
        // 未来改进：若 VLC 提供异步播放 API，应替换为真异步调用。
        await Task.Run(() => StartPlayback(), ct).ConfigureAwait(false);

        _opened = true;
        _logger.LogInformation("VLC 打开成功: {TrackCount} 条轨道, 时长 {Duration}",
            _tracks.Count, _metadata.Duration);
    }

    // TODO: 无头高并发优化点 (Headless/High-Concurrency)
    // 1. 将 Task.Run 改为 TCS 事件驱动 (等待 Playing 事件)
    // 2. LibVLC 构造需注入 --no-video --vout=dummy 等无头参数
    // 3. Marshal.Copy 改为 ArrayPool 复用或 Span 零拷贝
    private void StartPlayback()
    {
        _mediaPlayer = new MediaPlayer(_backend.LibVLC)
        {
            EnableHardwareDecoding = _backend.Options.EnableHardwareDecoding
        };

        if (_videoTrackIndex >= 0)
        {
            _mediaPlayer.SetVideoFormatCallbacks(_videoFormatCb, _videoCleanupCb);
            _mediaPlayer.SetVideoCallbacks(_videoLockCb, _videoUnlockCb, _videoDisplayCb);
        }

        if (_audioTrackIndex >= 0)
        {
            _mediaPlayer.SetAudioFormatCallback(_audioSetupCb, _audioCleanupCb);
            _mediaPlayer.SetAudioCallbacks(_audioPlayCb, _audioPauseCb, _audioResumeCb, _audioFlushCb, _audioDrainCb);
        }

        _mediaPlayer.Play(_media!);
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
            {
                return packet;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _mediaPlayer == null)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        // 伪异步：VLC MediaPlayer.SeekTo 为同步原生调用，Task.Run 仅卸载到线程池。
        // 未来改进：若 VLC 提供异步 seek API，应替换为真异步调用。
        return await Task.Run(() =>
        {
            // 时间轴重定位：音视频 PTS 由「计数 × 单位时长」合成、恒自基准起算，
            // seek 后若不换基准，新帧仍从 0 递增 → 与主时钟错位、被同步器全判 Drop。
            // 先置基准再 SeekTo：SeekTo 会触发 flush 与新帧回调，基准须早于首个新帧就位。
            _ptsBaseTicks = position.Ticks;
            _videoFrameCounter = 0;
            _audioSampleCounter = 0;
            _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(position.TotalMilliseconds));
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (!_opened) return;
        _opened = false;

        if (_mediaPlayer != null)
        {
            try { _mediaPlayer.Stop(); }
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

        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        _media?.Dispose();
        _media = null;
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

    // ── VLC 视频回调 ──

    private uint OnVideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // VLC 期望通过 chroma 指针写入 FourCC
        Marshal.WriteInt32(chroma, (int)FourCC("BGRA"));
        pitches = width * 4;
        lines = height;

        _videoWidth = (int)width;
        _videoHeight = (int)height;
        _videoPitch = (int)pitches;
        _videoFrameCounter = 0; // 新格式/新流起点：重置 PTS 合成计数

        if (_videoBuffer != IntPtr.Zero)
            Marshal.FreeHGlobal(_videoBuffer);
        _videoBuffer = Marshal.AllocHGlobal((int)(pitches * lines));

        return 1;
    }

    private void OnVideoCleanup(ref IntPtr opaque)
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

        // 视频帧 PTS：优先锚定 VLC 真实呈现时刻（mediaPlayer.Time，流内相对毫秒→ticks）。
        // 旧实现用「帧计数 × 单帧时长」CFR 合成——一旦帧率探测偏差或源为 VFR，视频时间轴便与音频
        // （按真实样本数合成）错位，表现为「帧提前/滞后」；且音视频各自从 0 独立累加，存在固定起始偏移。
        // 改用呈现时刻后，视频直接挂在 VLC 播放时钟上（seek 后 mediaPlayer.Time 自动从新位置起算），
        // 与音频（样本合成，同处媒体时间轴）天然对齐，根除合成偏差。
        // 回退：mediaPlayer 不可用或 Time<=0 时退回 CFR 合成（先取值后自增，首帧 PTS=_ptsBaseTicks）。
        long ptsTicks;
        if (_mediaPlayer != null && _mediaPlayer.Time > 0)
            ptsTicks = TimeSpan.FromMilliseconds(_mediaPlayer.Time).Ticks;
        else
        {
            ptsTicks = _ptsBaseTicks + _videoFrameCounter * _videoFrameDurationTicks;
            _videoFrameCounter++;
        }
        var pts = TimeSpan.FromTicks(ptsTicks);

        var packet = new MediaPacket(
            _videoTrackIndex, data,
            pts,
            TimeSpan.Zero, keyFrame: true,
            width: _videoWidth, height: _videoHeight, stride: _videoPitch);

        _frameChannel.Writer.TryWrite(packet);
    }

    // display 回调：帧已在 OnVideoUnlock 交付通道，此处无需动作。
    // ⚠ 曾在此加「实时 Thread.Sleep 节流」以为要把 VLC「推式超速解码」压到实时——【该假设已被实测证伪】：
    // 两次运行 VideoDroppedFrames 均恰为 985 = 30fps × 32.83s = 媒体完整帧数，说明【一帧都没在
    // _frameChannel(64/DropOldest) 里被挤掉】，全部抵达 Synchronizer 后才被判 Drop；且墙钟耗时 ≈ 媒体时长
    // ⇒ VLC 的 vout 本就按媒体时钟实时限流，不存在灌帧。真因是音频 PTS 污染主时钟（见 OnAudioPlay）。
    // 故此处保持空实现——不要在 VLC 回调线程上做阻塞 sleep（会拖住 VLC vout 线程并引发其内部丢帧）。
    private void OnVideoDisplay(IntPtr opaque, IntPtr picture) { }

    // ── VLC 音频回调 ──

    private int OnAudioSetup(ref IntPtr opaque, ref IntPtr format, ref uint rate, ref uint channels)
    {
        // 🔴 关键 ABI 约束（踩坑两次后的结论）：
        // LibVLCSharp 把 AudioSetupCallback 的 format 声明为 ref IntPtr，但 VLC 原生
        // vlc_audio_setup_cb 的 format 是 char*（指向 4 字节格式缓冲，按值传递）。
        // 托管封送器把 ref IntPtr 当成 char**，对 format 的读取/写回都会多解一次间接：
        //   · 读取 format → 野指针 → Marshal.PtrToStringAnsi 崩溃 (0xC0000005，初版 bug)
        //   · 写回 format = buffer → 向 4 字节缓冲写入 8 字节指针 → 破坏 VLC 栈 (stack smashing，二版 bug)
        // 因此【绝不触碰 format】：VLC 的 amem 音频输出默认即以 S16N(16 位交织) 交付，
        // 我们按固定 2 字节/样本、SampleFormat.S16 消费即可（与 LibVLCSharp 官方样例一致）。
        _audioSampleRate = (int)rate;
        _audioChannels = (int)channels;
        _audioBytesPerSample = 2;            // S16N：16 位有符号，每样本 2 字节
        _audioSampleFormat = SampleFormat.S16;
        _audioSampleCounter = 0;             // 新音频格式/新流起点：重置 PTS 合成计数
        _logger.LogInformation("VLC amem 音频格式协商: rate={Rate}Hz channels={Channels}（已强制 S16N；消费侧固定 2 字节/样本）",
            _audioSampleRate, _audioChannels);
        return 0;
    }

    private void OnAudioCleanup(IntPtr opaque)
    {
        // 音频格式缓冲不再由我们分配（见 OnAudioSetup 的 ABI 约束），此处无需释放。
    }

    private void OnAudioPause(IntPtr data, long pts)
    {
        // VLC 音频暂停回调——无需处理
    }

    private void OnAudioResume(IntPtr data, long pts)
    {
        // VLC 音频恢复回调——无需处理
    }

    private void OnAudioFlush(IntPtr data, long pts)
    {
        // VLC 音频刷新回调——仅丢弃缓冲中的<b>音频</b>包，保留视频包。
        // 修复 H7：原实现清空整个 _frameChannel，会连带丢弃尚未消费的视频帧。
        // 音视频共用单一 FIFO 通道，无法按轨选择性出队，故读出后分类：
        // 音频包 Dispose 释放缓冲；视频包暂存后按原顺序写回（已移除总量 >= 保留视频数，写回不会溢出有界通道）。
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

    private void OnAudioDrain(IntPtr data)
    {
        // VLC 音频排空回调——VLC 已播完所有数据，无需处理
    }

    // 🔴 根因（VLC 后端「视频 0 交付 / 985 全丢、音频却全到」的真凶，2026-08-06 收口）：
    // libvlc 的 audio play 回调形参 pts 有两个易踩的性质——
    //   ① 单位是【微秒】(int64)，不是毫秒；
    //   ② 属于【libvlc 绝对时钟域】(vlc_tick_now，≈系统单调时钟)，不是流内相对时间。
    // 旧代码 `TimeSpan.FromMilliseconds(pts)` 同时踩中两条：µs 当 ms（×1000）+ 绝对时刻当流时间。
    // 实测后果：音频包时间戳 ≈ 4.165e7 秒（探针 pos 列显示 41,650,190s，且以 1000 倍速前进；
    // 反推 4.165e10µs = 11.57h ≈ 当时系统开机时长，完全吻合）。
    // 该戳经 AudioPipeline → Synchronizer.SyncTo 成为【主时钟】，于是每个视频帧：
    //   delta = videoPTS(0~32s) - clockTime(4.165e7s) ≈ -4.16e7 秒 << -DropThreshold(200ms)
    //   → Synchronizer 判「严重落后」→【985 帧无一幸免全部 Drop】。
    // 而音频不经同步器 Drop 逻辑（宪法：音频不套 FrameChannel，直投 IAudioOutput），故 678 帧照常送达
    // ——「音频正常、视频全丢」这一非对称表象正是主时钟被污染的特征信号。
    // 修复：弃用 VLC 的绝对 pts，改按【累计样本数 / 采样率】合成流内相对 PTS。
    // PCM 下该式精确无误差、单调递增、自 0 起，与 OnVideoUnlock 的「帧计数 × 单帧时长」共用
    // 同一时间轴基准（_ptsBaseTicks），音视频天然对齐。
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

    private static uint FourCC(string s)
        => ((uint)s[0]) | ((uint)s[1] << 8) | ((uint)s[2] << 16) | ((uint)s[3] << 24);

    private static IReadOnlyList<LingFan.Media.Abstractions.MediaTrack> ParseTracks(VLCMedia media)
    {
        var tracks = new List<LingFan.Media.Abstractions.MediaTrack>();

        foreach (var vlcTrack in media.Tracks)
        {
            LingFan.Media.Abstractions.MediaTrack? track = vlcTrack.TrackType switch
            {
                LibVLCSharp.Shared.TrackType.Video => new LingFan.Media.Abstractions.MediaTrack
                {
                    Index = tracks.Count,
                    Type = LingFan.Media.Abstractions.TrackType.Video,
                    VideoCodec = MapVideoCodec(FourCCToString(vlcTrack.Codec)),
                    BitRate = (long)vlcTrack.Bitrate,
                    VideoInfo = new VideoTrackInfo
                    {
                        Width = (int)vlcTrack.Data.Video.Width,
                        Height = (int)vlcTrack.Data.Video.Height,
                        FrameRate = vlcTrack.Data.Video.FrameRateNum > 0 && vlcTrack.Data.Video.FrameRateDen > 0
                            ? (float)vlcTrack.Data.Video.FrameRateNum / vlcTrack.Data.Video.FrameRateDen
                            : 0,
                        Duration = media.Duration > 0
                            ? TimeSpan.FromMilliseconds(media.Duration)
                            : TimeSpan.Zero
                    }
                },
                LibVLCSharp.Shared.TrackType.Audio => new LingFan.Media.Abstractions.MediaTrack
                {
                    Index = tracks.Count,
                    Type = LingFan.Media.Abstractions.TrackType.Audio,
                    AudioCodec = MapAudioCodec(FourCCToString(vlcTrack.Codec)),
                    BitRate = (long)vlcTrack.Bitrate,
                    AudioInfo = new AudioTrackInfo
                    {
                        SampleRate = (int)vlcTrack.Data.Audio.Rate,
                        Channels = (int)vlcTrack.Data.Audio.Channels,
                        BitsPerSample = 0,
                        Duration = media.Duration > 0
                            ? TimeSpan.FromMilliseconds(media.Duration)
                            : TimeSpan.Zero
                    }
                },
                LibVLCSharp.Shared.TrackType.Text => new LingFan.Media.Abstractions.MediaTrack
                {
                    Index = tracks.Count,
                    Type = LingFan.Media.Abstractions.TrackType.Subtitle,
                    SubtitleCodec = MapSubtitleCodec(FourCCToString(vlcTrack.Codec))
                },
                _ => null
            };

            if (track != null)
                tracks.Add(track);
        }

        return tracks;
    }

    /// <summary>
    /// 将 VLC 的 FourCC (uint) 转换为 4 字符字符串。
    /// </summary>
    private static string FourCCToString(uint fourcc)
    {
        return new string(new char[]
        {
            (char)(fourcc & 0xFF),
            (char)((fourcc >> 8) & 0xFF),
            (char)((fourcc >> 16) & 0xFF),
            (char)((fourcc >> 24) & 0xFF)
        });
    }

    private static MediaMetadata ParseMetadata(VLCMedia media)
    {
        TimeSpan duration = media.Duration > 0
            ? TimeSpan.FromMilliseconds(media.Duration)
            : TimeSpan.Zero;

        return new MediaMetadata
        {
            Title = media.Meta(MetadataType.Title),
            Artist = media.Meta(MetadataType.Artist),
            Album = media.Meta(MetadataType.Album),
            Genre = media.Meta(MetadataType.Genre),
            Year = int.TryParse(media.Meta(MetadataType.Date), out int y) ? y : null,
            Duration = duration,
            ContainerFormat = ContainerFormat.Unknown
        };
    }

    private static VideoCodec MapVideoCodec(string? codec) => codec?.ToUpperInvariant() switch
    {
        "H264" or "AVC" or "AVC1" => VideoCodec.H264,
        "H265" or "HEVC" or "HVC1" => VideoCodec.H265,
        "AV01" or "AV1" => VideoCodec.AV1,
        "VP09" or "VP9" => VideoCodec.VP9,
        "MP2V" or "MPEG2" => VideoCodec.MPEG2,
        "MP4V" or "MPEG4" => VideoCodec.MPEG4,
        _ => VideoCodec.Unknown
    };

    private static AudioCodec MapAudioCodec(string? codec) => codec?.ToUpperInvariant() switch
    {
        "MP4A" or "AAC" => AudioCodec.AAC,
        "MP3 " or "MP3" or "MPEG" => AudioCodec.MP3,
        "OPUS" or "OPU" => AudioCodec.Opus,
        "FLAC" or "FLA" => AudioCodec.FLAC,
        "VORB" or "VORBIS" => AudioCodec.Vorbis,
        "S16N" or "S16L" or "PCM " or "PCM" => AudioCodec.PCM,
        "AC3 " or "AC3" or "A52" => AudioCodec.AC3,
        _ => AudioCodec.Unknown
    };

    private static SubtitleCodec MapSubtitleCodec(string? codec) => codec?.ToUpperInvariant() switch
    {
        "SUBT" or "SRT" or "SUBRIP" => SubtitleCodec.SRT,
        "SSA " or "ASS " or "SSA" or "ASS" => SubtitleCodec.ASS,
        "WEBVTT" or "VTT" => SubtitleCodec.WebVTT,
        "PGS " or "PGS" or "HDMV" => SubtitleCodec.PGS,
        "VOBSUB" or "SPU " or "DVD" => SubtitleCodec.VobSub,
        _ => SubtitleCodec.Unknown
    };
}

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

    // 音频格式
    private int _audioSampleRate;
    private int _audioChannels;
    private int _audioTrackIndex = -1;

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

        _mediaInput = new MediaStreamInput(stream);
        _media = new VLCMedia(_backend.LibVLC, _mediaInput);

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
            EnableHardwareDecoding = true
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

        var packet = new MediaPacket(
            _videoTrackIndex, data,
            TimeSpan.FromMilliseconds(_mediaPlayer?.Time ?? 0),
            TimeSpan.Zero, keyFrame: true);

        _frameChannel.Writer.TryWrite(packet);
    }

    private void OnVideoDisplay(IntPtr opaque, IntPtr picture) { }

    // ── VLC 音频回调 ──

    private int OnAudioSetup(ref IntPtr opaque, ref IntPtr format, ref uint rate, ref uint channels)
    {
        _audioSampleRate = (int)rate;
        _audioChannels = (int)channels;
        return 0;
    }

    private void OnAudioCleanup(IntPtr opaque) { }

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
        // VLC 音频刷新回调——清空 Channel 中的待播数据
        while (_frameChannel.Reader.TryRead(out _)) { }
    }

    private void OnAudioDrain(IntPtr data)
    {
        // VLC 音频排空回调——VLC 已播完所有数据，无需处理
    }

    private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        if (_audioTrackIndex < 0) return;

        int dataSize = (int)count * _audioChannels * 2;
        byte[] audioData = new byte[dataSize];
        Marshal.Copy(samples, audioData, 0, dataSize);

        var packet = new MediaPacket(
            _audioTrackIndex, audioData,
            TimeSpan.FromMilliseconds(pts),
            TimeSpan.Zero, keyFrame: true);

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

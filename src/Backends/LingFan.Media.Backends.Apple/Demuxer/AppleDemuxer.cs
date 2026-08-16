using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Apple.Shared;

namespace LingFan.Media.Backends.Apple.Demuxer;

/// <summary>
/// 基于 Apple AVFoundation <c>AVAssetReader</c> passthrough 的 <see cref="IMediaDemuxer"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>设计</b>：AVAssetReader 以 <c>outputSettings=nil</c> 直出<b>压缩</b> CMSampleBuffer（不解码），
/// 与契约要求的「demuxer → decoder 分离」一致：本类只拆包，解码交给 <see cref="AppleVideoDecoder"/> / <see cref="AppleAudioDecoder"/>。</para>
/// <para><b>多轨交织</b>：为每个视频/音频轨建一个 <c>AVAssetReaderTrackOutput</c>，<see cref="ReadPacketAsync"/> 轮询各输出、
/// 按轨道索引打标返回；解码器据 <c>TrackIndex</c> 路由。轮询非严格 PTS 有序，但解码器侧有缓冲/重排，B0/B1 可接受。</para>
/// <para><b>关键帧判定</b>：<c>kCMSampleAttachmentKey_NotSync</c> 标记非同步帧（与 FFmpeg / Mozilla 判定一致）。</para>
/// <para><b>定位（Seek）</b>：AVAssetReader 不支持在读过程中改 timeRange，故 Seek 重建 Reader（保留 asset 与轨道句柄），
/// 经 <c>setTimeRange:</c> 设定 <c>[position, +∞)</c> 后重新 startReading。</para>
/// <para><b>异步策略</b>（与 MFBackend 对称）：<see cref="OpenAsync"/> 混合 <c>await stream.ConnectAsync</c> +
/// <c>Task.Run</c> 伪异步；<see cref="ReadPacketAsync"/> / <see cref="SeekAsync"/> 经 <c>Task.Run</c> 卸载同步原生调用；
/// <see cref="Close"/> / <see cref="Dispose"/> 同步原生释放。</para>
/// <para><b>仅 Apple 可用</b>：非 Apple 运行时 OpenAsync 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para><b>地址限制</b>：AVAssetReader 仅支持按地址（文件/URL）打开；内存/透传流（<see cref="IMediaStream.Location"/> 为 null）
/// 抛 <see cref="PlatformNotSupportedException"/>（诚实失败，绝不假绿）。</para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2050",
    Justification = "无 [ComImport]，使用原始 [LibraryImport] P/Invoke，不会被裁剪器移除。仅 Apple 运行时使用。")]
internal sealed class AppleDemuxer : IMediaDemuxer
{
    private readonly AppleBackend _backend;
    private readonly ILogger<AppleDemuxer> _logger;

    private IMediaStream? _stream;
    private nint _asset;            // AVURLAsset（+1 自有）
    private nint _tracksArray;      // [asset tracks]（CFRetain）
    private List<SourceTrack>? _sourceTracks;
    private nint _reader;           // AVAssetReader（+1 自有）
    private List<AppleTrackOutput>? _outputs;
    private int _nextOutput;

    private bool _opened;
    private bool _disposed;

    private IReadOnlyList<MediaTrack> _tracks = Array.Empty<MediaTrack>();
    private MediaMetadata _metadata = new();

    private sealed class SourceTrack
    {
        public int Index;
        public nint Track = nint.Zero;   // CFRetain
        public TrackType Type;
        public VideoCodec? VideoCodec;
        public AudioCodec? AudioCodec;
        public byte[] Extra = Array.Empty<byte>();
    }

    private sealed class AppleTrackOutput
    {
        public int Index;
        public nint Output = nint.Zero; // AVAssetReaderTrackOutput（+1 自有）
        public bool Eof;
    }

    /// <summary>初始化 Apple 解封装器的新实例。</summary>
    public AppleDemuxer(AppleBackend backend, ILogger<AppleDemuxer> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <inheritdoc/>
    public MediaMetadata Metadata => _metadata;

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask; // 接口契约：无 I/O
    }

    /// <inheritdoc/>
    public async Task OpenAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_opened)
            throw new InvalidOperationException("AppleDemuxer 实例已打开，不可重复打开；请新建实例。");

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS())
            throw new PlatformNotSupportedException(
                "Apple 解封装器仅支持 Apple 运行时（macOS / iOS）。请使用 FFmpeg 作为跨平台后端。");

        string? location = stream.Location;
        if (string.IsNullOrEmpty(location))
            throw new PlatformNotSupportedException(
                "Apple 解封装器（AVAssetReader）仅支持按地址打开（文件路径或 http(s) URL），不支持内存/透传流。");

        ct.ThrowIfCancellationRequested();
        _stream = stream;

        await stream.ConnectAsync(ct).ConfigureAwait(false); // 文件流为无操作

        try
        {
            await Task.Run(() => OpenCore(location!), ct).ConfigureAwait(false);
            _opened = true;
        }
        catch
        {
            CloseSync();
            throw;
        }

        _logger.LogInformation("[APPLE-DEMUX] 打开成功: {Count} 条轨道, 时长 {Duration}",
            _tracks.Count, _metadata.Duration);
    }

    private void OpenCore(string location)
    {
        _asset = AppleAvFoundation.CreateUrlAsset(location);
        if (_asset == nint.Zero)
            throw new InvalidOperationException("[APPLE-DEMUX] 无法创建 AVURLAsset（路径无效或不可访问）");

        _tracksArray = AppleAvFoundation.GetTracks(_asset);
        if (_tracksArray == nint.Zero)
            throw new InvalidOperationException("[APPLE-DEMUX] 无法取得资产轨道数组");

        int count = AppleAvFoundation.GetArrayCount(_tracksArray);
        var source = new List<SourceTrack>(count);
        var mediaTracks = new List<MediaTrack>(count);

        for (int i = 0; i < count; i++)
        {
            nint track = AppleAvFoundation.GetArrayObject(_tracksArray, i);
            if (track == nint.Zero) continue;
            nint retained = AppleRuntime.CFRetain(track);

            string? mt = AppleAvFoundation.GetTrackMediaType(track);
            TrackType type;
            if (mt == "vide") type = TrackType.Video;
            else if (mt == "soun") type = TrackType.Audio;
            else if (mt == "sbtl") type = TrackType.Subtitle;
            else
            {
                // 非音视频/字幕轨道（如闭封面、元数据）跳过，不纳入解封装
                AppleRuntime.CFRelease(retained);
                continue;
            }

            var st = new SourceTrack { Index = i, Track = retained, Type = type };

            if (type == TrackType.Video)
            {
                uint sub = AppleAvFoundation.GetTrackCodecSubType(track);
                st.VideoCodec = AppleCodecMaps.FourCharToVideoCodec(sub);
                int w = AppleAvFoundation.GetTrackWidth(track);
                int h = AppleAvFoundation.GetTrackHeight(track);
                st.Extra = AppleAvFoundation.GetTrackExtraData(track);
                var vinfo = new VideoTrackInfo
                {
                    Width = w,
                    Height = h,
                    FrameRate = 0,
                    PixelFormat = PixelFormat.YUV420P, // 占位；解码器初始化时由 VideoToolbox 输出格式定
                    Duration = TimeSpan.Zero,
                    CodecConfiguration = st.Extra,
                };
                mediaTracks.Add(new MediaTrack
                {
                    Index = i,
                    Type = TrackType.Video,
                    VideoCodec = st.VideoCodec,
                    VideoInfo = vinfo,
                    Language = null,
                });
            }
            else if (type == TrackType.Audio)
            {
                uint sub = AppleAvFoundation.GetTrackCodecSubType(track);
                st.AudioCodec = AppleCodecMaps.FourCharToAudioCodec(sub);
                st.Extra = AppleAvFoundation.GetTrackExtraData(track);
                var ainfo = new AudioTrackInfo
                {
                    SampleRate = 0,
                    Channels = 0,
                    BitsPerSample = 0,
                    CodecConfiguration = st.Extra,
                    Duration = TimeSpan.Zero,
                };
                mediaTracks.Add(new MediaTrack
                {
                    Index = i,
                    Type = TrackType.Audio,
                    AudioCodec = st.AudioCodec,
                    AudioInfo = ainfo,
                    Language = null,
                });
            }
            else
            {
                mediaTracks.Add(new MediaTrack { Index = i, Type = type });
            }

            source.Add(st);
        }

        _sourceTracks = source;
        _tracks = mediaTracks;

        // 元数据：时长 + 容器格式
        AppleRuntime.CMTime dur = AppleAvFoundation.GetAssetDuration(_asset);
        _metadata = new MediaMetadata
        {
            Duration = TimeSpan.FromSeconds(dur.ToSeconds()),
            ContainerFormat = AppleCodecMaps.ContainerFromLocation(location),
        };

        CreateReader(TimeSpan.Zero);
    }

    /// <summary>创建 AVAssetReader 并为每个视频/音频轨绑定 passthrough 输出。start &gt; 0 时设定 timeRange 实现定位。</summary>
    private void CreateReader(TimeSpan start)
    {
        CloseReaderOnly();

        nint error = nint.Zero;
        _reader = AppleAvFoundation.CreateAssetReader(_asset, out error);
        if (_reader == nint.Zero)
        {
            throw new InvalidOperationException(
                $"[APPLE-DEMUX] AVAssetReader 创建失败（error={(long)error}）。");
        }

        if (start > TimeSpan.Zero)
        {
            AppleRuntime.CMTimeRangeMake(out var range, AppleRuntime.CMTime.FromTicks(start.Ticks), AppleRuntime.CMTimePositiveInfinity);
            AppleRuntime.objc_msgSend(_reader, AppleRuntime.Sel("setTimeRange:"), ref range);
        }

        _outputs = new List<AppleTrackOutput>();
        foreach (var st in _sourceTracks!)
        {
            if (st.Type != TrackType.Video && st.Type != TrackType.Audio) continue;
            nint output = AppleAvFoundation.CreateTrackOutput(st.Track, nint.Zero); // passthrough
            if (output == nint.Zero) continue;
            AppleAvFoundation.AssetReaderAddOutput(_reader, output);
            _outputs.Add(new AppleTrackOutput { Index = st.Index, Output = output, Eof = false });
        }

        if (!AppleAvFoundation.AssetReaderStartReading(_reader))
            throw new InvalidOperationException("[APPLE-DEMUX] AVAssetReader startReading 失败。");

        _nextOutput = 0;
    }

    /// <inheritdoc/>
    public async ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _outputs is null)
            throw new InvalidOperationException("解封装器未打开，请先调用 OpenAsync。");
        ct.ThrowIfCancellationRequested();

        return await Task.Run(() => ReadPacketCore(), ct).ConfigureAwait(false);
    }

    private MediaPacket? ReadPacketCore()
    {
        var outputs = _outputs!;
        if (outputs.Count == 0) return null;
        if (outputs.TrueForAll(o => o.Eof)) return null;

        int start = _nextOutput % outputs.Count;
        for (int k = 0; k < outputs.Count; k++)
        {
            int idx = (start + k) % outputs.Count;
            var o = outputs[idx];
            if (o.Eof) continue;

            nint sbuf = AppleAvFoundation.CopyNextSampleBuffer(o.Output);
            if (sbuf == nint.Zero)
            {
                o.Eof = true;
                continue;
            }

            try
            {
                // 标记/空样本（无数据 buffer）→ 跳过，不产出空包
                nint dataBuffer = AppleRuntime.CMSampleBufferGetDataBuffer(sbuf);
                if (dataBuffer == nint.Zero) continue;

                AppleRuntime.CMBlockBufferGetDataPointer(
                    dataBuffer, 0, out _, out nuint totalLength, out nint dataPtr);
                if (dataPtr == nint.Zero || totalLength == 0) continue;

                var data = new byte[(int)totalLength];
                Marshal.Copy(dataPtr, data, 0, (int)totalLength);

                double pts = AppleRuntime.CMSampleBufferGetPresentationTimeStamp(sbuf).ToSeconds();
                double dur = AppleRuntime.CMSampleBufferGetDuration(sbuf).ToSeconds();
                bool notSync = AppleAvFoundation.IsNotSyncSample(sbuf);

                _nextOutput = (idx + 1) % outputs.Count;
                return new MediaPacket(
                    o.Index, data, TimeSpan.FromSeconds(pts), TimeSpan.FromSeconds(dur), !notSync);
            }
            finally
            {
                // copyNextSampleBuffer 返回 +1（Create 规则），此处平衡
                AppleRuntime.CFRelease(sbuf);
            }
        }

        return null; // 全部 EOF
    }

    /// <inheritdoc/>
    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _sourceTracks is null) return false;
        ct.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            try
            {
                CreateReader(position);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[APPLE-DEMUX] Seek 到 {Position} 失败", position);
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Close() => CloseSync();

    private void CloseSync()
    {
        _opened = false;
        CloseReaderOnly();

        if (_sourceTracks is not null)
        {
            foreach (var st in _sourceTracks)
                if (st.Track != nint.Zero) AppleRuntime.CFRelease(st.Track);
            _sourceTracks = null;
        }

        if (_tracksArray != nint.Zero)
        {
            AppleRuntime.CFRelease(_tracksArray);
            _tracksArray = nint.Zero;
        }
        if (_asset != nint.Zero)
        {
            AppleRuntime.CFRelease(_asset);
            _asset = nint.Zero;
        }
        _stream = null;
    }

    private void CloseReaderOnly()
    {
        if (_reader != nint.Zero)
        {
            AppleAvFoundation.AssetReaderCancelReading(_reader);
            AppleRuntime.CFRelease(_reader);
            _reader = nint.Zero;
        }
        if (_outputs is not null)
        {
            foreach (var o in _outputs)
                if (o.Output != nint.Zero) AppleRuntime.CFRelease(o.Output);
            _outputs = null;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        CloseSync();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseSync();
    }
}

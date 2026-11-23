using LingFan.Media.Abstractions;
using LingFan.Media.Apple.Shared;

namespace LingFan.Media.Backends.Apple.Demuxer;

/// <summary>
/// 资产打开分部：AVURLAsset 轨道枚举、轨道/元数据构建与 AVAssetReader passthrough 输出绑定。
/// </summary>
internal sealed partial class AppleDemuxer
{
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
}
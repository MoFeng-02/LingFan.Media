using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LingFan.Media.Backends.VLCNative.Demuxer;

// VLCNativeDemuxer 的 Tracks 职责段（部分类，由 VLCNativeDemuxer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Open/ReadPacket/Seek/Close/Dispose）在主文件；VLC 回调式帧交付适配为拉取式接口。
internal sealed partial class VLCNativeDemuxer
{
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

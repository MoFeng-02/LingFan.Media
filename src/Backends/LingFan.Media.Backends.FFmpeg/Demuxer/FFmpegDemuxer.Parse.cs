using System.IO;
using System.Runtime.InteropServices;
using LingFan.Media.Backends.FFmpeg.Interop;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Demuxer;

// FFmpegDemuxer 的 Parse 职责段（部分类，由 FFmpegDemuxer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Open/ReadPacket/Seek/Close/Dispose）在主文件；libavformat 同步 C 调用经 Task.Run 卸载。
internal sealed partial class FFmpegDemuxer
{
    /// <summary>获取流的时间基（转换为秒的 double）。</summary>
    private static unsafe double GetTimeBase(AVFormatContext* fmtCtx, int streamIndex)
    {
        if (streamIndex < 0 || streamIndex >= (int)fmtCtx->nb_streams)
            return 0;
        AVRational tb = fmtCtx->streams[streamIndex]->time_base;
        return FF.av_q2d(tb);
    }

    /// <summary>从 ffmpeg 流参数拷贝 <c>extradata</c>（SPS+PPS / AudioSpecificConfig 等）到托管内存。
    /// 返回默认空表示无 extradata。</summary>
    /// <remarks>托管拷贝以确保生命周期独立于 AVStream（AVFormatContext 关闭后指针即失效）。</remarks>
    private static unsafe ReadOnlyMemory<byte> CopyExtradata(AVCodecParameters* codecPar)
    {
        if (codecPar->extradata == null || codecPar->extradata_size <= 0)
            return default;
        var bytes = new byte[codecPar->extradata_size];
        Marshal.Copy((IntPtr)codecPar->extradata, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>解析所有轨道信息。</summary>
    private static unsafe IReadOnlyList<MediaTrack> ParseTracks(AVFormatContext* fmtCtx)
    {
        var tracks = new List<MediaTrack>((int)fmtCtx->nb_streams);

        for (uint i = 0; i < fmtCtx->nb_streams; i++)
        {
            AVStream* avStream = fmtCtx->streams[i];
            AVCodecParameters* codecPar = (AVCodecParameters*)avStream->codecpar;
            string? language = GetStreamLanguage(avStream);
            double timeBase = GetTimeBase(fmtCtx, (int)i);
            TimeSpan streamDuration = avStream->duration > 0
                ? TimeSpan.FromTicks((long)(avStream->duration * timeBase * TimeSpan.TicksPerSecond))
                : TimeSpan.Zero;

            // 流时间基（AVStream.time_base）：ffmpeg 解码帧的 pts/dts 以此为单位。须透传给解码器写入
            // ctx->pkt_timebase，否则解码后 avFrame->time_base / ctx->time_base 常为 0，帧时间戳全 0
            // （视频不节流突发提交、主时钟被 SyncTo(0) 钉死、pos 不前进）。den==0 表示无有效时间基，回落 default。
            AVRational rawTb = avStream->time_base;
            Rational trackTimeBase = rawTb.den > 0 ? new Rational(rawTb.num, rawTb.den) : default;

            MediaTrack? track = codecPar->codec_type switch
            {
                AVMediaType.AVMEDIA_TYPE_VIDEO => new MediaTrack
                {
                    Index = (int)i,
                    Type = TrackType.Video,
                    VideoCodec = MapVideoCodecFromFFmpeg(codecPar->codec_id),
                    Language = language,
                    BitRate = codecPar->bit_rate,
                    VideoInfo = new VideoTrackInfo
                    {
                        Width = codecPar->width,
                        Height = codecPar->height,
                        PixelFormat = MapPixelFormatFromFFmpeg((AVPixelFormat)codecPar->format),
                        FrameRate = GetFrameRate(avStream),
                        Duration = streamDuration,
                        TimeBase = trackTimeBase,
                        // 透传编解码器私有配置（H264/H265 的 SPS+PPS 等）。解码器需据此设置 extradata，
                        // 否则 MP4 中 length-prefixed 的 HEVC/H264 包无法被解码器解析（No start code）。
                        CodecConfiguration = CopyExtradata(codecPar)
                    }
                },

                AVMediaType.AVMEDIA_TYPE_AUDIO => new MediaTrack
                {
                    Index = (int)i,
                    Type = TrackType.Audio,
                    AudioCodec = MapAudioCodecFromFFmpeg(codecPar->codec_id),
                    Language = language,
                    BitRate = codecPar->bit_rate,
                    AudioInfo = new AudioTrackInfo
                    {
                        SampleRate = codecPar->sample_rate,
                        Channels = codecPar->ch_layout.nb_channels,
                        BitsPerSample = codecPar->bits_per_coded_sample,
                        TimeBase = trackTimeBase,
                        // 透传 AudioSpecificConfig 等。AAC 在 MP4 中为裸流，解码器必须据此设置 extradata。
                        CodecConfiguration = CopyExtradata(codecPar),
                        Duration = streamDuration
                    }
                },

                AVMediaType.AVMEDIA_TYPE_SUBTITLE => new MediaTrack
                {
                    Index = (int)i,
                    Type = TrackType.Subtitle,
                    SubtitleCodec = MapSubtitleCodecFromFFmpeg(codecPar->codec_id),
                    Language = language,
                    BitRate = codecPar->bit_rate
                },

                _ => null
            };

            if (track != null)
                tracks.Add(track);
        }

        return tracks;
    }

    /// <summary>解析容器元数据。</summary>
    private static unsafe MediaMetadata ParseMetadata(AVFormatContext* fmtCtx)
    {
        TimeSpan duration = fmtCtx->duration > 0
            ? TimeSpan.FromTicks(fmtCtx->duration * TimeSpan.TicksPerSecond / FF.AV_TIME_BASE)
            : TimeSpan.Zero;

        string? fmtName = null;
        if (fmtCtx->iformat != null)
        {
            fmtName = Marshal.PtrToStringUTF8((IntPtr)fmtCtx->iformat->name);
        }

        ContainerFormat containerFormat = MapContainerFormatFromFFmpeg(fmtName);

        // 提取元数据字典
        string? title = null, artist = null, album = null, genre = null;
        int? year = null;

        AVDictionaryEntry* entry = null;
        while (true)
        {
            entry = FF.av_dict_get(fmtCtx->metadata, "", entry, FF.AV_DICT_IGNORE_SUFFIX);
            if (entry == null) break;

            string key = Marshal.PtrToStringUTF8((IntPtr)entry->key) ?? string.Empty;
            string value = Marshal.PtrToStringUTF8((IntPtr)entry->value) ?? string.Empty;

            switch (key.ToLowerInvariant())
            {
                case "title": title = value; break;
                case "artist": artist = value; break;
                case "album": album = value; break;
                case "genre": genre = value; break;
                case "date" when int.TryParse(value, out int y): year = y; break;
            }
        }

        return new MediaMetadata
        {
            Title = title,
            Artist = artist,
            Album = album,
            Genre = genre,
            Year = year,
            Duration = duration,
            ContainerFormat = containerFormat
        };
    }

    /// <summary>获取流语言标签。</summary>
    private static unsafe string? GetStreamLanguage(AVStream* stream)
    {
        AVDictionaryEntry* entry = FF.av_dict_get(stream->metadata, "language", null, 0);
        if (entry == null) return null;
        return Marshal.PtrToStringUTF8((IntPtr)entry->value);
    }

    /// <summary>获取帧率。</summary>
    private static unsafe float GetFrameRate(AVStream* stream)
    {
        AVRational r = stream->avg_frame_rate;
        if (r.num == 0 || r.den == 0)
            r = stream->r_frame_rate;
        if (r.num == 0 || r.den == 0)
            return 0;
        return (float)r.num / r.den;
    }

    /// <summary>将 FFmpeg 错误码转换为可读字符串。</summary>
    private static string GetErrorString(int errorCode)
    {
        unsafe
        {
            byte* buf = stackalloc byte[FF.AV_ERROR_MAX_STRING_SIZE];
            FF.av_strerror(errorCode, buf, (UIntPtr)FF.AV_ERROR_MAX_STRING_SIZE);
            return Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"error code {errorCode}";
        }
    }

    private static VideoCodec MapVideoCodecFromFFmpeg(AVCodecID codecId) => codecId switch
    {
        AVCodecID.AV_CODEC_ID_H264 => VideoCodec.H264,
        AVCodecID.AV_CODEC_ID_HEVC => VideoCodec.H265,
        AVCodecID.AV_CODEC_ID_AV1 => VideoCodec.AV1,
        AVCodecID.AV_CODEC_ID_VP9 => VideoCodec.VP9,
        AVCodecID.AV_CODEC_ID_MPEG2VIDEO => VideoCodec.MPEG2,
        AVCodecID.AV_CODEC_ID_MPEG4 => VideoCodec.MPEG4,
        _ => VideoCodec.Unknown
    };

    private static AudioCodec MapAudioCodecFromFFmpeg(AVCodecID codecId) => codecId switch
    {
        AVCodecID.AV_CODEC_ID_AAC => AudioCodec.AAC,
        AVCodecID.AV_CODEC_ID_MP3 => AudioCodec.MP3,
        AVCodecID.AV_CODEC_ID_OPUS => AudioCodec.Opus,
        AVCodecID.AV_CODEC_ID_FLAC => AudioCodec.FLAC,
        AVCodecID.AV_CODEC_ID_VORBIS => AudioCodec.Vorbis,
        AVCodecID.AV_CODEC_ID_PCM_S16LE => AudioCodec.PCM,
        AVCodecID.AV_CODEC_ID_PCM_S24LE => AudioCodec.PCM,
        AVCodecID.AV_CODEC_ID_PCM_S32LE => AudioCodec.PCM,
        AVCodecID.AV_CODEC_ID_AC3 => AudioCodec.AC3,
        _ => AudioCodec.Unknown
    };

    private static SubtitleCodec MapSubtitleCodecFromFFmpeg(AVCodecID codecId) => codecId switch
    {
        AVCodecID.AV_CODEC_ID_SUBRIP => SubtitleCodec.SRT,
        AVCodecID.AV_CODEC_ID_ASS => SubtitleCodec.ASS,
        AVCodecID.AV_CODEC_ID_SSA => SubtitleCodec.ASS,
        AVCodecID.AV_CODEC_ID_WEBVTT => SubtitleCodec.WebVTT,
        AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE => SubtitleCodec.PGS,
        AVCodecID.AV_CODEC_ID_DVD_SUBTITLE => SubtitleCodec.VobSub,
        _ => SubtitleCodec.Unknown
    };

    private static PixelFormat MapPixelFormatFromFFmpeg(AVPixelFormat fmt) => fmt switch
    {
        AVPixelFormat.AV_PIX_FMT_YUV420P => PixelFormat.YUV420P,
        AVPixelFormat.AV_PIX_FMT_YUV422P => PixelFormat.YUV422P,
        AVPixelFormat.AV_PIX_FMT_YUV444P => PixelFormat.YUV444P,
        AVPixelFormat.AV_PIX_FMT_NV12 => PixelFormat.NV12,
        AVPixelFormat.AV_PIX_FMT_NV21 => PixelFormat.NV21,
        AVPixelFormat.AV_PIX_FMT_BGRA => PixelFormat.BGRA32,
        AVPixelFormat.AV_PIX_FMT_RGBA => PixelFormat.RGBA32,
        AVPixelFormat.AV_PIX_FMT_RGB24 => PixelFormat.RGB24,
        _ => PixelFormat.YUV420P
    };

    private static ContainerFormat MapContainerFormatFromFFmpeg(string? formatName)
    {
        if (string.IsNullOrEmpty(formatName)) return ContainerFormat.Unknown;
        if (formatName.Contains("mp4") || formatName.Contains("mov")) return ContainerFormat.MP4;
        if (formatName.Contains("matroska") || formatName.Contains("webm")) return ContainerFormat.MKV;
        if (formatName.Contains("avi")) return ContainerFormat.AVI;
        if (formatName.Contains("mpegts") || formatName.Contains("ts")) return ContainerFormat.TS;
        if (formatName.Contains("flv")) return ContainerFormat.FLV;
        return ContainerFormat.Unknown;
    }
}

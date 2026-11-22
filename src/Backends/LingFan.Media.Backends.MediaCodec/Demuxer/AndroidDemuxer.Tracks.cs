using Android.Media;
// Android.Media 亦含 MediaMetadata，与 Abstractions 全局冲突 → 别名锁定契约层类型。
using MediaMetadata = LingFan.Media.Abstractions.MediaMetadata;

namespace LingFan.Media.Backends.MediaCodec.Demuxer;

/// <summary>
/// 轨道解析分部：容器格式 → 元数据 + 轨道详情（一次性遍历各轨 MediaFormat）。
/// </summary>
internal sealed partial class AndroidDemuxer
{
    private (IReadOnlyList<MediaTrack> Tracks, MediaMetadata Metadata) ParseTracksAndMetadata(MediaExtractor extractor)
    {
        int count = extractor.TrackCount;
        var list = new List<MediaTrack>(count);
        long maxDurationUs = 0;
        string? containerMime = null;

        for (int i = 0; i < count; i++)
        {
            using var fmt = extractor.GetTrackFormat(i);

            // 容器格式优先读 AOSP 轨道格式上的 "file-format" 键（如 video/mp4、audio/mp4）。
            if (containerMime is null && fmt.ContainsKey("file-format"))
                containerMime = fmt.GetString("file-format");
            if (fmt.ContainsKey(MediaFormat.KeyDuration))
            {
                long d = fmt.GetLong(MediaFormat.KeyDuration);
                if (d > maxDurationUs) maxDurationUs = d;
            }

            string? mime = fmt.GetString(MediaFormat.KeyMime);
            if (string.IsNullOrEmpty(mime)) continue;
            var type = AndroidCodecMaps.MimeToTrackType(mime);
            var track = BuildTrack((int)i, type, mime, fmt);
            if (track is not null) list.Add(track);
        }

        var metadata = new MediaMetadata
        {
            Duration = maxDurationUs > 0 ? TimeSpan.FromTicks(maxDurationUs * 10) : TimeSpan.Zero,
            ContainerFormat = containerMime is null
                ? ContainerFormat.Unknown
                : AndroidCodecMaps.MimeToContainerFormat(containerMime),
        };
        return (list, metadata);
    }

    private MediaTrack? BuildTrack(int idx, TrackType type, string mime, MediaFormat fmt)
    {
        // 累计轨道 max-input-size，供解封装读取缓冲定容
        if (GetInteger(fmt, MediaFormat.KeyMaxInputSize, out int mis) && mis > _maxInputSize)
            _maxInputSize = mis;

        VideoTrackInfo? vinfo = null;
        AudioTrackInfo? ainfo = null;

        switch (type)
        {
            case TrackType.Video:
            {
                GetInteger(fmt, MediaFormat.KeyWidth, out int w);
                GetInteger(fmt, MediaFormat.KeyHeight, out int h);
                GetInteger(fmt, MediaFormat.KeyFrameRate, out int fps);
                long durUs = GetLong(fmt, MediaFormat.KeyDuration);
                var csd0 = ReadCsd(fmt);
                vinfo = new VideoTrackInfo
                {
                    Width = w,
                    Height = h,
                    FrameRate = fps,
                    // 解码器实际输出格式在 Initialize 时由输出媒体类型定，此处仅占位
                    PixelFormat = PixelFormat.YUV420P,
                    Duration = durUs > 0 ? TimeSpan.FromTicks(durUs * 10) : TimeSpan.Zero,
                    CodecConfiguration = csd0, // csd-0（SPS+PPS 等），init 属性，此处一并赋值
                };
                break;
            }

            case TrackType.Audio:
            {
                GetInteger(fmt, MediaFormat.KeySampleRate, out int sr);
                GetInteger(fmt, MediaFormat.KeyChannelCount, out int ch);
                // KeyPcmEncoding 为 API 24+：低版本无此键，按默认 0（BitsPerSample 归 0）处理。
                int enc = 0;
                if (OperatingSystem.IsAndroidVersionAtLeast(24))
                    GetInteger(fmt, MediaFormat.KeyPcmEncoding, out enc);
                long adurUs = GetLong(fmt, MediaFormat.KeyDuration);
                var acsd0 = ReadCsd(fmt);
                ainfo = new AudioTrackInfo
                {
                    SampleRate = sr,
                    Channels = ch,
                    BitsPerSample = enc == (int)Encoding.PcmFloat ? 32
                        : enc == (int)Encoding.Pcm16bit ? 16 : 0,
                    Duration = adurUs > 0 ? TimeSpan.FromTicks(adurUs * 10) : TimeSpan.Zero,
                    CodecConfiguration = acsd0, // byte[]? → ReadOnlyMemory<byte>
                };
                break;
            }

            case TrackType.Subtitle:
                // 当前后端无字幕解码器；仅列举轨道，不填充详情
                break;
        }

        var lang = fmt.GetString(MediaFormat.KeyLanguage);

        return new MediaTrack
        {
            Index = idx,
            Type = type,
            VideoCodec = type == TrackType.Video ? AndroidCodecMaps.MimeToVideoCodec(mime) : null,
            AudioCodec = type == TrackType.Audio ? AndroidCodecMaps.MimeToAudioCodec(mime) : null,
            VideoInfo = vinfo,
            AudioInfo = ainfo,
            Language = string.IsNullOrEmpty(lang) ? null : lang,
        };
    }

    /// <summary>读取整数键；不存在返回 false（MediaFormat.GetInteger 无键即抛，须先判 ContainsKey）。</summary>
    private static bool GetInteger(MediaFormat fmt, string key, out int value)
    {
        if (fmt.ContainsKey(key))
        {
            value = fmt.GetInteger(key);
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>读取长整型键（如 durationUs）；不存在返回 0。</summary>
    private static long GetLong(MediaFormat fmt, string key)
        => fmt.ContainsKey(key) ? fmt.GetLong(key) : 0;

    /// <summary>读取 csd-0/csd-1/…（拷贝为托管 byte[] 并依序拼接）；无则返回 null。键名 "csd-N"（AOSP <c>MediaFormat.KEY_CSD0/1/2</c>）。</summary>
    /// <remarks>
    /// H264 的 csd-0=SPS、csd-1=PPS（各自带 Annex-B 起始码）——<b>PPS 缺失时解码器无法解任何 slice</b>
    /// （表现为：queueInputBuffer 全成功、解码器持续释放输入缓冲，但 dequeue 恒 TRY_AGAIN、整流 0 帧产出），
    /// 故必须拼接完整参数集（多 NAL 同一 csd 缓冲合法且常见）。HEVC 的 csd-0 通常已含 VPS+SPS+PPS，
    /// 额外拼接无害。AAC 的 csd-0 单包完整，循环在 csd-1 处自然终止。
    /// </remarks>
    private static byte[]? ReadCsd(MediaFormat fmt)
    {
        byte[]? result = null;
        for (int i = 0; ; i++)
        {
            string key = $"csd-{i}";
            if (!fmt.ContainsKey(key)) break;
            var bb = fmt.GetByteBuffer(key);
            if (bb is null) break;
            int n = bb.Remaining();
            if (n <= 0) break;
            var chunk = new byte[n];
            bb.Get(chunk);
            if (result is null) { result = chunk; continue; }
            var merged = new byte[result.Length + chunk.Length];
            System.Buffer.BlockCopy(result, 0, merged, 0, result.Length);
            System.Buffer.BlockCopy(chunk, 0, merged, result.Length, chunk.Length);
            result = merged;
        }
        return result;
    }
}
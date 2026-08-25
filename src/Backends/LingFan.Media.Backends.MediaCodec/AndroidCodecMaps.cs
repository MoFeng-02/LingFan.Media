namespace LingFan.Media.Backends.MediaCodec;

/// <summary>
/// Android MediaCodec / MediaExtractor 的 MIME 字符串、编解码器枚举、像素格式与采样格式的互映射。
/// </summary>
/// <remarks>
/// <para>MIME 字符串严格取自 AOSP <c>MediaDefs</c>（<c>frameworks/base/media/java/android/media/MediaFormat.java</c>
/// 的 <c>MIMETYPE_*</c> 常量），不可臆改：</para>
/// <list type="bullet">
/// <item>video/avc、video/hevc、video/av01（AV1 基础类型，profile 串如 video/av01.0.00M.08 以此前缀匹配）、
/// video/x-vnd.on2.vp9（VP9 标准常量，非 video/vp9）、video/mpeg2、video/mp4v-es。</item>
/// <item>audio/mp4a-latm（AAC）、audio/mpeg（MP3）、audio/opus、audio/flac、audio/vorbis、
/// audio/raw（PCM）、audio/ac3。</item>
/// </list>
/// <para>本后端 100% Android 特化（依赖 <c>Android.Media.MediaCodecCapabilities</c>/<c>Encoding</c>），
/// 仅 net10.0-android 目标编译。均为纯值映射，AOT 安全（无反射、无字典反射）。</para>
/// </remarks>
internal static class AndroidCodecMaps
{
    // ── MIME → 枚举 ──

    public static VideoCodec MimeToVideoCodec(string mime)
    {
        if (mime.StartsWith("video/avc", StringComparison.Ordinal)) return VideoCodec.H264;
        if (mime.StartsWith("video/hevc", StringComparison.Ordinal)) return VideoCodec.H265;
        if (mime.StartsWith("video/av01", StringComparison.Ordinal)) return VideoCodec.AV1;
        if (mime.StartsWith("video/x-vnd.on2.vp9", StringComparison.Ordinal)
            || mime.StartsWith("video/vp9", StringComparison.Ordinal)) return VideoCodec.VP9;
        if (mime.StartsWith("video/mpeg2", StringComparison.Ordinal)) return VideoCodec.MPEG2;
        if (mime.StartsWith("video/mp4v-es", StringComparison.Ordinal)) return VideoCodec.MPEG4;
        return mime.StartsWith("video/", StringComparison.Ordinal) ? VideoCodec.Unknown : VideoCodec.Unknown;
    }

    public static AudioCodec MimeToAudioCodec(string mime)
    {
        return mime switch
        {
            _ when mime.StartsWith("audio/mp4a-latm", StringComparison.Ordinal) => AudioCodec.AAC,
            _ when mime.StartsWith("audio/mpeg", StringComparison.Ordinal) => AudioCodec.MP3,
            _ when mime.StartsWith("audio/opus", StringComparison.Ordinal) => AudioCodec.Opus,
            _ when mime.StartsWith("audio/flac", StringComparison.Ordinal) => AudioCodec.FLAC,
            _ when mime.StartsWith("audio/vorbis", StringComparison.Ordinal) => AudioCodec.Vorbis,
            _ when mime.StartsWith("audio/raw", StringComparison.Ordinal) => AudioCodec.PCM,
            _ when mime.StartsWith("audio/ac3", StringComparison.Ordinal) => AudioCodec.AC3,
            _ => AudioCodec.Unknown
        };
    }

    public static TrackType MimeToTrackType(string mime)
    {
        if (mime.StartsWith("video/", StringComparison.Ordinal)) return TrackType.Video;
        if (mime.StartsWith("audio/", StringComparison.Ordinal)) return TrackType.Audio;
        if (mime.StartsWith("text/", StringComparison.Ordinal)) return TrackType.Subtitle;
        return TrackType.Subtitle; // 未知按字幕（不安全，但至少可被列举）
    }

    // ── 枚举 → MIME ──

    public static string? VideoCodecToMime(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "video/avc",
        VideoCodec.H265 => "video/hevc",
        VideoCodec.AV1 => "video/av01",
        VideoCodec.VP9 => "video/x-vnd.on2.vp9",
        VideoCodec.MPEG2 => "video/mpeg2",
        VideoCodec.MPEG4 => "video/mp4v-es",
        _ => null
    };

    public static string? AudioCodecToMime(AudioCodec codec) => codec switch
    {
        AudioCodec.AAC => "audio/mp4a-latm",
        AudioCodec.MP3 => "audio/mpeg",
        AudioCodec.Opus => "audio/opus",
        AudioCodec.FLAC => "audio/flac",
        AudioCodec.Vorbis => "audio/vorbis",
        AudioCodec.PCM => "audio/raw",
        AudioCodec.AC3 => "audio/ac3",
        _ => null
    };

    // ── 容器格式（从 file format mime 推断）──

    public static ContainerFormat MimeToContainerFormat(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return ContainerFormat.Unknown;
        if (mime.Contains("mp4") || mime.Contains("mpeg4") || mime.Contains("quicktime")) return ContainerFormat.MP4;
        if (mime.Contains("matroska") || mime.Contains("webm")) return ContainerFormat.MKV;
        if (mime.Contains("mpegts") || mime.Contains("mp2t")) return ContainerFormat.TS;
        if (mime.Contains("avi")) return ContainerFormat.AVI;
        if (mime.Contains("flv")) return ContainerFormat.FLV;
        if (mime.Contains("webm")) return ContainerFormat.WebM;
        return ContainerFormat.Unknown;
    }

    // ── 色彩空间 NDK 值 → LingFan 枚举（YUV→RGB 矩阵选择）──

    /// <summary>将 AOSP key-color-standard 的 int 值映射到 <see cref="ColorStandard"/>。</summary>
    public static ColorStandard ColorStandardFromNdk(int value) => value switch
    {
        1 => ColorStandard.Bt709,      // COLOR_STANDARD_BT709
        2 => ColorStandard.Bt601,      // COLOR_STANDARD_BT601_PAL
        4 => ColorStandard.Bt601,      // COLOR_STANDARD_BT601_NTSC
        5 => ColorStandard.Bt2020,     // COLOR_STANDARD_BT2020
        _ => ColorStandard.Unspecified,
    };

    /// <summary>将 AOSP key-color-range 的 int 值映射到 <see cref="ColorRange"/>。</summary>
    public static ColorRange ColorRangeFromNdk(int value) => value switch
    {
        1 => ColorRange.Full,          // COLOR_RANGE_FULL
        2 => ColorRange.Limited,       // COLOR_RANGE_LIMITED
        _ => ColorRange.Unspecified,
    };

    /// <summary>将 AOSP key-color-transfer 的 int 值映射到 <see cref="ColorTransfer"/>。</summary>
    public static ColorTransfer ColorTransferFromNdk(int value) => value switch
    {
        1 => ColorTransfer.Linear,     // COLOR_TRANSFER_LINEAR
        3 => ColorTransfer.SdrVideo,   // COLOR_TRANSFER_SDR_VIDEO
        6 => ColorTransfer.St2084,     // COLOR_TRANSFER_ST2084
        7 => ColorTransfer.Hlg,        // COLOR_TRANSFER_HLG
        _ => ColorTransfer.Unspecified,
    };

    // ── pcm-encoding → 采样格式（音频解码输出）──

    /// <summary>将 AOSP pcm-encoding 值映射到 LingFan <see cref="SampleFormat"/>；不支持返回 null。</summary>
    /// <remarks>
    /// 值取自 net-android 非废弃的 <c>Android.Media.Encoding</c> 枚举（对应 AOSP <c>ENCODING_PCM_*</c>；
    /// 旧常量字段在 .NET 绑定已标 <c>[Obsolete(..., true)]</c>，不可引用）。
    /// <c>Pcm8bit</c> 与 <c>Pcm24bitPacked</c> 无对应 LingFan 枚举
    /// （8-bit 无 S8、24-bit 打包为 3 字节/样本），按「绝不假绿」原则返回 null 供调用方诚实失败。
    /// </remarks>
    public static SampleFormat? PcmEncodingToSampleFormat(int encoding)
    {
        return encoding switch
        {
            (int)Android.Media.Encoding.Pcm16bit => SampleFormat.S16,
            (int)Android.Media.Encoding.PcmFloat => SampleFormat.F32,
            // ENCODING_PCM_32BIT = 22（API 31）；枚举成员引用会触发 CA1416（Android 21 调用点），故用字面量。
            22 => SampleFormat.S32,
            _ => null
        };
    }

    /// <summary>采样格式每样本字节数。</summary>
    public static int BytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.S16 => 2,
        SampleFormat.S32 => 4,
        SampleFormat.F32 => 4,
        _ => 0
    };

    // ── H264 SPS 尺寸解析（解码器需显式 width/height，但容器声明尺寸可能与 SPS 不符）──

    /// <summary>
    /// 从 H264 的 csd-0（SPS/PPS）中定位 SPS NAL 并解析编码尺寸；解析失败返回 false。
    /// 容器（moov/tkhd）声明的 KeyWidth/KeyHeight 可能与 SPS 编码尺寸不一致，硬塞给 MTK/高通硬件解码器
    /// 会导致格式不匹配（dequeue 恒 TRY_AGAIN 或 configure 抛 IllegalArgumentException），故以 SPS 为准。
    /// </summary>
    public static bool TryParseH264WidthHeight(byte[] csd, out int width, out int height)
    {
        width = 0;
        height = 0;

        // 定位 SPS NAL（nal_unit_type == 7）。csd 可能为 Annex-B（含 00 00 01 起始码）或 4 字节长前缀，扫描即可。
        int spsStart = -1;
        for (int i = 0; i < csd.Length; i++)
        {
            if ((csd[i] & 0x1F) == 7) { spsStart = i; break; }
        }
        if (spsStart < 0 || spsStart + 1 >= csd.Length) return false;

        // 剥离 emulation prevention（RBSP 中 00 00 03 的 0x03 必须去掉，否则位错位）。
        // 从 NAL 头开始按输出序列累计连续零判定，标准做法。
        var rbsp = new byte[csd.Length - spsStart];
        int m = 0, zeros = 0;
        for (int i = spsStart; i < csd.Length; i++)
        {
            byte b = csd[i];
            if (zeros >= 2 && b == 3)
            {
                zeros = 0; // 跳过转义字节，且重置零计数（03 后不会再是零前缀）
                continue;
            }
            rbsp[m++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }
        int rbspLen = m;
        if (rbspLen < 4) return false;

        // 从 NAL 头之后（rbsp）解析。
        int bitPos = 8;

        int ReadBits(int n)
        {
            int v = 0;
            for (int k = 0; k < n && bitPos < rbspLen * 8; k++)
            {
                v = (v << 1) | ((rbsp[bitPos / 8] >> (7 - (bitPos % 8))) & 1);
                bitPos++;
            }
            return v;
        }

        int ReadUe()
        {
            int zeros2 = 0;
            while (bitPos < rbspLen * 8 && ReadBits(1) == 0) zeros2++;
            int val = (1 << zeros2) - 1;
            return zeros2 > 0 ? val + ReadBits(zeros2) : val;
        }

        int ReadSe()
        {
            int codeNum = ReadUe();
            return (codeNum & 1) == 0 ? -(codeNum >> 1) : (codeNum + 1) >> 1;
        }

        void ReadScalingList(int size)
        {
            // H.264 规范：nextScale==0 时 scalingList[j]=lastScale（lastScale 保持不变）。
            int lastScale = 8, nextScale = 8;
            for (int j = 0; j < size; j++)
            {
                if (nextScale != 0)
                {
                    nextScale = (lastScale + ReadSe() + 256) & 0xFF;
                    if (nextScale != 0) lastScale = nextScale;
                }
            }
        }

        byte profileIdc = (byte)ReadBits(8);

        // chroma_format_idc：default 1（4:2:0）；高 profile 才出现在码流中。
        uint chromaFormatIdc = 1;

        // 高 profile（色彩/位深扩展）跳过若干扩展字段。
        bool highProfile = profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128
            or 138 or 139 or 134 or 135;
        if (highProfile)
        {
            chromaFormatIdc = (uint)ReadUe();
            if (chromaFormatIdc == 3) ReadBits(1); // separate_colour_plane_flag
            ReadUe(); // bit_depth_luma_minus8
            ReadUe(); // bit_depth_chroma_minus8
            ReadBits(1); // qpprime_y_zero_transform_bypass_flag
            if (ReadBits(1) != 0) // seq_scaling_matrix_present_flag（High profile 常见：按规范读缩放表）
            {
                int lists = chromaFormatIdc == 3 ? 12 : 8;
                for (int i = 0; i < lists; i++)
                {
                    if (ReadBits(1) != 0) // seq_scaling_list_present_flag
                        ReadScalingList(i < 6 ? 16 : 64);
                }
            }
        }

        _ = ReadUe(); // seq_parameter_set_id
        _ = ReadUe(); // log2_max_frame_num_minus4
        int picOrderCntType = ReadUe();
        if (picOrderCntType == 0) _ = ReadUe(); // log2_max_pic_order_cnt_lsb_minus4
        else if (picOrderCntType == 1) { _ = ReadBits(1); _ = ReadUe(); _ = ReadUe(); _ = ReadUe(); }
        _ = ReadUe(); // max_num_ref_frames
        _ = ReadBits(1); // gaps_in_frame_num_value_allowed_flag

        long wMbsMinus1 = ReadUe();
        long hMapUnitsMinus1 = ReadUe();
        int frameMbsOnly = ReadBits(1);
        if (frameMbsOnly == 0) ReadBits(1); // mb_adaptive_frame_field_flag
        ReadBits(1); // direct_8x8_inference_flag

        long codedW = (wMbsMinus1 + 1) * 16;
        long codedH = (2 - frameMbsOnly) * (hMapUnitsMinus1 + 1) * 16;
        if (codedW <= 0 || codedH <= 0 || codedW > 8192 || codedH > 8192) return false;

        // 帧裁剪（frame_cropping）：从画面两侧裁掉未编码像素。
        if (ReadBits(1) != 0) // frame_cropping_flag
        {
            int cropL = ReadUe(), cropR = ReadUe(), cropT = ReadUe(), cropB = ReadUe();
            // 按 chroma_format_idc 取子采样比例：4:2:0→(2,2)、4:2:2→(2,1)、4:4:4→(1,1)。
            long cropUnitX = chromaFormatIdc == 3 ? 1 : chromaFormatIdc == 2 ? 2 : 2;
            long cropUnitY = chromaFormatIdc == 3 ? 1 : chromaFormatIdc == 2 ? 1 : 2;
            long wDel = cropUnitX * (cropL + cropR);
            long hDel = cropUnitY * (cropT + cropB);
            if (codedW > wDel) codedW -= wDel;
            if (codedH > hDel) codedH -= hDel;
        }

        width = (int)codedW;
        height = (int)codedH;
        return width > 0 && height > 0;
    }
}

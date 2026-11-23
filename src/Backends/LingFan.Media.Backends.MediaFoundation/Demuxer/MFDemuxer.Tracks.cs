using System.Runtime.Versioning;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Demuxer;

// MFDemuxer 的 Tracks 职责段（部分类，由 MFDemuxer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Open/ReadPacket/ExtractPacket/Seek/Close/Dispose）在主文件；
// IMFSourceReader 同步 COM 调用，全部在所属线程执行（COM 线程亲和）。
internal sealed partial class MFDemuxer
{
    /// <summary>
    /// 从 IMFAttributes 读一个原生自分配的宽字符串（GetAllocatedString，slotIndex 10）。仅用于 MFT 身份取证，非热路径。
    /// </summary>
    /// <remarks>
    /// 属性不存在时返回 <see langword="null"/>（MF_E_ATTRIBUTENOTFOUND）——对 MFT_ENUM_HARDWARE_URL_Attribute 而言，
    /// 「不存在」本身就是有效信息：该 MFT 为软件 MFT。原生 buffer 由 CoTaskMemAlloc 分配，必须 FreeCoTaskMem 配对释放。
    /// </remarks>
    private static string? TryGetAllocatedString(IntPtr attrs, Guid key)
    {
        IntPtr pStr = IntPtr.Zero;
        try
        {
            int hr = MfVTable.Get<IMFAttributes_GetAllocatedString>(attrs, 10)(attrs, ref key, out pStr, out _);
            if (hr < 0 || pStr == IntPtr.Zero)
                return null;
            return Marshal.PtrToStringUni(pStr);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (pStr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pStr);
        }
    }

    /// <summary>
    /// 解析 MF SourceReader 的轨道信息。
    /// </summary>
    /// <remarks>
    /// 实例方法（非 static）：需要 <c>_logger</c> 上报属性缺失/媒体类型协商失败——这些是静默失败的高发区，
    /// 无日志会让下游拿到 0Hz/0ch 或压缩裸流而无从诊断（勿改回 static）。
    /// </remarks>
    private IReadOnlyList<MediaTrack> ParseTracks(IntPtr readerPtr, TimeSpan containerDuration)
    {
        var tracks = new List<MediaTrack>();
        int index = 0;

        // GetNativeMediaType = 槽 5 → index 2
        var getNativeMediaType = MfVTable.Get<IMFSourceReader_GetNativeMediaType>(readerPtr, 2);

        // IMFMediaType vtable 委托（继承 IMFAttributes）：GetMajorType=槽33→30、GetUINT32=槽7→4、GetUINT64=槽8→5、GetGuid=槽10→7。
        // 在拿到首个 mediaType 指针后解析一次（所有 IMFMediaType 实例 vtable 相同），循环内复用。
        IMFMediaType_GetMajorType? getMajorType = null;
        IMFMediaType_GetUINT32? getUINT32 = null;
        IMFMediaType_GetUINT64? getUINT64 = null;
        IMFMediaType_GetGuid? getGuid = null;

        // 遍历所有流
        while (true)
        {
            int hr = getNativeMediaType(readerPtr, (uint)index, 0, out IntPtr mediaTypePtr);
            if (hr == MFConstants.MF_E_NO_MORE_TYPES || hr < 0)
                break;

            if (mediaTypePtr == IntPtr.Zero)
            {
                index++;
                continue;
            }

            if (getMajorType == null)
            {
                getMajorType = MfVTable.Get<IMFMediaType_GetMajorType>(mediaTypePtr, 30);
                getUINT32 = MfVTable.Get<IMFMediaType_GetUINT32>(mediaTypePtr, 4);
                getUINT64 = MfVTable.Get<IMFMediaType_GetUINT64>(mediaTypePtr, 5);
                getGuid = MfVTable.Get<IMFMediaType_GetGuid>(mediaTypePtr, 7);
            }

            getMajorType(mediaTypePtr, out Guid majorType);

            MediaTrack? track = null;

            if (majorType == MFConstants.MFMediaType_Video)
            {
                Guid subtypeKey = MFConstants.MF_MT_SUBTYPE;
                Guid frameSizeKey = MFConstants.MF_MT_FRAME_SIZE;
                getGuid!(mediaTypePtr, ref subtypeKey, out Guid subtype);
                getUINT64!(mediaTypePtr, ref frameSizeKey, out ulong frameSize);
                int width = (int)(frameSize >> 32);
                int height = (int)(frameSize & 0xFFFFFFFF);

                var vcodec = MapVideoCodec(subtype);
                if (vcodec == VideoCodec.Unknown)
                    _logger.LogWarning("[OPEN-DIAG] 未识别视频子类型 {Subtype} → 标记 Unknown（后端仅支持 H264/H265；AV1/VP9/MPEG 等需扩 codec 路由）", subtype);

                // 把该视频流协商为 NV12【解码后】输出
                // 仅在创建期成功挂上 D3D 管理器时才尝试；成功后 SourceReader 会自行加载硬件解码 MFT
                // 并在共享 D3D11 设备上分配输出表面 ⇒ ReadSample 直出可 QI 成 IMFDXGIBuffer 的样本。
                // 必须在构造 MediaTrack **之前**完成：VideoTrackInfo 为 init-only，尺寸须一次性写定
                // （硬件 MFT 可能把显示尺寸对齐到编码尺寸，回读实测值比沿用原生类型更可靠）。
                // 失败/未启用 ⇒ _decodedVideoStreamIndex 保持 -1，该流继续输出压缩裸流，走原 MFVideoDecoder MFT 路径。
                // EnableReaderDecodeFusion=false 是零拷贝定界开关：主动放弃一体化，把解码交回自管 MFT，
                //    用两条路径的帧落点差异判定「读回」发生在 SourceReader 封装层还是 MFT/驱动层。
                if (!_options.EnableReaderDecodeFusion && _decodedVideoStreamIndex < 0)
                {
                    _logger.LogWarning(
                        "[MF-D3D] 诊断开关 EnableReaderDecodeFusion=false —— 跳过 NV12 一体化协商，"
                        + "视频流 {Index} 继续输出压缩裸流，改由 MFVideoDecoder 自管 MFT 解码（用于零拷贝定界）", index);
                }
                else if (_hardwareReaderRequested && _decodedVideoStreamIndex < 0)
                {
                    if (TryConfigureVideoStreamToNv12(readerPtr, index, ref width, ref height, out int decodedStride))
                    {
                        _decodedVideoStreamIndex = index;
                        _decodedVideoWidth = width;
                        _decodedVideoHeight = height;
                        _decodedVideoStride = decodedStride;

                        // 拓扑既已成型，立刻取证 SourceReader 真实建了什么 MFT 链（零拷贝失效成因定位）。
                        DiagnoseStreamTransformChain(readerPtr, index);
                    }
                }

                track = new MediaTrack
                {
                    Index = index,
                    Type = TrackType.Video,
                    VideoCodec = vcodec,
                    VideoInfo = new VideoTrackInfo
                    {
                        Width = width,
                        Height = height,
                        Duration = containerDuration
                    }
                };

                // 提取 H264/H265 解码必需的 out-of-band SPS+PPS（Annex-B 序列头）。
                // MP4(AVCC) 容器内 SPS/PPS 在 avcC 盒、不在每个 sample 内联；不透传给解码器则
                // IMFTransform::ProcessOutput 永久返回 MF_E_TRANSFORM_NEED_MORE_INPUT。
                // 优先直取 MF_MT_MPEG_SEQUENCE_HEADER；缺失则从 MF_MT_MPEG4_SAMPLE_DESCRIPTION（整个 stsd 盒）解析 avcC。
                // 注：早期「MF 媒体源不会填 MF_MT_MPEG_SEQUENCE_HEADER」的结论建立在错误 GUID 上（恒 ATTRIBUTENOTFOUND），
                //     GUID 已依 SDK 头文件修正，两条路径产出均为 Annex-B，可安全并存。
                if (track.VideoCodec is VideoCodec.H264 or VideoCodec.H265)
                {
                    var seqHeader = TryGetBlob(mediaTypePtr, MFConstants.MF_MT_MPEG_SEQUENCE_HEADER);
                    if (seqHeader.Length == 0)
                    {
                        var stsd = TryGetBlob(mediaTypePtr, MFConstants.MF_MT_MPEG4_SAMPLE_DESCRIPTION);
                        if (stsd.Length > 0)
                            seqHeader = ParseAvcCToAnnexB(stsd);
                    }
                    track.VideoInfo!.CodecConfiguration = seqHeader;
                }
            }
            else if (majorType == MFConstants.MFMediaType_Audio)
            {
                Guid subtypeKey = MFConstants.MF_MT_SUBTYPE;
                Guid sampleRateKey = MFConstants.MF_MT_AUDIO_SAMPLES_PER_SECOND;
                Guid channelsKey = MFConstants.MF_MT_AUDIO_NUM_CHANNELS;
                Guid bitsPerSampleKey = MFConstants.MF_MT_AUDIO_BITS_PER_SAMPLE;
                getGuid!(mediaTypePtr, ref subtypeKey, out Guid audioSubtype);

                // 必须检查 HRESULT：GetUINT32 失败时 out 参数为 0，静默吞掉会让下游拿到
                // SampleRate=0 / Channels=0 去初始化 WASAPI（属性键 GUID 写错时正是如此，
                // 且因无 hr 检查而长期无声失败）。缺失时回落到 CD 音质默认并告警。
                if (getUINT32!(mediaTypePtr, ref sampleRateKey, out uint sampleRate) < 0 || sampleRate == 0)
                {
                    _logger.LogWarning("音频流 {Index} 缺少 MF_MT_AUDIO_SAMPLES_PER_SECOND，回落 44100Hz", index);
                    sampleRate = 44100;
                }
                if (getUINT32!(mediaTypePtr, ref channelsKey, out uint channels) < 0 || channels == 0)
                {
                    _logger.LogWarning("音频流 {Index} 缺少 MF_MT_AUDIO_NUM_CHANNELS，回落 2 声道", index);
                    channels = 2;
                }
                if (getUINT32!(mediaTypePtr, ref bitsPerSampleKey, out uint bitsPerSample) < 0 || bitsPerSample == 0)
                {
                    _logger.LogWarning("音频流 {Index} 缺少 MF_MT_AUDIO_BITS_PER_SAMPLE，回落 16bit", index);
                    bitsPerSample = 16;
                }

                // 关键：SourceReader 默认输出**压缩原生格式**（AAC/MP3 裸流）。
                // MFAudioDecoder 是直通实现（不自带 MFT），若不在此显式协商为 PCM，
                // 下游会把 AAC 字节当成 S16 PCM 直喂 WASAPI → 噪声/静音。
                var pcm = ConfigureAudioStreamToPcm(readerPtr, index, sampleRate, channels, bitsPerSample);

                track = new MediaTrack
                {
                    Index = index,
                    Type = TrackType.Audio,
                    AudioCodec = MapAudioCodec(audioSubtype), // 保留源编码标识（AAC/MP3），供 UI/诊断显示
                    AudioInfo = new AudioTrackInfo
                    {
                        // 注意：此处为 SourceReader **输出**（解码后 PCM）的实测参数，
                        // 而非容器内压缩流的参数——MediaPlayer 据此初始化 WASAPI 设备，必须是输出侧。
                        SampleRate = pcm.SampleRate,
                        Channels = pcm.Channels,
                        BitsPerSample = pcm.BitsPerSample,
                        Duration = containerDuration
                    }
                };
            }

            if (track != null)
            {
                tracks.Add(track);
            }

            Marshal.Release(mediaTypePtr);
            index++;
        }

        return tracks;
    }

    /// <summary>读取 IMFAttributes Blob 属性（GetAllocatedBlob=slot13，AOT 安全：原生自分配 buffer 并返回指针+长度，
    /// 与已工作的 GetBlobSize 同形，彻底绕开 GetBlob「原生向调用方 buffer 大块写入」在 AOT 发布二进制静默 AV 退出的路径）。
    /// 属性不存在返回空数组。</summary>
    private static byte[] TryGetBlob(IntPtr attributesPtr, Guid key)
    {
        // Guid 用 GCHandle 固定后传 IntPtr 地址（与 GetBlobSize/GetAllocatedBlob 的 IntPtr guidKey 一致）。
        var kh = GCHandle.Alloc(key, GCHandleType.Pinned);
        IntPtr keyPtr = kh.AddrOfPinnedObject();
        IntPtr blobPtr = IntPtr.Zero;
        uint size = 0;
        try
        {
            var getAllocatedBlob = MfVTable.Get<IMFAttributes_GetAllocatedBlob>(attributesPtr, 13);
            try
            {
                if (getAllocatedBlob(attributesPtr, keyPtr, out blobPtr, out size) < 0 || size == 0 || blobPtr == IntPtr.Zero)
                {
                    return Array.Empty<byte>();
                }
                var result = new byte[size];
                Marshal.Copy(blobPtr, result, 0, (int)size);
                return result;
            }
            catch (Exception)
            {
                // 原生属性读取异常（极少见）不应中断 Open；缺失序列头的解码失败会在后续 ProcessOutput 暴露
                return Array.Empty<byte>();
            }
            finally
            {
                // GetAllocatedBlob 用 CoTaskMem 分配，须 FreeCoTaskMem（非 FreeHGlobal）。
                if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
            }
        }
        finally
        {
            kh.Free();
        }
    }

    /// <summary>
    /// 从 stsd 盒数据（MF_MT_MPEG4_SAMPLE_DESCRIPTION 透传）中定位 avcC / hvcC 盒，
    /// 把参数集（SPS/PPS/VPS）转换为 Annex-B 序列头（00 00 00 01 起始码拼接）。
    /// 解析失败返回空数组（解码器侧兜底：无序列头时不设置 MF_MT_MPEG_SEQUENCE_HEADER）。
    /// </summary>
    private static byte[] ParseAvcCToAnnexB(byte[] stsd)
    {
        // avcC 盒（ISO/IEC 14496-15 5.3.3.1）：configurationVersion(1) profile(1) compat(1) level(1)
        //   lengthSizeMinusOne(1, 低2位) numOfSPS(1, 低5位) { spsLen(2BE) sps } numOfPPS(1) { ppsLen(2BE) pps }
        int avcc = IndexOfFourCC(stsd, (byte)'a', (byte)'v', (byte)'c', (byte)'C');
        if (avcc >= 0)
        {
            var output = new List<byte>(64);
            int p = avcc + 4; // 跳过 fourcc，指向 configurationVersion
            if (p + 6 > stsd.Length) return Array.Empty<byte>();
            int numSps = stsd[p + 5] & 0x1F;
            p += 6;
            for (int i = 0; i < numSps; i++)
                if (!AppendLengthPrefixedNal(stsd, ref p, output)) return Array.Empty<byte>();
            if (p >= stsd.Length) return Array.Empty<byte>();
            int numPps = stsd[p];
            p += 1;
            for (int i = 0; i < numPps; i++)
                if (!AppendLengthPrefixedNal(stsd, ref p, output)) return Array.Empty<byte>();
            return output.ToArray();
        }

        // hvcC 盒（ISO/IEC 14496-15 8.3.3.1）：22 字节固定头 + numOfArrays(1) +
        //   每数组 { arrayHeader(1) numNalus(2BE) { naluLen(2BE) nalu } }（含 VPS/SPS/PPS 数组）
        int hvcc = IndexOfFourCC(stsd, (byte)'h', (byte)'v', (byte)'c', (byte)'C');
        if (hvcc >= 0)
        {
            var output = new List<byte>(128);
            int p = hvcc + 4 + 22;
            if (p >= stsd.Length) return Array.Empty<byte>();
            int numArrays = stsd[p];
            p += 1;
            for (int a = 0; a < numArrays; a++)
            {
                if (p + 3 > stsd.Length) return Array.Empty<byte>();
                int numNalus = (stsd[p + 1] << 8) | stsd[p + 2];
                p += 3;
                for (int n = 0; n < numNalus; n++)
                    if (!AppendLengthPrefixedNal(stsd, ref p, output)) return Array.Empty<byte>();
            }
            return output.ToArray();
        }

        return Array.Empty<byte>();
    }

    /// <summary>读取「2 字节大端长度 + NAL 数据」并以 00 00 00 01 起始码追加到 output。越界返回 false。</summary>
    private static bool AppendLengthPrefixedNal(byte[] data, ref int p, List<byte> output)
    {
        if (p + 2 > data.Length) return false;
        int len = (data[p] << 8) | data[p + 1];
        p += 2;
        if (len <= 0 || p + len > data.Length) return false;
        output.Add(0); output.Add(0); output.Add(0); output.Add(1);
        for (int i = 0; i < len; i++) output.Add(data[p + i]);
        p += len;
        return true;
    }

    /// <summary>在字节数组中查找 4 字节 fourcc，返回起始索引；未找到返回 -1。</summary>
    private static int IndexOfFourCC(byte[] data, byte c0, byte c1, byte c2, byte c3)
    {
        for (int i = 0; i + 4 <= data.Length; i++)
            if (data[i] == c0 && data[i + 1] == c1 && data[i + 2] == c2 && data[i + 3] == c3)
                return i;
        return -1;
    }

    private static AudioCodec MapAudioCodec(Guid subtype) => subtype switch
    {
        _ when subtype == MFConstants.MFAudioFormat_AAC => AudioCodec.AAC,
        _ when subtype == MFConstants.MFAudioFormat_MP3 => AudioCodec.MP3,
        _ when subtype == MFConstants.MFAudioFormat_PCM => AudioCodec.PCM,
        _ => AudioCodec.Unknown
    };

    private static VideoCodec MapVideoCodec(Guid subtype) => subtype switch
    {
        _ when subtype == MFConstants.MFVideoFormat_H264 => VideoCodec.H264,
        _ when subtype == MFConstants.MFVideoFormat_H265 => VideoCodec.H265,
        _ when subtype == MFConstants.MFVideoFormat_HEVC => VideoCodec.H265,
        _ when subtype == MFConstants.MFVideoFormat_HEVC_ES => VideoCodec.H265,
        _ => VideoCodec.Unknown
    };
}

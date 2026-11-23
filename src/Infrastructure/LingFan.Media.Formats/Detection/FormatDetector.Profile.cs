using System.Buffers;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Formats.Detection;

/// <summary>
/// Profile 探测分部：容器 + 视频编码轻量探测（IFormatDetector 契约实现）与编码标识串搜索。
/// </summary>
public partial class FormatDetector
{
    /// <summary>同步轻量探测 (容器, 视频编码)。不可定位流返回 Unknown/Unknown。</summary>
    /// <remarks>实现 <see cref="IFormatDetector.DetectProfile"/>。</remarks>
    public MediaFormatProfile DetectProfile(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            return new MediaFormatProfile(ContainerFormat.Unknown, VideoCodec.Unknown);

        long originalPosition = stream.Position;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProbeBufferSize);
        try
        {
            int totalRead = 0;
            while (totalRead < ProbeBufferSize)
            {
                int read = stream.Read(buffer.AsSpan(totalRead, ProbeBufferSize - totalRead));
                if (read == 0) break;
                totalRead += read;
            }
            if (totalRead == 0)
                return new MediaFormatProfile(ContainerFormat.Unknown, VideoCodec.Unknown);

            var container = DetectFormat(buffer.AsSpan(0, totalRead));
            var video = container == ContainerFormat.Unknown
                ? VideoCodec.Unknown
                : ProbeVideoCodec(container, buffer.AsSpan(0, totalRead));
            return new MediaFormatProfile(container, video);
        }
        catch (IOException)
        {
            return new MediaFormatProfile(ContainerFormat.Unknown, VideoCodec.Unknown);
        }
        catch (OperationCanceledException)
        {
            return new MediaFormatProfile(ContainerFormat.Unknown, VideoCodec.Unknown);
        }
        finally
        {
            try { stream.Seek(originalPosition, SeekOrigin.Begin); }
            catch (Exception) { /* best-effort */ }
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>异步轻量探测 (容器, 视频编码)。不可定位流返回 Unknown/Unknown。</summary>
    /// <remarks>实现 <see cref="IFormatDetector.DetectProfileAsync"/>。</remarks>
    public async Task<MediaFormatProfile> DetectProfileAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            return new MediaFormatProfile(ContainerFormat.Unknown, VideoCodec.Unknown);

        long originalPosition = stream.Position;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProbeBufferSize);
        try
        {
            int totalRead = 0;
            while (totalRead < ProbeBufferSize)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, ProbeBufferSize - totalRead), ct)
                    .ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }
            if (totalRead == 0)
                return new MediaFormatProfile(ContainerFormat.Unknown, VideoCodec.Unknown);

            var container = DetectFormat(buffer.AsSpan(0, totalRead));
            var video = container == ContainerFormat.Unknown
                ? VideoCodec.Unknown
                : ProbeVideoCodec(container, buffer.AsSpan(0, totalRead));
            return new MediaFormatProfile(container, video);
        }
        catch (IOException)
        {
            return new MediaFormatProfile(ContainerFormat.Unknown, VideoCodec.Unknown);
        }
        catch (OperationCanceledException)
        {
            return new MediaFormatProfile(ContainerFormat.Unknown, VideoCodec.Unknown);
        }
        finally
        {
            try { stream.Seek(originalPosition, SeekOrigin.Begin); }
            catch (Exception) { /* best-effort */ }
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 在已识别容器前提下，于探测头内搜索该容器的已知视频编码标识串，映射为 <see cref="VideoCodec"/>。
    /// 纯 ASCII 子串搜索（零分配），假阳性极低（仅在对应容器下搜对应编码串）。
    /// 搜索不到返回 <see cref="VideoCodec.Unknown"/>，调用方退回全试回退（无害，不污染缓存）。
    /// </summary>
    private static VideoCodec ProbeVideoCodec(ContainerFormat container, ReadOnlySpan<byte> data)
    {
        switch (container)
        {
            case ContainerFormat.MP4:
                if (ContainsAscii(data, "hvc1") || ContainsAscii(data, "hev1")) return VideoCodec.H265;
                if (ContainsAscii(data, "avc1") || ContainsAscii(data, "avc3")) return VideoCodec.H264;
                if (ContainsAscii(data, "av01")) return VideoCodec.AV1;
                if (ContainsAscii(data, "vp09")) return VideoCodec.VP9;
                if (ContainsAscii(data, "mp4v")) return VideoCodec.MPEG4;
                return VideoCodec.Unknown;
            case ContainerFormat.WebM:
            case ContainerFormat.MKV:
                if (ContainsAscii(data, "V_VP9")) return VideoCodec.VP9;
                if (ContainsAscii(data, "V_MPEGH/ISO/HEVC")) return VideoCodec.H265;
                if (ContainsAscii(data, "V_MPEG4/ISO/AVC")) return VideoCodec.H264;
                if (ContainsAscii(data, "V_AV1")) return VideoCodec.AV1;
                if (ContainsAscii(data, "V_MPEG2V")) return VideoCodec.MPEG2;
                return VideoCodec.Unknown;
            case ContainerFormat.FLV:
                // 解析首个 video tag：tagType(1)=9，data[0] 高 4 位 codecId（7=AVC/H264，12=HEVC/H265）
                int off = 9 + 4; // FLV header(9) + PreviousTagSize0(4)
                while (off + 11 <= data.Length)
                {
                    int type = data[off];
                    int dataSize = (data[off + 1] << 16) | (data[off + 2] << 8) | data[off + 3];
                    if (type == 9)
                    {
                        int codecId = (data[off + 11] >> 4) & 0x0F;
                        if (codecId == 7) return VideoCodec.H264;
                        if (codecId == 12) return VideoCodec.H265;
                        return VideoCodec.Unknown;
                    }
                    if (dataSize < 0) break;
                    off += 11 + dataSize + 4; // tag header + data + PreviousTagSize
                }
                return VideoCodec.Unknown;
            default:
                return VideoCodec.Unknown;
        }
    }

    /// <summary>ASCII 子串搜索（零分配）。needle 为 ASCII 常量。</summary>
    private static bool ContainsAscii(ReadOnlySpan<byte> haystack, string needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != (byte)needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
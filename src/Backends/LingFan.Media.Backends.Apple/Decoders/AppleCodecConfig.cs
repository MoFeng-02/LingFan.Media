using System.Runtime.Versioning;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Backends.Apple.Decoders;

/// <summary>
/// 解析 Apple 标准私有配置（avcC / hvcC），提取 NAL 参数集与 NAL 长度，供
/// <see cref="AppleVideoDecoder"/> 经 <c>CMVideoFormatDescriptionCreateFromH264/HEVCParameterSets</c> 构建格式描述。
/// </summary>
/// <remarks>
/// <para>avcC（ISO/IEC 14496-15 §5.3.3.1）：configurationVersion(1) + profile/compat/level(3) + (reserved 6 + lengthSizeMinusOne 2) + (reserved 3 + numSPS 5) + [SPS] + numPPS + [PPS]。</para>
/// <para>hvcC（ISO/IEC 14496-15 §8.3.3.1）：至 numOfArrays(1) 后跟若干 array，每个 array 含 NAL_unit_type、numNalus 与 NALU（VPS=32/SPS=33/PPS=34）。</para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2050",
    Justification = "无 [ComImport]，使用原始 [LibraryImport] P/Invoke，不会被裁剪器移除。仅 Apple 运行时使用。")]
internal sealed class AvcConfig
{
    /// <summary>NAL 长度字段字节数（1/2/4）。</summary>
    public int NalLengthSize { get; private set; }

    /// <summary>序列参数集（H264 与 HEVC 均有）。</summary>
    public List<byte[]> Sps { get; } = new();

    /// <summary>图像参数集。</summary>
    public List<byte[]> Pps { get; } = new();

    /// <summary>视频参数集（仅 HEVC）。</summary>
    public List<byte[]> Vps { get; } = new();

    private AvcConfig() { }

    /// <summary>从标准 avcC / hvcC 字节解析参数集。</summary>
    /// <param name="extra">avcC（H264）或 hvcC（HEVC）字节。</param>
    /// <param name="isHevc">true=hvcC（HEVC）；false=avcC（H264）。</param>
    /// <returns>解析结果；字节不足或结构非法返回 <see langword="null"/>（诚实失败，调用方据实上报）。</returns>
    public static AvcConfig? Parse(ReadOnlyMemory<byte> extra, bool isHevc)
    {
        var data = extra.Span;
        if (data.Length < (isHevc ? 23 : 7)) return null;

        try
        {
            if (isHevc) return ParseHevc(data);
            return ParseAvc(data);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AvcConfig ParseAvc(ReadOnlySpan<byte> d)
    {
        var cfg = new AvcConfig();
        // byte[4]: reserved(6) + lengthSizeMinusOne(2)
        cfg.NalLengthSize = (d[4] & 0x03) + 1;
        // byte[5]: reserved(3) + numOfSPS(5)
        int numSps = d[5] & 0x1F;
        int off = 6;
        for (int i = 0; i < numSps; i++)
        {
            int len = (d[off] << 8) | d[off + 1];
            off += 2;
            cfg.Sps.Add(d.Slice(off, len).ToArray());
            off += len;
        }
        if (off >= d.Length) return cfg;
        int numPps = d[off] & 0x1F;
        off += 1;
        for (int i = 0; i < numPps; i++)
        {
            int len = (d[off] << 8) | d[off + 1];
            off += 2;
            cfg.Pps.Add(d.Slice(off, len).ToArray());
            off += len;
        }
        return cfg;
    }

    private static AvcConfig ParseHevc(ReadOnlySpan<byte> d)
    {
        var cfg = new AvcConfig();
        // byte[21]: constantFrameRate(2) + numTemporalLayers(3) + temporalIdNested(1) + lengthSizeMinusOne(2)
        cfg.NalLengthSize = (d[21] & 0x03) + 1;
        int numArrays = d[22];
        int off = 23;
        for (int a = 0; a < numArrays && off < d.Length; a++)
        {
            if (off + 3 > d.Length) break;
            byte nalType = (byte)(d[off] & 0x3F);
            int numNalus = (d[off + 1] << 8) | d[off + 2];
            off += 3;
            for (int n = 0; n < numNalus && off + 2 <= d.Length; n++)
            {
                int len = (d[off] << 8) | d[off + 1];
                off += 2;
                if (off + len > d.Length) break;
                var nal = d.Slice(off, len).ToArray();
                off += len;
                switch (nalType)
                {
                    case 32: cfg.Vps.Add(nal); break;
                    case 33: cfg.Sps.Add(nal); break;
                    case 34: cfg.Pps.Add(nal); break;
                }
            }
        }
        return cfg;
    }
}

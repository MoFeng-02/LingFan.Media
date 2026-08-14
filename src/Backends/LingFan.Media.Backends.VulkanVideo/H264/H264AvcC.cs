using System;
using System.Collections.Generic;

namespace LingFan.Media.Backends.VulkanVideo.H264;

/// <summary>
/// avcC / Annex-B 参数集提取：从 <see cref="VideoSettings.CodecConfiguration"/> 抽取 SPS/PPS 原始字节，
/// 并剥离 emulation prevention（00 00 03 → 00 00）得到 RBSP，供 SPS/PPS 解析器消费。
/// </summary>
internal static class H264AvcC
{
    /// <summary>
    /// 从 avcC 或 Annex-B 配置抽取 SPS/PPS（已转 RBSP），并给出 Annex-B 包的长度前缀字节数。
    /// </summary>
    /// <param name="config">CodecConfiguration（avcC 首字节 0x01；或 Annex-B 起始码流）。</param>
    /// <param name="spsRbsp">输出 SPS RBSP（剥离起始码与 emulation prevention）。</param>
    /// <param name="ppsRbsp">输出 PPS RBSP。</param>
    /// <param name="nalLengthSize">AVCC 长度前缀字节数（Annex-B 配置为 0，表示包已是 Annex-B）。</param>
    /// <returns>是否成功抽取到 SPS+PPS。</returns>
    public static bool TryParse(ReadOnlyMemory<byte> config, out byte[] spsRbsp, out byte[] ppsRbsp, out int nalLengthSize)
    {
        spsRbsp = Array.Empty<byte>();
        ppsRbsp = Array.Empty<byte>();
        nalLengthSize = 0;

        var s = config.Span;
        if (s.Length < 4) return false;

        if (s[0] == 1)
        {
            // avcC：nal_length_size = (config[4] & 0x03) + 1
            nalLengthSize = (s[4] & 0x03) + 1;
            int pos = 5;
            if (pos >= s.Length) return false;
            int numSps = s[pos++] & 0x1F;
            for (int i = 0; i < numSps && pos + 2 <= s.Length; i++)
            {
                int len = (s[pos] << 8) | s[pos + 1];
                pos += 2;
                if (pos + len > s.Length) return false;
                // 去掉 1 字节 NAL 头（forbidden_zero+nal_ref_idc+nal_unit_type），
                // SPS/PPS RBSP 自 profile_idc 起算，NAL 头非语法一部分。
                spsRbsp = ToRbsp(s.Slice(pos + 1, len - 1));
                pos += len;
            }

            if (pos >= s.Length) return false;
            int numPps = s[pos++];
            for (int i = 0; i < numPps && pos + 2 <= s.Length; i++)
            {
                int len = (s[pos] << 8) | s[pos + 1];
                pos += 2;
                if (pos + len > s.Length) return false;
                ppsRbsp = ToRbsp(s.Slice(pos + 1, len - 1));
                pos += len;
            }

            return spsRbsp.Length > 0 && ppsRbsp.Length > 0;
        }

        // Annex-B：扫描起始码，提取 SPS(7)/PPS(8) NAL
        return TryParseAnnexB(s, out spsRbsp, out ppsRbsp);
    }

    private static bool TryParseAnnexB(ReadOnlySpan<byte> s, out byte[] sps, out byte[] pps)
    {
        sps = Array.Empty<byte>();
        pps = Array.Empty<byte>();
        int i = 0;
        while (i + 3 < s.Length)
        {
            int start = FindStartCode(s, i);
            if (start < 0) break;
            int nalStart = start + (s[start + 2] == 1 ? 3 : 4); // 00 00 01 或 00 00 00 01
            int next = FindStartCode(s, nalStart);
            int end = next < 0 ? s.Length : next;
            int nalType = s[nalStart] & 0x1F;
            var nal = s.Slice(nalStart, end - nalStart);
            // 去掉 1 字节 NAL 头，SPS/PPS RBSP 自 profile_idc 起算。
            if (nalType == 7) sps = ToRbsp(nal.Slice(1));
            else if (nalType == 8) pps = ToRbsp(nal.Slice(1));
            if (next < 0) break;
            i = next + 1;
        }

        return sps.Length > 0 && pps.Length > 0;
    }

    private static int FindStartCode(ReadOnlySpan<byte> s, int from)
    {
        for (int i = from; i + 3 < s.Length; i++)
        {
            if (s[i] == 0 && s[i + 1] == 0 && (s[i + 2] == 1 || (i + 4 < s.Length && s[i + 2] == 0 && s[i + 3] == 1)))
                return i;
        }

        return -1;
    }

    /// <summary>剥离 emulation prevention three byte（00 00 03 → 00 00）。</summary>
    private static byte[] ToRbsp(ReadOnlySpan<byte> nal)
    {
        var outBuf = new List<byte>(nal.Length);
        for (int i = 0; i < nal.Length; i++)
        {
            if (i + 2 < nal.Length && nal[i] == 0 && nal[i + 1] == 0 && nal[i + 2] == 3)
            {
                outBuf.Add(0);
                outBuf.Add(0);
                i += 2; // 跳过 03
                continue;
            }

            outBuf.Add(nal[i]);
        }

        return outBuf.ToArray();
    }
}

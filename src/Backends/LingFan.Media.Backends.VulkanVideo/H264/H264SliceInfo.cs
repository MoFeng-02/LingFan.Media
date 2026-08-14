using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Video;

namespace LingFan.Media.Backends.VulkanVideo.H264;

/// <summary>
/// H.264 切片（slice）解析：从 packet 抽取 slice NAL 拼为解码比特流、计算切片偏移，
/// 并解析首个 slice 头填充 <see cref="StdVideoDecodeH264PictureInfo"/> / <see cref="StdVideoDecodeH264ReferenceInfo"/>。
/// </summary>
/// <remarks>
/// <para>Vulkan H.264 解码契约：<c>vkCmdDecodeVideoKHR</c> 的 <see cref="VideoDecodeInfoKHR.SrcBuffer"/> 承载
/// 「去起始码」的 slice NAL 序列（每个 slice 自 NAL 头字节起，含 emulation prevention 字节由驱动剥离），
/// <see cref="VideoDecodeInfoKHR.PSliceOffsets"/> 指向各 slice 在缓冲内的字节偏移。</para>
/// <para>本类同时支持 avcC 长度前缀包（<paramref name="nalLengthSize"/> &gt; 0）与 Annex-B 起始码包（= 0）。</para>
/// </remarks>
internal static unsafe class H264SliceInfo
{
    // slice NAL 类型：1=非IDR, 5=IDR, 19=slice in scalable ext, 20=slice ext
    private static bool IsSliceNal(byte nalType) => nalType == 1 || nalType == 5 || nalType == 19 || nalType == 20;

    /// <summary>
    /// 把 packet 中的 slice NAL 拼为解码比特流（去长度/起始前缀，保留 NAL 头字节），返回切片偏移。
    /// </summary>
    public static byte[] BuildBitstream(ReadOnlySpan<byte> packet, int nalLengthSize, out int[] sliceOffsets)
    {
        var outBuf = new List<byte>(packet.Length);
        var offsets = new List<int>();

        if (nalLengthSize > 0)
        {
            int p = 0;
            while (p + nalLengthSize <= packet.Length)
            {
                int len = 0;
                for (int i = 0; i < nalLengthSize; i++)
                    len = (len << 8) | packet[p + i];
                p += nalLengthSize;
                if (p + len > packet.Length) break;
                var nal = packet.Slice(p, len);
                byte nalType = (byte)(nal[0] & 0x1F);
                if (IsSliceNal(nalType))
                {
                    offsets.Add(outBuf.Count);
                    for (int i = 0; i < nal.Length; i++) outBuf.Add(nal[i]);
                }
                p += len;
            }
        }
        else
        {
            // Annex-B：扫描起始码
            int n = packet.Length;
            int i = 0;
            while (i + 3 < n)
            {
                int sc = FindStartCode(packet, i);
                if (sc < 0) break;
                int hdrLen = (packet[sc + 2] == 1) ? 3 : 4;
                int nalStart = sc + hdrLen;
                int next = FindStartCode(packet, nalStart);
                int end = next < 0 ? n : next;
                if (end > nalStart)
                {
                    byte nalType = (byte)(packet[nalStart] & 0x1F);
                    if (IsSliceNal(nalType))
                    {
                        offsets.Add(outBuf.Count);
                        for (int k = nalStart; k < end; k++) outBuf.Add(packet[k]);
                    }
                }
                if (next < 0) break;
                i = next + 1;
            }
        }

        sliceOffsets = offsets.ToArray();
        return outBuf.ToArray();
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

    /// <summary>
    /// 解析首个 slice 的 RBSP（已去 NAL 头与 emulation prevention），结合 SPS 上下文填充图片信息与参考信息。
    /// </summary>
    public static void ReadPictureInfo(
        ReadOnlySpan<byte> firstSliceRbsp, byte nalRefIdc, byte nalUnitType, H264ParameterSet sps,
        out StdVideoDecodeH264PictureInfo picInfo, out StdVideoDecodeH264ReferenceInfo refInfo)
    {
        picInfo = new StdVideoDecodeH264PictureInfo();
        refInfo = new StdVideoDecodeH264ReferenceInfo();

        var r = new H264BitReader(firstSliceRbsp);
        _ = r.ReadUe(); // first_mb_in_slice
        int sliceType = r.ReadUe();
        _ = r.ReadUe(); // pic_parameter_set_id
        if (sps.SeparateColourPlaneFlag == 1) r.ReadBits(2); // colour_plane_id

        int frameNumBits = sps.Log2MaxFrameNumMinus4 + 4;
        int frameNum = (int)r.ReadBits(frameNumBits);
        int fieldPicFlag = 0;
        if (sps.FrameMbsOnlyFlag == 0) fieldPicFlag = r.ReadBit();
        int bottomFieldFlag = 0;
        if (fieldPicFlag == 1) bottomFieldFlag = r.ReadBit();
        int idrPicId = 0;
        if (nalUnitType == 5) idrPicId = r.ReadUe();

        int picOrderCntLsb = 0;
        int deltaPicOrderCnt0 = 0;
        int deltaPicOrderCnt1 = 0;
        if (sps.PicOrderCntType == 0)
            picOrderCntLsb = (int)r.ReadBits(sps.Log2MaxPicOrderCntLsbMinus4 + 4);
        else if (sps.PicOrderCntType == 1 && fieldPicFlag == 0)
        {
            deltaPicOrderCnt0 = r.ReadSe();
            deltaPicOrderCnt1 = r.ReadSe();
        }

        bool isIntra = sliceType == 2 || sliceType == 4 || sliceType == 7 || sliceType == 9;
        bool isRef = nalRefIdc != 0;

        picInfo.Flags.IdrPicFlag = nalUnitType == 5 ? 1u : 0u;
        picInfo.Flags.IsIntra = isIntra ? 1u : 0u;
        picInfo.Flags.FieldPicFlag = (uint)fieldPicFlag;
        picInfo.Flags.BottomFieldFlag = (uint)bottomFieldFlag;
        picInfo.Flags.IsReference = isRef ? 1u : 0u;
        picInfo.Flags.ComplementaryFieldPair = 0;
        picInfo.SeqParameterSetId = sps.Sps.SeqParameterSetId;
        picInfo.PicParameterSetId = sps.Pps.PicParameterSetId;
        picInfo.FrameNum = (ushort)frameNum;
        picInfo.IdrPicId = (ushort)idrPicId;
        picInfo.PicOrderCnt[0] = sps.PicOrderCntType == 0 ? picOrderCntLsb : deltaPicOrderCnt0;
        picInfo.PicOrderCnt[1] = sps.PicOrderCntType == 1 ? deltaPicOrderCnt1 : 0;

        refInfo.Flags.TopFieldFlag = fieldPicFlag == 0 ? 1u : 0u;
        refInfo.Flags.BottomFieldFlag = (uint)bottomFieldFlag;
        refInfo.Flags.UsedForLongTermReference = 0;
        refInfo.Flags.IsNonExisting = 0;
        refInfo.FrameNum = (ushort)frameNum;
        refInfo.PicOrderCnt[0] = picInfo.PicOrderCnt[0];
        refInfo.PicOrderCnt[1] = picInfo.PicOrderCnt[1];
    }

    /// <summary>剥离 emulation prevention three byte（00 00 03 → 00 00）。</summary>
    public static byte[] ToRbsp(ReadOnlySpan<byte> nal)
    {
        var outBuf = new List<byte>(nal.Length);
        for (int i = 0; i < nal.Length; i++)
        {
            if (i + 2 < nal.Length && nal[i] == 0 && nal[i + 1] == 0 && nal[i + 2] == 3)
            {
                outBuf.Add(0);
                outBuf.Add(0);
                i += 2;
                continue;
            }
            outBuf.Add(nal[i]);
        }
        return outBuf.ToArray();
    }
}

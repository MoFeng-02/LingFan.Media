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
/// <para>Vulkan H.264 解码契约（权威规范 VK_KHR_video_decode_h264 §42.11.1 + VkVideoDecodeH264PictureInfoKHR）：
/// <c>vkCmdDecodeVideoKHR</c> 的 <see cref="VideoDecodeInfoKHR.SrcBuffer"/> 承载「<b>不含</b> NAL 起始码（00 00 01）前缀」
/// 的 slice NAL 序列——每个 slice 的 NAL 头字节（如 IDR=0x65、非 IDR=0x41）须直接位于缓冲起点，
/// <see cref="VideoDecodeInfoKHR.PSliceOffsets"/> 指向各 slice 的「slice header 起点」（即 NAL 头字节）。
/// 起始码是 Annex-B 帧封装细节、非 NAL 单元的一部分；若保留起始码，解码器会把起始码首字节误读为 NAL 头
/// （type=0 未定义 NAL）→ 静默丢弃全部切片 → DPB 全零 NV12 → 恒绿（绿屏根因）。</para>
/// <para>本方法去除 avcC 长度前缀 / Annex-B 起始码后，直接拼接 NAL 单元（NAL 头 + RBSP，emulation prevention 字节保留——
/// 硬件解码器自行处理），并为每个 slice 记录「指向 NAL 头」的对齐偏移。</para>
/// <para>本类同时支持 avcC 长度前缀包（<paramref name="nalLengthSize"/> &gt; 0）与 Annex-B 起始码包（= 0）。</para>
/// </remarks>
internal static unsafe class H264SliceInfo
{
    // slice NAL 类型：1=非IDR, 5=IDR, 19=slice in scalable ext, 20=slice ext
    private static bool IsSliceNal(byte nalType) => nalType == 1 || nalType == 5 || nalType == 19 || nalType == 20;

    /// <summary>
    /// 把 packet 中的 slice NAL 拼为解码比特流（去除长度前缀/Annex-B 起始码，再为每个 slice 重新加回
    /// 00 00 01 NAL 起始码），返回指向各 slice 起始码的切片偏移（已按 <paramref name="bitstreamOffsetAlign"/> 对齐）。
    /// </summary>
    /// <param name="bitstreamOffsetAlign">VkVideoCapabilitiesKHR::minBitstreamBufferOffsetAlignment（切片偏移对齐）。</param>
    public static byte[] BuildBitstream(ReadOnlySpan<byte> packet, int nalLengthSize, out int[] sliceOffsets, ulong bitstreamOffsetAlign = 1)
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
                    AppendNal(outBuf, offsets, nal, bitstreamOffsetAlign);
                p += len;
            }
        }
        else
        {
            // Annex-B：扫描起始码，提取 NAL（不含起始码）后统一加回 00 00 01 起始码。
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
                        AppendNal(outBuf, offsets, packet.Slice(nalStart, end - nalStart), bitstreamOffsetAlign);
                }
                if (next < 0) break;
                i = next + 1;
            }
        }

        sliceOffsets = offsets.ToArray();
        return outBuf.ToArray();
    }

    /// <summary>
    /// 向解码比特流追加一个 slice NAL：起始码前导 0 填充至 <paramref name="bitstreamOffsetAlign"/> 边界，
    /// 再直接写 NAL 原始字节（NAL 头 + RBSP，emulation prevention 字节保留）。
    /// <paramref name="offsets"/> 记录 NAL 头位置——Vulkan H.264 解码要求 <c>pSliceOffsets</c> 指向 NAL 头
    /// （slice header 起点），绝不可带起始码前缀，否则解码器静默丢弃（无错误、输出全零 → 绿屏）。
    /// </summary>
    private static void AppendNal(List<byte> outBuf, List<int> offsets, ReadOnlySpan<byte> nal, ulong bitstreamOffsetAlign)
    {
        ulong align = bitstreamOffsetAlign < 1 ? 1 : bitstreamOffsetAlign;
        while ((ulong)outBuf.Count % align != 0) outBuf.Add(0); // 前导 0 填充（解码器不读取前导字节）
        offsets.Add(outBuf.Count);                        // 指向 NAL 头（slice header 起点，无起始码）
        for (int i = 0; i < nal.Length; i++) outBuf.Add(nal[i]);
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

        // 规范表（VK_KHR_video_decode_h264）：StdVideoDecodeH264ReferenceInfo 的场标志按此判定——
        // 渐进帧(field_pic_flag=0)=帧 → top/bottom 均为 0；隔行帧按 bottom_field_flag 设顶场(1,0)/底场(0,1)。
        // 旧 NVIDIA 驱动 bug 曾要求渐进帧 top=1，现已废弃；按现规范置 0 才符合"frame"语义，亦避免 validation 层抱怨。
        if (fieldPicFlag == 0)
        {
            refInfo.Flags.TopFieldFlag = 0;
            refInfo.Flags.BottomFieldFlag = 0;
        }
        else
        {
            refInfo.Flags.TopFieldFlag = bottomFieldFlag == 0 ? 1u : 0u;
            refInfo.Flags.BottomFieldFlag = (uint)bottomFieldFlag;
        }
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

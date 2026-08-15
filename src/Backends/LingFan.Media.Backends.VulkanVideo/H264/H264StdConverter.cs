using System;
using System.Runtime.InteropServices;
using LingFan.Media.GPUShare.Vulkan;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Video;

namespace LingFan.Media.Backends.VulkanVideo.H264;

/// <summary>
/// H.264 SPS/PPS → Vulkan STD 参数集转换器（VK_KHR_video_decode_h264 契约）。
/// </summary>
/// <remarks>
/// <para>把从 <see cref="H264AvcC"/> 抽出的 SPS/PPS RBSP（已剥离 NAL 头与 emulation prevention）解析为
/// <see cref="StdVideoH264SequenceParameterSet"/> / <see cref="StdVideoH264PictureParameterSet"/>，
/// 并持有其引用的原生子结构（scaling lists、offset_for_ref_frame），供
/// <c>vkCreateVideoSessionParametersKHR</c> 使用。</para>
/// <para><b>所有权</b>：本类实现 <see cref="IDisposable"/>，<see cref="Dispose"/> 释放所有经
/// <see cref="Marshal.AllocHGlobal"/> 分配的原生子结构缓冲；SPS/PPS 主结构本身为托管 blittable 值类型，
/// 由其字段内的指针指向本类拥有的原生内存（指针在 VideoSessionParameters 生命周期内持续有效）。</para>
/// <para><b>AOT 兼容</b>：纯非安全值类型操作 + 原生内存分配，零反射。</para>
/// </remarks>
internal sealed unsafe class H264ParameterSet : IDisposable
{
    // SPS/PPS 主结构（值类型，含指向本类原生子结构的指针）
    public StdVideoH264SequenceParameterSet Sps;
    public StdVideoH264PictureParameterSet Pps;

    // 供切片解析器使用的 SPS 上下文（从 RBSP 解析的同时缓存）
    public byte Log2MaxFrameNumMinus4;
    public byte PicOrderCntType;
    public byte Log2MaxPicOrderCntLsbMinus4;
    public byte FrameMbsOnlyFlag;
    public byte SeparateColourPlaneFlag;
    public byte ChromaFormatIdc;
    public byte MaxNumRefFrames;
    public uint PicWidthInMbsMinus1;
    public uint PicHeightInMapUnitsMinus1;

    // 原生子结构缓冲（本类拥有）
    private IntPtr _scalingLists;       // StdVideoH264ScalingLists*
    private IntPtr _ppsScalingLists;    // 仅当 PPS 自带 scaling matrix 时分配
    private IntPtr _offsetForRefFrame;  // int[]

    private bool _disposed;
    private static bool _diagStdEmitted;

    /// <summary>
    /// 解析 SPS/PPS RBSP（已去 NAL 头），构造并填充 STD 参数集。
    /// </summary>
    /// <param name="spsRbsp">SPS RBSP（自 profile_idc 起）。</param>
    /// <param name="ppsRbsp">PPS RBSP（自 pic_parameter_set_id 起）。</param>
    /// <exception cref="NotSupportedException">SPS/PPS 语法非法或本实现不支持（如 FMO / MVC）。</exception>
    public static H264ParameterSet Parse(ReadOnlySpan<byte> spsRbsp, ReadOnlySpan<byte> ppsRbsp)
    {
        var ps = new H264ParameterSet();
        ps.ParseSps(spsRbsp);
        ps.ParsePps(ppsRbsp);
        return ps;
    }

    // ── SPS 解析 ──

    private void ParseSps(ReadOnlySpan<byte> rbsp)
    {
        var r = new H264BitReader(rbsp);
        if (rbsp.Length < 4) throw new NotSupportedException("SPS RBSP 过短");

        byte profileIdc = (byte)r.ReadBits(8);
        byte constraintByte = (byte)r.ReadBits(8); // constraint_set0..5 (bit7..bit2) + 2 reserved
        byte levelIdc = (byte)r.ReadBits(8);
        uint spsId = (uint)r.ReadUe();

        Sps.ProfileIdc = (StdVideoH264ProfileIdc)profileIdc;
        Sps.LevelIdc = MapLevel(levelIdc);
        Sps.Flags.ConstraintSet0Flag = (uint)((constraintByte >> 7) & 1);
        Sps.Flags.ConstraintSet1Flag = (uint)((constraintByte >> 6) & 1);
        Sps.Flags.ConstraintSet2Flag = (uint)((constraintByte >> 5) & 1);
        Sps.Flags.ConstraintSet3Flag = (uint)((constraintByte >> 4) & 1);
        Sps.Flags.ConstraintSet4Flag = (uint)((constraintByte >> 3) & 1);
        Sps.Flags.ConstraintSet5Flag = (uint)((constraintByte >> 2) & 1);
        Sps.SeqParameterSetId = (byte)spsId;

        // ⚠️ 规范铁律（H.264 Annex A SPS RBSP 语法）：
        // chroma_format_idc / bit_depth_luma_minus8 / bit_depth_chroma_minus8 /
        // qpprime_y_zero_transform_bypass_flag / seq_scaling_matrix_present_flag 仅当 profile 属
        // 「high」档族（上列 profile_idc）时才出现在比特流；Baseline(66)/Main(77)/Extended(88) 等档位
        // 无这些字段，chroma 隐式 4:2:0（=1）。
        // 此前这些字段被无条件 ReadUe、且漏读 qpprime/seq_scaling 两比特 → 非 high 档整体偏移 2 个 ue(v)、
        // high 档偏移 2 比特 → SPS 全乱 → 解码器无法解切片 → 静默吐全零 DPB（绿屏真因）。
        bool highProfile = profileIdc == 100 || profileIdc == 110 || profileIdc == 122 || profileIdc == 244
                          || profileIdc == 44 || profileIdc == 83 || profileIdc == 86 || profileIdc == 118
                          || profileIdc == 128 || profileIdc == 138 || profileIdc == 139 || profileIdc == 134 || profileIdc == 135;

        byte chromaFmt;
        byte separateColourPlaneFlag = 0;
        if (highProfile)
        {
            chromaFmt = (byte)r.ReadUe();
            if (chromaFmt == 3)
            {
                separateColourPlaneFlag = (byte)r.ReadBit();
                Sps.Flags.SeparateColourPlaneFlag = separateColourPlaneFlag;
            }
            Sps.BitDepthLumaMinus8 = (byte)r.ReadUe();
            Sps.BitDepthChromaMinus8 = (byte)r.ReadUe();
            Sps.Flags.QpprimeYZeroTransformBypassFlag = (byte)r.ReadBit();
            byte seqScalingMatrixPresentFlag = (byte)r.ReadBit();
            Sps.Flags.SeqScalingMatrixPresentFlag = seqScalingMatrixPresentFlag;
            if (seqScalingMatrixPresentFlag == 1)
            {
                int chromaArrayType = (separateColourPlaneFlag == 1) ? 0 : chromaFmt;
                ParseScalingLists(ref r, chromaArrayType, out _scalingLists, ref Sps.PScalingLists);
            }
        }
        else
        {
            chromaFmt = 1; // 隐式 4:2:0，无 chroma/bit_depth/scaling 字段
        }
        ChromaFormatIdc = chromaFmt;
        Sps.ChromaFormatIdc = (StdVideoH264ChromaFormatIdc)chromaFmt;

        byte log2MaxFrameNumMinus4 = (byte)r.ReadUe();
        byte picOrderCntType = (byte)r.ReadUe();
        Log2MaxFrameNumMinus4 = log2MaxFrameNumMinus4;
        PicOrderCntType = picOrderCntType;
        Sps.Log2MaxFrameNumMinus4 = log2MaxFrameNumMinus4;
        Sps.PicOrderCntType = (StdVideoH264PocType)picOrderCntType;

        int[]? offsetForRefFrame = null;
        if (picOrderCntType == 0)
        {
            Sps.Log2MaxPicOrderCntLsbMinus4 = (byte)r.ReadUe();
            Log2MaxPicOrderCntLsbMinus4 = Sps.Log2MaxPicOrderCntLsbMinus4;
        }
        else if (picOrderCntType == 1)
        {
            Sps.Flags.DeltaPicOrderAlwaysZeroFlag = (byte)r.ReadBit();
            Sps.OffsetForNonRefPic = r.ReadSe();
            Sps.OffsetForTopToBottomField = r.ReadSe();
            byte numRefFramesInPicOrderCntCycle = (byte)r.ReadUe();
            Sps.NumRefFramesInPicOrderCntCycle = numRefFramesInPicOrderCntCycle;
            if (numRefFramesInPicOrderCntCycle > 0)
            {
                offsetForRefFrame = new int[numRefFramesInPicOrderCntCycle];
                for (int i = 0; i < numRefFramesInPicOrderCntCycle; i++)
                    offsetForRefFrame[i] = r.ReadSe();
            }
        }
        else if (picOrderCntType != 2)
        {
            throw new NotSupportedException($"不支持的 pic_order_cnt_type={picOrderCntType}");
        }

        byte maxNumRefFrames = (byte)r.ReadUe();
        MaxNumRefFrames = maxNumRefFrames;
        Sps.MaxNumRefFrames = maxNumRefFrames;
        Sps.Flags.GapsInFrameNumValueAllowedFlag = (byte)r.ReadBit();

        uint picWidthInMbsMinus1 = (uint)r.ReadUe();
        uint picHeightInMapUnitsMinus1 = (uint)r.ReadUe();
        PicWidthInMbsMinus1 = picWidthInMbsMinus1;
        PicHeightInMapUnitsMinus1 = picHeightInMapUnitsMinus1;
        Sps.PicWidthInMbsMinus1 = picWidthInMbsMinus1;
        Sps.PicHeightInMapUnitsMinus1 = picHeightInMapUnitsMinus1;

        byte frameMbsOnlyFlag = (byte)r.ReadBit();
        FrameMbsOnlyFlag = frameMbsOnlyFlag;
        Sps.Flags.FrameMbsOnlyFlag = frameMbsOnlyFlag;
        if (frameMbsOnlyFlag == 0)
            Sps.Flags.MbAdaptiveFrameFieldFlag = (byte)r.ReadBit();
        Sps.Flags.Direct8x8InferenceFlag = (byte)r.ReadBit();

        byte frameCroppingFlag = (byte)r.ReadBit();
        Sps.Flags.FrameCroppingFlag = frameCroppingFlag;
        if (frameCroppingFlag == 1)
        {
            Sps.FrameCropLeftOffset = (uint)r.ReadUe();
            Sps.FrameCropRightOffset = (uint)r.ReadUe();
            Sps.FrameCropTopOffset = (uint)r.ReadUe();
            Sps.FrameCropBottomOffset = (uint)r.ReadUe();
        }

        // 可选 VUI：本实现不解析 VUI 时序（呈现时间戳由管线按 packet 透传），
        // 故 VuiParametersPresentFlag 置 0 且 PSequenceParameterSetVui 留空，与驱动契约自洽。
        Sps.Flags.VuiParametersPresentFlag = 0;
        Sps.PSequenceParameterSetVui = null;
        _ = r.ReadBit(); // vui_parameters_present_flag（丢弃）

        // 注：seq scaling lists 已在上方 high 档分支按 Annex A 语法（bit_depth 之后、qpprime/seq_scaling_matrix_present_flag 之后）
        // 正确解析；非 high 档 SPS 无 scaling lists 字段（PScalingLists 默认 null 正确）。此处不再处理，避免错位重读。

        // offset_for_ref_frame 原生缓冲
        if (offsetForRefFrame is not null)
        {
            int n = offsetForRefFrame.Length;
            _offsetForRefFrame = Marshal.AllocHGlobal(n * sizeof(int));
            var p = (int*)_offsetForRefFrame;
            for (int i = 0; i < n; i++) p[i] = offsetForRefFrame[i];
            Sps.POffsetForRefFrame = p;
        }
        else
        {
            Sps.POffsetForRefFrame = null;
        }

        Sps.Reserved1 = 0;
        Sps.Reserved2 = 0;

        // [DIAG] 打印真正喂给 Vulkan 的 SPS std 字段值（一次性、只读），核对尺寸/参考帧/档位等是否合规。
        if (!_diagStdEmitted)
        {
            _diagStdEmitted = true;
            Console.WriteLine($"[DIAG-SPS-STD] chroma={(int)Sps.ChromaFormatIdc} separateColourPlane={Sps.Flags.SeparateColourPlaneFlag} " +
                $"maxNumRef={Sps.MaxNumRefFrames} picW={Sps.PicWidthInMbsMinus1} picH={Sps.PicHeightInMapUnitsMinus1} " +
                $"frameMbsOnly={Sps.Flags.FrameMbsOnlyFlag} bitDepthLuma={Sps.BitDepthLumaMinus8} " +
                $"bitDepthChroma={Sps.BitDepthChromaMinus8} level={(int)Sps.LevelIdc}");
        }
    }

    // ── PPS 解析 ──

    private void ParsePps(ReadOnlySpan<byte> rbsp)
    {
        var r = new H264BitReader(rbsp);
        if (rbsp.Length < 2) throw new NotSupportedException("PPS RBSP 过短");

        uint ppsId = (uint)r.ReadUe();
        uint spsId = (uint)r.ReadUe();
        Pps.PicParameterSetId = (byte)ppsId;
        Pps.SeqParameterSetId = (byte)spsId;

        Pps.Flags.EntropyCodingModeFlag = (byte)r.ReadBit();
        Pps.Flags.BottomFieldPicOrderInFramePresentFlag = (byte)r.ReadBit();

        // num_slice_groups_minus1（FMO 不被支持）
        uint numSliceGroupsMinus1 = (uint)r.ReadUe();
        if (numSliceGroupsMinus1 != 0)
            throw new NotSupportedException("不支持 FMO（num_slice_groups_minus1 != 0）");

        Pps.NumRefIdxL0DefaultActiveMinus1 = (byte)r.ReadUe();
        Pps.NumRefIdxL1DefaultActiveMinus1 = (byte)r.ReadUe();
        Pps.PicInitQpMinus26 = (byte)r.ReadSe();
        Pps.PicInitQsMinus26 = (byte)r.ReadSe();
        Pps.ChromaQpIndexOffset = (byte)r.ReadSe();

        Pps.Flags.DeblockingFilterControlPresentFlag = (byte)r.ReadBit();
        Pps.Flags.ConstrainedIntraPredFlag = (byte)r.ReadBit();
        Pps.Flags.RedundantPicCntPresentFlag = (byte)r.ReadBit();

        // weighted_pred_flag / weighted_bipred_idc
        Pps.Flags.WeightedPredFlag = (byte)r.ReadBit();
        Pps.WeightedBipredIdc = (StdVideoH264WeightedBipredIdc)r.ReadBits(2);

        if (!r.Eof)
        {
            Pps.Flags.Transform8x8ModeFlag = (byte)r.ReadBit();
            byte picScalingMatrixPresentFlag = (byte)r.ReadBit();
            Pps.Flags.PicScalingMatrixPresentFlag = picScalingMatrixPresentFlag;
            if (picScalingMatrixPresentFlag == 1)
            {
                // PPS 自带 scaling matrix：分配独立缓冲，不共享 SPS 的
                ParseScalingLists(ref r, ChromaFormatIdc, out _ppsScalingLists, ref Pps.PScalingLists);
            }
            else
            {
                // 否则复用 SPS 的 scaling lists（指针共享）
                Pps.PScalingLists = Sps.PScalingLists;
            }

            if (!r.Eof)
                Pps.SecondChromaQpIndexOffset = (byte)r.ReadSe();
        }
    }

    // ── scaling lists（SPS 与 PPS 共用） ──

    /// <summary>
    /// 解析 seq/pic scaling lists，写入新建的 <see cref="StdVideoH264ScalingLists"/> 原生缓冲。
    /// </summary>
    private static void ParseScalingLists(
        ref H264BitReader r, int chromaArrayType, out IntPtr nativePtr, ref StdVideoH264ScalingLists* pDst)
    {
        int count = (chromaArrayType != 3) ? 8 : 12;
        nativePtr = Marshal.AllocHGlobal(Marshal.SizeOf<StdVideoH264ScalingLists>());
        var sl = (StdVideoH264ScalingLists*)nativePtr;
        sl->ScalingListPresentMask = 0;
        sl->UseDefaultScalingMatrixMask = 0;

        for (int i = 0; i < count; i++)
        {
            int present = r.ReadBit();
            if (present == 0) continue;
            sl->ScalingListPresentMask |= (ushort)(1 << i);

            int size = (i < 6) ? 16 : 64;
            int lastScale = 8;
            int nextScale = 8;
            bool useDefault = false;
            for (int j = 0; j < size; j++)
            {
                int deltaScale = 0;
                if (nextScale != 0)
                    deltaScale = r.ReadSe();
                nextScale = (lastScale + deltaScale + 256) & 0xFF;
                useDefault = (j == 0 && nextScale == 0);
                int val = useDefault ? lastScale : nextScale;
                if (i < 6)
                    sl->ScalingList4x4[i * 16 + j] = (byte)val;
                else
                    sl->ScalingList8x8[(i - 6) * 64 + j] = (byte)val;
                lastScale = val;
            }

            // 8x8 列表（i>=6）在首元素 useDefault 时置对应 default 位（STD 约定位偏移 = i-6）
            if (i >= 6 && useDefault)
                sl->UseDefaultScalingMatrixMask |= (ushort)(1 << (i - 6));
        }

        pDst = sl;
    }

    // ── 辅助：level_idc 映射 ──

    private static StdVideoH264LevelIdc MapLevel(byte levelIdc)
    {
        int tens = levelIdc / 10;
        int units = levelIdc % 10;
        int v = tens switch
        {
            1 => units,
            2 => 4 + units,
            3 => 7 + units,
            4 => 10 + units,
            5 => 13 + units,
            6 => 16 + units,
            _ => 0
        };
        if (v < 0 || v > 18) v = 0;
        return (StdVideoH264LevelIdc)v;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_scalingLists != IntPtr.Zero) { Marshal.FreeHGlobal(_scalingLists); _scalingLists = IntPtr.Zero; }
        if (_ppsScalingLists != IntPtr.Zero) { Marshal.FreeHGlobal(_ppsScalingLists); _ppsScalingLists = IntPtr.Zero; }
        if (_offsetForRefFrame != IntPtr.Zero) { Marshal.FreeHGlobal(_offsetForRefFrame); _offsetForRefFrame = IntPtr.Zero; }
        Sps.PScalingLists = null;
        Sps.POffsetForRefFrame = null;
        Sps.PSequenceParameterSetVui = null;
        Pps.PScalingLists = null;
    }
}

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.VulkanVideo.H264;
using LingFan.Media.GPUShare.Vulkan;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using Silk.NET.Vulkan.Video;

namespace LingFan.Media.Backends.VulkanVideo.Decoders;

// VulkanVideoDecoder 的解码提交职责段（部分类，按职责拆分，编译期等价）。
// begin/decode/end coding 作用域命令记录与队列提交（DPB 参考槽装配、布局保证）；
// DecodeCore 编排在主文件，DPB 管理在 Dpb 段。
internal sealed unsafe partial class VulkanVideoDecoder
{
    private void SubmitDecode(
        int outputSlot, StdVideoDecodeH264PictureInfo picInfo, StdVideoDecodeH264ReferenceInfo refInfo,
        int[] sliceOffsets, ulong bitstreamLen, int semIdx)
    {
        int beginCount = _references.Count + 1; // active refs + setup 槽
        var beginSlots = stackalloc VideoReferenceSlotInfoKHR[beginCount];
        var beginResources = stackalloc VideoPictureResourceInfoKHR[beginCount];
        var beginDpbInfos = stackalloc VideoDecodeH264DpbSlotInfoKHR[beginCount];

        // setup 槽（置于末，slotIndex=outputSlot 真实索引）供 VkVideoDecodeInfoKHR.pSetupReferenceSlot 使用：
        // 重建帧须经 pNext 链携带当前帧 StdVideoDecodeH264ReferenceInfo。
        FillDpbResource(ref beginResources[beginCount - 1], outputSlot);
        beginDpbInfos[beginCount - 1] = new VideoDecodeH264DpbSlotInfoKHR
        {
            SType = StructureType.VideoDecodeH264DpbSlotInfoKhr,
            PNext = null,
            PStdReferenceInfo = (StdVideoDecodeH264ReferenceInfo*)Unsafe.AsPointer(ref refInfo),
        };
        FillRefSlot(ref beginSlots[beginCount - 1], outputSlot, ref beginResources[beginCount - 1], &beginDpbInfos[beginCount - 1]);
        for (int i = 0; i < _references.Count; i++)
        {
            FillDpbResource(ref beginResources[i], _references[i]);
            beginDpbInfos[i] = new VideoDecodeH264DpbSlotInfoKHR
            {
                SType = StructureType.VideoDecodeH264DpbSlotInfoKhr,
                PNext = null,
                PStdReferenceInfo = (StdVideoDecodeH264ReferenceInfo*)Unsafe.AsPointer(ref _slotRefInfo[_references[i]]),
            };
            FillRefSlot(ref beginSlots[i], _references[i], ref beginResources[i], &beginDpbInfos[i]);
        }

        // 规范要求（VK_KHR_video_queue + 官方 H.264 解码样板）：本版 VkVideoBeginCodingInfoKHR
        // 无 pSetupReferenceSlot 字段，setup 槽须出现在 pReferenceSlots 中，但须以 slotIndex=-1 标记
        // （表示该图本帧作为重建目标、尚未关联 DPB 槽，由解码命令随后关联）。若用真实输出槽索引
        // （+值）则驱动误判该空槽为“已激活参考”→ 首帧（无参考）即触发 VU、驱动静默拒绝写入 DPB
        // → 恒全零 NV12 → (0,135,0) 绿屏（绿屏成因）。
        // 故专为本 begin 作用域构造槽数组：active refs 用真实索引，setup 条目 slotIndex=-1（复用同一图与参考信息）。
        var beginSlotsForBegin = stackalloc VideoReferenceSlotInfoKHR[beginCount];
        for (int i = 0; i < _references.Count; i++)
            beginSlotsForBegin[i] = beginSlots[i];
        VideoReferenceSlotInfoKHR beginSetupSlot = beginSlots[beginCount - 1];
        beginSetupSlot.SlotIndex = -1; // 重建目标标记（非 DPB 槽索引）
        beginSlotsForBegin[_references.Count] = beginSetupSlot;

        // 解码参考槽 = active refs 仅（不含 setup），复用 beginSlots 前段（setup 在 decodeInfo.pSetupReferenceSlot）
        VideoReferenceSlotInfoKHR* pDecodeSlots = _references.Count > 0 ? (VideoReferenceSlotInfoKHR*)Unsafe.AsPointer(ref beginSlots[0]) : null;

        VideoBeginCodingInfoKHR beginInfo;
        beginInfo.SType = StructureType.VideoBeginCodingInfoKhr;
        beginInfo.PNext = null;
        beginInfo.Flags = 0;
        beginInfo.VideoSession = _videoSession;
        beginInfo.VideoSessionParameters = _sessionParams;
        beginInfo.ReferenceSlotCount = (uint)beginCount;
        beginInfo.PReferenceSlots = (VideoReferenceSlotInfoKHR*)Unsafe.AsPointer(ref beginSlotsForBegin[0]);

        VideoCodingControlInfoKHR ctrlInfo;
        ctrlInfo.SType = StructureType.VideoCodingControlInfoKhr;
        ctrlInfo.PNext = null;
        ctrlInfo.Flags = _firstFrame ? VideoCodingControlFlagsKHR.ResetBitKhr : 0;

        // 同步切片偏移数组（按最小对齐填充 range）
        var sliceOff = stackalloc uint[sliceOffsets.Length];
        for (int i = 0; i < sliceOffsets.Length; i++) sliceOff[i] = (uint)sliceOffsets[i];
        ulong alignedRange = AlignUp(bitstreamLen, _minBitstreamSizeAlign);

        VideoDecodeH264PictureInfoKHR h264PicInfo;
        h264PicInfo.SType = StructureType.VideoDecodeH264PictureInfoKhr;
        h264PicInfo.PNext = null;
        h264PicInfo.PStdPictureInfo = &picInfo;
        h264PicInfo.SliceCount = (uint)sliceOffsets.Length;
        h264PicInfo.PSliceOffsets = sliceOffsets.Length > 0 ? (uint*)Unsafe.AsPointer(ref sliceOff[0]) : null;

        VideoDecodeInfoKHR decodeInfo;
        decodeInfo.SType = StructureType.VideoDecodeInfoKhr;
        // 规范要求（VK_KHR_video_decode_h264）：解码命令的 H.264 图片信息
        // （pStdPictureInfo / SliceCount / pSliceOffsets——即真正要解码的切片）必须挂在
        // VkVideoDecodeInfoKHR.PNext 链上。VkVideoDecodeInfoKHR 无对应内嵌字段，PNext=null 时
        // vkCmdDecodeVideoKHR 收不到任何切片偏移与图片参数 → 静默产出全零 DPB → 恒绿（绿屏成因）。
        decodeInfo.PNext = Unsafe.AsPointer(ref h264PicInfo);
        decodeInfo.Flags = 0;
        decodeInfo.SrcBuffer = _bitstreamBuf;
        decodeInfo.SrcBufferOffset = 0;
        decodeInfo.SrcBufferRange = alignedRange;
        decodeInfo.DstPictureResource = beginResources[beginCount - 1];
        decodeInfo.PSetupReferenceSlot = (VideoReferenceSlotInfoKHR*)Unsafe.AsPointer(ref beginSlots[beginCount - 1]);
        decodeInfo.ReferenceSlotCount = (uint)_references.Count;
        decodeInfo.PReferenceSlots = pDecodeSlots;

        // [DIAG] 解码取证：打印前 2 帧提交参数（只读、可逆）。
        if (_diagSubmitEmitted < 2)
        {
            _diagSubmitEmitted++;
            string offs = "";
            for (int i = 0; i < sliceOffsets.Length && i < 8; i++) offs += $"{sliceOffsets[i]} ";
            Console.WriteLine($"[DIAG-SUBMIT#{_diagSubmitEmitted}] outputSlot={outputSlot} sliceCount={sliceOffsets.Length} " +
                              $"srcRange={alignedRange} refs={_references.Count} offsets=[{offs}]");
        }

        VideoEndCodingInfoKHR endInfo;
        endInfo.SType = StructureType.VideoEndCodingInfoKhr;
        endInfo.PNext = null;
        endInfo.Flags = 0;

        // 记录并提交命令
        VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
        var beginCi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        if (VulkanNative.BeginCommandBuffer(_commandBuffer, ref beginCi) != Result.Success)
        {
            _logger.LogError("Vulkan 解码 BeginCommandBuffer 失败");
            return;
        }

        // 解码前布局保证（须在 video coding 作用域之外、CmdBeginVideoCodingKHR 之前）：
        // 输出槽（COINCIDE 模式同时作解码输出与重建）与全部 active 参考槽，
        // 须处于 VK_IMAGE_LAYOUT_VIDEO_DECODE_DPB_KHR（规范 VU，解码器不隐式转换）。
        // 跨队列族 DPB 已 CONCURRENT 共享，屏障无需所有权转移（族索引 ~0u）。
        EnsureSlotDecodeLayout(outputSlot);
        for (int i = 0; i < _references.Count; i++)
            EnsureSlotDecodeLayout(_references[i]);

        VulkanNative.CmdBeginVideoCodingKHR(_commandBuffer, &beginInfo);
        if (_firstFrame)
            VulkanNative.CmdControlVideoCodingKHR(_commandBuffer, &ctrlInfo);
        VulkanNative.CmdDecodeVideoKHR(_commandBuffer, &decodeInfo);
        VulkanNative.CmdEndVideoCodingKHR(_commandBuffer, &endInfo);

        if (VulkanNative.EndCommandBuffer(_commandBuffer) != Result.Success)
        {
            _logger.LogError("Vulkan 解码 EndCommandBuffer 失败");
            return;
        }

        fixed (CommandBuffer* pCb = &_commandBuffer)
        fixed (Fence* pFence = &_fence)
        fixed (Silk.NET.Vulkan.Semaphore* pSem = &_decodeDoneSemaphores[semIdx])
        {
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = pCb,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = pSem,
            };
            VulkanNative.ResetFences(_device, 1, pFence);
            if (VulkanNative.QueueSubmit(_videoQueue, 1, &submit, (nint)_fence.Handle) != Result.Success)
            {
                _logger.LogError("Vulkan 解码 QueueSubmit 失败");
                return;
            }
                VulkanNative.WaitForFences(_device, 1, pFence, 1, ulong.MaxValue);
            }

        _firstFrame = false;
    }
}

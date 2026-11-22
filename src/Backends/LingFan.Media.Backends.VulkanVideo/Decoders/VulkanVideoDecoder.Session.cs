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

// VulkanVideoDecoder 的 Session 职责段（部分类，由 VulkanVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/DecodeCore/SubmitDecode/Dispose）在主文件；Vulkan Video 硬解会话与 DPB 管理。
internal sealed unsafe partial class VulkanVideoDecoder
{
    private void CreateVideoSession(VideoSettings settings)
    {
        // 1) 抽取 SPS/PPS RBSP
        if (!H264AvcC.TryParse(settings.CodecConfiguration, out var spsRbsp, out var ppsRbsp, out _nalLengthSize))
            throw new NotSupportedException("无法从 CodecConfiguration 抽取 H.264 SPS/PPS（avcC/Annex-B）");
        _paramSet = H264ParameterSet.Parse(spsRbsp, ppsRbsp);

        // 2) 视频 Profile（H.264 / 4:2:0 / 8bit）
        VideoDecodeH264ProfileInfoKHR h264Profile;
        h264Profile.SType = StructureType.VideoDecodeH264ProfileInfoKhr;
        h264Profile.PNext = null;
        h264Profile.StdProfileIdc = _paramSet.Sps.ProfileIdc;
        h264Profile.PictureLayout = (VideoDecodeH264PictureLayoutFlagsKHR)0; // Progressive = 0

        VideoProfileInfoKHR profile;
        profile.SType = StructureType.VideoProfileInfoKhr;
        profile.PNext = &h264Profile;
        profile.VideoCodecOperation = VideoCodecOperationFlagsKHR.DecodeH264BitKhr;
        profile.ChromaSubsampling = VideoChromaSubsamplingFlagsKHR.Subsampling420BitKhr;
        profile.LumaBitDepth = VideoComponentBitDepthFlagsKHR.Depth8BitKhr;
        profile.ChromaBitDepth = VideoComponentBitDepthFlagsKHR.Depth8BitKhr;

        // 3) 能力查询
        // 规范 VU 硬约束（khronos/lunarg 官方 VU，不可省略）
        // VUID-vkGetPhysicalDeviceVideoCapabilitiesKHR-pVideoProfile-07184：
        //   当 videoCodecOperation 为 VK_VIDEO_CODEC_OPERATION_DECODE_H264_BIT_KHR 时，
        //   pCapabilities 的 pNext 链【必须】含 VkVideoDecodeH264CapabilitiesKHR。
        // VUID-...-07183：任何 decode 操作时，pNext 链【必须】含 VkVideoDecodeCapabilitiesKHR。
        // 缺失则驱动/验证层返回 VK_ERROR_INITIALIZATION_FAILED（此前崩溃的直接原因）。
        // 结构体在栈帧存活至本次调用返回，取地址安全。
        VideoDecodeH264CapabilitiesKHR h264Caps;
        h264Caps.SType = StructureType.VideoDecodeH264CapabilitiesKhr;
        h264Caps.PNext = null;

        VideoDecodeCapabilitiesKHR decodeCaps;
        decodeCaps.SType = StructureType.VideoDecodeCapabilitiesKhr;
        decodeCaps.PNext = &h264Caps;

        VideoCapabilitiesKHR caps;
        caps.SType = StructureType.VideoCapabilitiesKhr;
        caps.PNext = &decodeCaps;
        var res = VulkanNative.GetPhysicalDeviceVideoCapabilitiesKHR(_physicalDevice, &profile, &caps);
        if (res != Result.Success)
            throw new NotSupportedException($"vkGetPhysicalDeviceVideoCapabilitiesKHR 失败：{res}");

        _maxDpbSlots = (int)Math.Min(caps.MaxDpbSlots, 32);
        if (_maxDpbSlots < 1) _maxDpbSlots = 1;
        _maxActiveRefs = (int)Math.Min(_maxDpbSlots - 1, Math.Min(caps.MaxActiveReferencePictures, _paramSet.MaxNumRefFrames == 0 ? _maxDpbSlots - 1 : _paramSet.MaxNumRefFrames));
        _minBitstreamSizeAlign = caps.MinBitstreamBufferSizeAlignment == 0 ? 1 : caps.MinBitstreamBufferSizeAlignment;
        _minBitstreamOffsetAlign = caps.MinBitstreamBufferOffsetAlignment == 0 ? 1 : caps.MinBitstreamBufferOffsetAlignment;

        _codedW = (int)(_paramSet.PicWidthInMbsMinus1 + 1) * 16;
        _codedH = (int)(_paramSet.PicHeightInMapUnitsMinus1 + 1) * 16 * (_paramSet.FrameMbsOnlyFlag == 1 ? 1 : 2);
        if (_codedW <= 0 || _codedH <= 0)
            throw new NotSupportedException("SPS 给出的图像尺寸非法");

        // [DIAG] 解码取证：打印 SPS/PPS 关键字段与比特流对齐，供绿屏定位（只读、可逆）。
        Console.WriteLine($"[DIAG-SPS] profile_idc={(byte)_paramSet.Sps.ProfileIdc} level_idc={(byte)_paramSet.Sps.LevelIdc} " +
                          $"coded={_codedW}x{_codedH} maxDpb={_maxDpbSlots} maxRef={_maxActiveRefs} " +
                          $"spsId={_paramSet.Sps.SeqParameterSetId} ppsId={_paramSet.Pps.PicParameterSetId} " +
                          $"pocType={_paramSet.PicOrderCntType} log2MaxFrameNum={_paramSet.Log2MaxFrameNumMinus4} " +
                          $"log2MaxPocLsb={_paramSet.Log2MaxPicOrderCntLsbMinus4}");
        Console.WriteLine($"[DIAG-ALIGN] sizeAlign={_minBitstreamSizeAlign} offAlign={_minBitstreamOffsetAlign}");
        Console.WriteLine($"[DIAG-CAPS] decodeCapsFlags=0x{(uint)decodeCaps.Flags:X} (bit0=Coincide,bit1=Distinct)");


        // 4) 创建 Video Session
        VideoSessionCreateInfoKHR sessionCreate;
        sessionCreate.SType = StructureType.VideoSessionCreateInfoKhr;
        sessionCreate.PNext = null;
        sessionCreate.QueueFamilyIndex = _videoQueueFamilyIndex;
        sessionCreate.Flags = default;
        sessionCreate.PVideoProfile = &profile;
        sessionCreate.PictureFormat = Format.G8B8R82Plane420Unorm;
        sessionCreate.MaxCodedExtent = new Extent2D { Width = (uint)_codedW, Height = (uint)_codedH };
        sessionCreate.ReferencePictureFormat = Format.G8B8R82Plane420Unorm;
        sessionCreate.MaxDpbSlots = (uint)_maxDpbSlots;
        sessionCreate.MaxActiveReferencePictures = (uint)_maxActiveRefs;
        sessionCreate.PStdHeaderVersion = &caps.StdHeaderVersion;

        res = VulkanNative.CreateVideoSessionKHR(_device, ref sessionCreate, null, out _videoSession);
        if (res != Result.Success)
            throw new NotSupportedException($"vkCreateVideoSessionKHR 失败：{res}");

        // 5) 绑定 session 内存
        BindVideoSessionMemory();

        // 6) 创建 Session Parameters（SPS/PPS）
        CreateSessionParameters();
    }

    private void BindVideoSessionMemory()
    {
        uint count = 0;
        VulkanNative.GetVideoSessionMemoryRequirementsKHR(_device, _videoSession, ref count, null);
        if (count == 0) return;

        var reqs = stackalloc VideoSessionMemoryRequirementsKHR[(int)count];
        for (int i = 0; i < count; i++)
            reqs[i].SType = StructureType.VideoSessionMemoryRequirementsKhr;
        VulkanNative.GetVideoSessionMemoryRequirementsKHR(_device, _videoSession, ref count, reqs);

        for (int i = 0; i < count; i++)
        {
            var memReq = reqs[i].MemoryRequirements;
            uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
            var alloc = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memReq.Size,
                MemoryTypeIndex = memType,
            };
            if (VulkanNative.AllocateMemory(_device, ref alloc, null, out var mem) != Result.Success)
                throw new NotSupportedException("分配 Video Session 内存失败");

            var bind = new BindVideoSessionMemoryInfoKHR
            {
                SType = StructureType.BindVideoSessionMemoryInfoKhr,
                MemoryBindIndex = reqs[i].MemoryBindIndex,
                Memory = mem,
                MemoryOffset = 0,
                MemorySize = memReq.Size,
            };
            if (VulkanNative.BindVideoSessionMemoryKHR(_device, _videoSession, 1u, &bind) != Result.Success)
                throw new NotSupportedException("绑定 Video Session 内存失败");
        }
    }

    private void CreateSessionParameters()
    {
        // 复制 SPS/PPS 到局部并 pin，供创建调用读取（Vulkan 会拷贝数据入内部存储）
        var localSps = _paramSet!.Sps;
        var localPps = _paramSet.Pps;

        VideoDecodeH264SessionParametersAddInfoKHR addInfo;
        addInfo.SType = StructureType.VideoDecodeH264SessionParametersAddInfoKhr;
        addInfo.PNext = null;
        addInfo.StdSpscount = 1;
        addInfo.StdPpscount = 1;

        VideoDecodeH264SessionParametersCreateInfoKHR h264Params;
        h264Params.SType = StructureType.VideoDecodeH264SessionParametersCreateInfoKhr;
        h264Params.PNext = null;
        // 规范 VU 04782（VK_KHR_video_decode_h264）：本创建信息须为 SPS/PPS 预留容量，
        // maxStdSpsCount / maxStdPpsCount 必须 >= pParametersAddInfo 中实际条数（此处各 1）。
        // 漏设则默认 0 → 驱动静默丢弃 SPS/PPS（CreateVideoSessionParametersKHR 仍返回 Success），
        // 解码器无任何参数集 → 静默产出全零 NV12 DPB → 恒绿（绿屏成因）。
        // 字段名经反射确认的 Silk.NET 2.23.0 拼写为 MaxStdSpscount / MaxStdPpscount（末位 c 小写）。
        h264Params.MaxStdSpscount = 1;
        h264Params.MaxStdPpscount = 1;

        VideoSessionParametersCreateInfoKHR create;
        create.SType = StructureType.VideoSessionParametersCreateInfoKhr;
        create.PNext = null;
        create.Flags = default;
        create.VideoSession = _videoSession;
        create.VideoSessionParametersTemplate = default;

        StdVideoH264SequenceParameterSet* pSps = &localSps;
        StdVideoH264PictureParameterSet* pPps = &localPps;
        addInfo.PStdSpss = pSps;
        addInfo.PStdPpss = pPps;
        h264Params.PParametersAddInfo = &addInfo;
        create.PNext = &h264Params;

        if (VulkanNative.CreateVideoSessionParametersKHR(_device, ref create, null, out _sessionParams) != Result.Success)
            throw new NotSupportedException("vkCreateVideoSessionParametersKHR 失败");
    }

    private void CreateCommandResources()
    {
        VulkanNative.GetDeviceQueue(_device, _videoQueueFamilyIndex, 0, out _videoQueue);

        var poolCi = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _videoQueueFamilyIndex,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit, // VUID-00046/00050：命令缓冲每帧 Reset 须此位
        };
        if (VulkanNative.CreateCommandPool(_device, ref poolCi, null, out _commandPool) != Result.Success)
            throw new NotSupportedException("创建 video-decode 命令池失败");

        var alloc = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        fixed (CommandBuffer* pCb = &_commandBuffer)
        {
            if (VulkanNative.AllocateCommandBuffers(_device, ref alloc, pCb) != Result.Success)
                throw new NotSupportedException("分配 video-decode 命令缓冲失败");
        }

        var fenceCi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        if (VulkanNative.CreateFence(_device, &fenceCi, null, out _fence) != Result.Success)
            throw new NotSupportedException("创建 video-decode 栅栏失败");

        // 跨队列同步信号量：video 队列解码完成后 signal，渲染器 graphics 队列提交前 wait，
        // 建立「解码写入 → 着色器采样」的跨队列执行依赖与内存可见性（CONCURRENT 共享仅解决所有权转移，不提供此依赖）。
        // 跨队列同步信号量环：每 DPB 槽一把。解码 video 队列 signal 该槽信号量；
        // 渲染器 graphics 队列 wait 同一槽信号量（VulkanVideoFrameResource.DecodeDoneSemaphore 携带）。
        // 槽在被消费者释放(DisplayDone)前不重用 → 同槽信号量必已被 wait（unsignaled）→ 再次 signal 合法（VUID-00067）。
        _decodeDoneSemaphores = new Silk.NET.Vulkan.Semaphore[DecodeDoneSemaphoreRingSize];
        SemaphoreCreateInfo semCi = new() { SType = StructureType.SemaphoreCreateInfo };
        for (int i = 0; i < _decodeDoneSemaphores.Length; i++)
        {
            if (VulkanNative.CreateSemaphore(_device, ref semCi, null, out _decodeDoneSemaphores[i]) != Result.Success)
                throw new NotSupportedException("创建 video-decode 完成信号量失败");
        }

        // 诊断路径：GPU→CPU 回读助手（仅为 ReadbackToCpu 真实现铺路；生产零拷贝上屏不消耗）。
        // 关键：回读含 CmdCopyImageToBuffer（TRANSFER 操作），必须跑在支持 TRANSFER 的队列族上。
        // video-decode 队列族通常仅含 VIDEO_DECODE_BIT，提交 transfer 命令会设备丢失/AV，故用 graphics 队列族。
        // 优先取 graphics 族内与渲染器（占 index 0）不同的索引，避免与渲染器 graphics 队列并发提交竞争。
        // 诊断路径：回读含 CmdCopyImageToBuffer（TRANSFER），须跑在支持 TRANSFER 的队列族。
        // video-decode 队列族通常仅含 VIDEO_DECODE_BIT，不可用于回读。
        // 优先用 gpuContext 提供的 graphics 队列族（与渲染器同族，零拷贝闭环已成立）；
        // 若该族无效（MaxValue）或能力缺失，扫描物理设备找一个 TRANSFER 能力族兜底。
        uint rbFamily = _graphicsQueueFamilyIndex != uint.MaxValue
            ? _graphicsQueueFamilyIndex
            : FindTransferQueueFamily(_physicalDevice);
        _readbackQueue = default;
        if (rbFamily != uint.MaxValue)
        {
            // vkGetDeviceQueue 的 index 必须 < vkCreateDevice 时该族实际请求的队列数 M。
            // GetQueueFamilyQueueCount 返回的是物理设备属性报告的队列数 N（通常远大于 M），
            // 据此推算 index 会越界取空句柄（handle=0 且不报错）→ 诊断整条链路失效、绿屏无从取证。
            // 故改为「探测式」取队列：优先取与渲染器（idx 0）不同的索引（若该族 M>1）以避免同 VkQueue 并发提交，
            // 越界（M=1）则回落 idx 0（诊断路径与渲染器同线程串行，无竞争）。
            // vkGetDeviceQueue 的 index 必须 < vkCreateDevice 时该族实际请求的队列数 M（非物理设备报告的 N）。
            // 本机 M=1（族 0 仅 1 队列），故诊断回读直接取 index 0（与渲染器同 VkQueue，串行诊断无竞争）；
            // 取 idx>0 会越界触发 VUID-00385。
            uint qCount = GetQueueFamilyQueueCount(_physicalDevice, rbFamily);
            uint probeIdx = qCount >= 1 ? 0u : 0u; // 恒取 index 0（VUID-00385：越界取空句柄）
            VulkanNative.GetDeviceQueue(_device, rbFamily, probeIdx, out var probe);
            uint gqIdx = probe.Handle != 0 ? probeIdx : 0u;
            VulkanNative.GetDeviceQueue(_device, rbFamily, gqIdx, out _readbackQueue);
        }
        _readbackContext = _readbackQueue.Handle != 0
            ? new VulkanVideoGpuReadbackContext(_device, _physicalDevice, _readbackQueue, rbFamily)
            : null;
        Console.WriteLine($"[HEADFUL-READBACK-DIAG] graphicsFamily={_graphicsQueueFamilyIndex} " +
                          $"videoFamily={_videoQueueFamilyIndex} chosenReadbackFamily={rbFamily} " +
                          $"readbackQueueHandle={_readbackQueue.Handle} " +
                          $"readbackContext={(_readbackContext is null ? "NULL" : "OK")}");
    }
}

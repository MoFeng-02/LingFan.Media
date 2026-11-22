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

// VulkanVideoDecoder 的 Dpb 职责段（部分类，由 VulkanVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/DecodeCore/SubmitDecode/Dispose）在主文件；Vulkan Video 硬解会话与 DPB 管理。
internal sealed unsafe partial class VulkanVideoDecoder
{
    private void CreateDpb()
    {
        _dpb = new DpbSlot[_maxDpbSlots];
        _slotInRef = new bool[_maxDpbSlots];
        _slotEmpty = new bool[_maxDpbSlots];
        _slotDisplayDone = new bool[_maxDpbSlots];
        _slotRefInfo = new StdVideoDecodeH264ReferenceInfo[_maxDpbSlots];
        _slotLayout = new ImageLayout[_maxDpbSlots]; // default=Undefined（尚待解码写入）
        for (int i = 0; i < _maxDpbSlots; i++) _slotEmpty[i] = true;

        // 单一 arrayed DPB 图像：所有 DPB 槽 = 同一图像的不同 array layer。
        // 规范要求（VK_KHR_video_decode_h264 + 本机 profile 不支持 SEPARATE_REFERENCE_IMAGES）：
        // 参考槽必须引用同一图像的不同层（VUID-07244）；每槽独立图像会被验证层拒收 → 解码静默 no-op → 全零 DPB 绿屏。
        bool crossFamily = _videoQueueFamilyIndex != uint.MaxValue
                           && _graphicsQueueFamilyIndex != uint.MaxValue
                           && _videoQueueFamilyIndex != _graphicsQueueFamilyIndex;

        // 解码 Profile 列表：带 VIDEO_DECODE_* usage 的图像/缓冲必须链 VkVideoProfileListInfoKHR
        // 且 pProfiles 含本解码 profile，否则 VUID-04815/04813 + 07135/07142（解码器视资源"不兼容"→ 静默 no-op）。
        VideoDecodeH264ProfileInfoKHR h264Profile;
        h264Profile.SType = StructureType.VideoDecodeH264ProfileInfoKhr;
        h264Profile.PNext = null;
        h264Profile.StdProfileIdc = _paramSet!.Sps.ProfileIdc;
        h264Profile.PictureLayout = (VideoDecodeH264PictureLayoutFlagsKHR)0; // Progressive
        VideoProfileInfoKHR profile;
        profile.SType = StructureType.VideoProfileInfoKhr;
        profile.PNext = &h264Profile;
        profile.VideoCodecOperation = VideoCodecOperationFlagsKHR.DecodeH264BitKhr;
        profile.ChromaSubsampling = VideoChromaSubsamplingFlagsKHR.Subsampling420BitKhr;
        profile.LumaBitDepth = VideoComponentBitDepthFlagsKHR.Depth8BitKhr;
        profile.ChromaBitDepth = VideoComponentBitDepthFlagsKHR.Depth8BitKhr;
        VideoProfileListInfoKHR profileList;
        profileList.SType = StructureType.VideoProfileListInfoKhr;
        profileList.PNext = null;
        profileList.ProfileCount = 1;
        profileList.PProfiles = &profile;

        // DPB 图像 usage/flags：按 Khronos Vulkan-Video-Samples 权威范式
        // 关键约束：视频格式属性查询的「返回值」只能用来确认 format/tiling 支持，
        // 绝不可把返回的 imageUsageFlags/imageCreateFlags 直接回填给 vkCreateImage——
        // 本机驱动对 decode profile 返回 0xFC07（含 VIDEO_ENCODE_* 位），直接采用会要求
        // VK_KHR_video_encode_queue 扩展 → VUID-usage-parameter/04816/06811 → 图像判"profile 无关"
        // → 解码静默 no-op → 全零 DPB 绿屏。故显式构造 decode-only usage + MUTABLE 标志，忽略查询返回值。
        PhysicalDeviceVideoFormatInfoKHR fmtInfo;
        fmtInfo.SType = StructureType.PhysicalDeviceVideoFormatInfoKhr;
        fmtInfo.PNext = &profileList;
        // 查询用 decode-only usage（Coincide 模式须含 DPB_BIT|DST_BIT），仅用于确认格式支持。
        fmtInfo.ImageUsage = ImageUsageFlags.VideoDecodeDpbBitKhr | ImageUsageFlags.VideoDecodeDstBitKhr
                           | ImageUsageFlags.VideoDecodeSrcBitKhr;
        uint fmtCount = 0;
        Result fmtRes = VulkanNative.GetPhysicalDeviceVideoFormatPropertiesKHR(_physicalDevice, &fmtInfo, &fmtCount, null);
        bool fmtOk = fmtRes == Result.Success && fmtCount > 0;
        if (fmtOk)
        {
            var fmtProps = stackalloc VideoFormatPropertiesKHR[(int)fmtCount];
            for (uint fi = 0; fi < fmtCount; fi++)
                fmtProps[(int)fi].SType = StructureType.VideoFormatPropertiesKhr;
            fmtRes = VulkanNative.GetPhysicalDeviceVideoFormatPropertiesKHR(_physicalDevice, &fmtInfo, &fmtCount, fmtProps);
            // 仅核对 format 是否受支持（忽略返回的 usage/flags，按权威范式自构）。
            bool fmtSupported = false;
            for (uint fi = 0; fi < fmtCount; fi++)
                if (fmtProps[(int)fi].Format == Format.G8B8R82Plane420Unorm) { fmtSupported = true; break; }
            if (!fmtSupported)
                Console.WriteLine($"[DIAG-FMT] 警告：查询未返回 G8B8R82Plane420Unorm（fmtCount={fmtCount}），DPB 格式可能不被支持。");
        }

        // 显式构造（decode-only，无 ENCODE/SPARSE；转换器采样/回读需 SAMPLED|TRANSFER_SRC）：
        ImageUsageFlags dpbUsage = ImageUsageFlags.VideoDecodeDpbBitKhr
                                 | ImageUsageFlags.VideoDecodeDstBitKhr
                                 | ImageUsageFlags.VideoDecodeSrcBitKhr
                                 | ImageUsageFlags.SampledBit
                                 | ImageUsageFlags.TransferSrcBit;
        // MUTABLE_FORMAT_BIT（转换器 per-plane R8/R8G8 视图必需，VUID-12397/01564）；
        // 含 SAMPLED 的多平面图像再加 EXTENDED_USAGE_BIT（VUID-01564）。权威范式仅 MUTABLE，此处保守补齐两者。
        ImageCreateFlags dpbCreateFlags = (ImageCreateFlags)0x00000008u | (ImageCreateFlags)0x00000004u;
        Console.WriteLine($"[DIAG-FMT] DPB 格式构造: format=G8B8R82Plane420Unorm createFlags=0x{(uint)dpbCreateFlags:X} usage=0x{(uint)dpbUsage:X} (queryOk={fmtOk})");

        var imageCi = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.G8B8R82Plane420Unorm,
            Extent = new Extent3D { Width = (uint)_codedW, Height = (uint)_codedH, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = (uint)_maxDpbSlots,   // 单一 arrayed 图像：所有 DPB 槽 = 不同层
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            // 查询结果（含 MUTABLE_FORMAT_BIT + EXTENDED_USAGE_BIT，允许转换器建 per-plane R8/R8G8 视图采样）。
            Flags = dpbCreateFlags,
            Usage = dpbUsage,
            SharingMode = crossFamily ? SharingMode.Concurrent : SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            PNext = &profileList,
        };

        // CONCURRENT 共享的队列族列表（video 族 + graphics 族），须存活至 CreateImage 调用返回。
        if (crossFamily)
        {
            var qfis = stackalloc uint[2] { _videoQueueFamilyIndex, _graphicsQueueFamilyIndex };
            imageCi.QueueFamilyIndexCount = 2;
            imageCi.PQueueFamilyIndices = qfis;
        }

        // 单一 arrayed 图像（所有槽共享）
        if (VulkanNative.CreateImage(_device, ref imageCi, null, out _dpbImage) != Result.Success)
            throw new NotSupportedException("创建 DPB arrayed 图像失败");
        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, _dpbImage, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
        };
        if (VulkanNative.AllocateMemory(_device, ref alloc, null, out _dpbMemory) != Result.Success)
            throw new NotSupportedException("分配 DPB 内存失败");
        if (VulkanNative.BindImageMemory(_device, _dpbImage, _dpbMemory, 0) != Result.Success)
            throw new NotSupportedException("绑定 DPB 内存失败");

        // 单一 layered 解码绑定视图：aspect=COLOR_BIT（VUID-07818：每视图至多一个 multiplanar aspect，
        // PLANE_0|PLANE_1 同时出现非法 → 视图创建失败 → 解码静默 no-op）。
        // 链 VkImageViewUsageCreateInfo、usage 仅 decode（不含 SAMPLED）→ 避免 VUID-06415
        //（multiplanar 格式 + SAMPLED 须带 VkSamplerYcbcrConversionInfo，解码绑定视图不需要）。
        ImageViewUsageCreateInfo viewUsage;
        viewUsage.SType = StructureType.ImageViewUsageCreateInfo;
        viewUsage.PNext = null;
        viewUsage.Usage = ImageUsageFlags.VideoDecodeDstBitKhr | ImageUsageFlags.VideoDecodeDpbBitKhr
                        | ImageUsageFlags.VideoDecodeSrcBitKhr;
        var viewCi = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            ViewType = ImageViewType.Type2DArray,
            Image = _dpbImage,
            Format = Format.G8B8R82Plane420Unorm,
            Components = new ComponentMapping
            {
                R = ComponentSwizzle.Identity,
                G = ComponentSwizzle.Identity,
                B = ComponentSwizzle.Identity,
                A = ComponentSwizzle.Identity,
            },
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = (uint)_maxDpbSlots,
            },
            PNext = &viewUsage,
        };
        if (VulkanNative.CreateImageView(_device, &viewCi, null, out _dpbView) != Result.Success)
            throw new NotSupportedException("创建 DPB layered 视图失败");

        // 各槽共享同一图像/视图/内存；解码经 FillDpbResource 设 BaseArrayLayer=slotIndex 选层。
        for (int i = 0; i < _maxDpbSlots; i++)
            _dpb[i] = new DpbSlot { Image = _dpbImage, Memory = _dpbMemory, ImageView = _dpbView };
    }

    private void CreateBitstreamBuffer()
    {
        _bitstreamSize = 1024 * 1024 * 4; // 初始 4MB，按需增长

        // 码流缓冲带 VIDEO_DECODE_SRC usage 必须链 profile list（VUID-04813）；否则解码器视缓冲"不兼容"→ 静默 no-op。
        VideoDecodeH264ProfileInfoKHR h264Profile;
        h264Profile.SType = StructureType.VideoDecodeH264ProfileInfoKhr;
        h264Profile.PNext = null;
        h264Profile.StdProfileIdc = _paramSet!.Sps.ProfileIdc;
        h264Profile.PictureLayout = (VideoDecodeH264PictureLayoutFlagsKHR)0;
        VideoProfileInfoKHR profile;
        profile.SType = StructureType.VideoProfileInfoKhr;
        profile.PNext = &h264Profile;
        profile.VideoCodecOperation = VideoCodecOperationFlagsKHR.DecodeH264BitKhr;
        profile.ChromaSubsampling = VideoChromaSubsamplingFlagsKHR.Subsampling420BitKhr;
        profile.LumaBitDepth = VideoComponentBitDepthFlagsKHR.Depth8BitKhr;
        profile.ChromaBitDepth = VideoComponentBitDepthFlagsKHR.Depth8BitKhr;
        VideoProfileListInfoKHR profileList;
        profileList.SType = StructureType.VideoProfileListInfoKhr;
        profileList.PNext = null;
        profileList.ProfileCount = 1;
        profileList.PProfiles = &profile;

        var bufCi = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = _bitstreamSize,
            Usage = BufferUsageFlags.VideoDecodeSrcBitKhr,
            SharingMode = SharingMode.Exclusive,
            PNext = &profileList,
        };
        if (VulkanNative.CreateBuffer(_device, ref bufCi, null, out _bitstreamBuf) != Result.Success)
            throw new NotSupportedException("创建比特流缓冲失败");

        MemoryRequirements memReq;
        VulkanNative.GetBufferMemoryRequirements(_device, _bitstreamBuf, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
        };
        if (VulkanNative.AllocateMemory(_device, ref alloc, null, out _bitstreamMem) != Result.Success)
            throw new NotSupportedException("分配比特流内存失败");
        if (VulkanNative.BindBufferMemory(_device, _bitstreamBuf, _bitstreamMem, 0) != Result.Success)
            throw new NotSupportedException("绑定比特流内存失败");

        void* mapped = null;
        if (VulkanNative.MapMemory(_device, _bitstreamMem, 0, _bitstreamSize, 0, &mapped) != Result.Success)
            throw new NotSupportedException("映射比特流内存失败");
        _bitstreamMapped = mapped;
    }

    private void OnFrameReleased(int slotIndex)
    {
        if (slotIndex >= 0 && slotIndex < _slotDisplayDone.Length)
            _slotDisplayDone[slotIndex] = true;
    }

    private int PickFreeSlot()
    {
        for (int i = 0; i < _maxDpbSlots; i++)
        {
            if (!_slotInRef[i] && (_slotEmpty[i] || _slotDisplayDone[i]))
                return i;
        }
        // 兜底：取首个非 active 引用（理论不可达，DPB 槽数应 ≥ 并发参考数 + 1）
        for (int i = 0; i < _maxDpbSlots; i++)
            if (!_slotInRef[i]) return i;
        return 0;
    }

    private void EnsureBitstreamCapacity(ulong needed)
    {
        // 按 capabilities.MinBitstreamBufferSizeAlignment 对齐（创建时默认 4MB 通常足够）
        if (needed <= _bitstreamSize) return;
        // 增长：重建缓冲（罕见路径）
        VulkanNative.DeviceWaitIdle(_device);
        if (_bitstreamMapped != null) VulkanNative.UnmapMemory(_device, _bitstreamMem);
        VulkanNative.DestroyBuffer(_device, _bitstreamBuf, null);
        VulkanNative.FreeMemory(_device, _bitstreamMem, null);
        _bitstreamSize = needed * 2;
        CreateBitstreamBuffer();
    }

    private void FillDpbResource(ref VideoPictureResourceInfoKHR res, int slotIndex)
    {
        res.SType = StructureType.VideoPictureResourceInfoKhr;
        res.PNext = null;
        res.CodedOffset = new Offset2D { X = 0, Y = 0 };
        res.CodedExtent = new Extent2D { Width = (uint)_codedW, Height = (uint)_codedH };
        res.BaseArrayLayer = (uint)slotIndex; // 单一 arrayed 图像：槽 = 不同层
        res.ImageViewBinding = _dpbView;      // 单一 layered 视图（所有槽共享）
    }

    private void FillRefSlot(ref VideoReferenceSlotInfoKHR slot, int slotIndex,
        ref VideoPictureResourceInfoKHR resource, VideoDecodeH264DpbSlotInfoKHR* pDpbSlotInfo)
    {
        slot.SType = StructureType.VideoReferenceSlotInfoKhr;
        // 规范要求（VK_KHR_video_decode_h264 4.5/4.9 节）：setup 槽（重建帧）与 active 参考槽的
        // VkVideoReferenceSlotInfoKHR 必须经 pNext 链 VkVideoDecodeH264DpbSlotInfoKHR 携带
        // StdVideoDecodeH264ReferenceInfo；Silk.NET 2.23.0 的 VideoReferenceSlotInfoKHR 无 PStdReferenceInfo 直接字段。
        // pNext 链缺失时解码器对重建帧无参考信息 → 静默不落盘 → 全零 DPB → 恒绿（绿屏成因）。
        slot.PNext = (void*)pDpbSlotInfo;
        slot.SlotIndex = slotIndex;
        slot.PPictureResource = (VideoPictureResourceInfoKHR*)Unsafe.AsPointer(ref resource);
    }

    /// <summary>
    /// 确保指定 DPB 槽图像在视频解码前处于 <see cref="ImageLayout.VideoDecodeDpbBitKhr"/> 布局。
    /// 规范 VU：video decode 操作要求所有被访问图像子资源（解码输出/重建/参考）解码前即处于该布局，
    /// 解码器不会隐式转换——输出槽经 COINCIDE 模式同时作为解码输出与重建，亦须此布局。
    /// DPB 图像以 <c>VK_SHARING_MODE_CONCURRENT</c> 跨 video/graphics 双队列族共享，
    /// 故屏障无需所有权转移（族索引取 <c>~0u</c> = VK_QUEUE_FAMILY_IGNORED）。
    /// </summary>
    private void EnsureSlotDecodeLayout(int slot)
    {
        ImageLayout current = _slotLayout[slot];
        if (current == ImageLayout.VideoDecodeDpbKhr) return; // 已是解码所需布局

        // 依当前布局推导源阶段/访问：CONCURRENT 共享下跨族可见性由布局屏障建立，
        // 且消费者（渲染器）经 _slotDisplayDone 门控后才复用，故源数据已稳定。
        (PipelineStageFlags srcStage, AccessFlags srcAccess) = current switch
        {
            ImageLayout.Undefined => (PipelineStageFlags.TopOfPipeBit, AccessFlags.None),
            ImageLayout.ShaderReadOnlyOptimal => (PipelineStageFlags.FragmentShaderBit, AccessFlags.ShaderReadBit),
            ImageLayout.TransferSrcOptimal => (PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit),
            _ => (PipelineStageFlags.TopOfPipeBit, AccessFlags.None),
        };

        TransitionDpbSlotToDecodeLayout(slot, current, srcStage, srcAccess);
        _slotLayout[slot] = ImageLayout.VideoDecodeDpbKhr;
    }

    /// <summary>
    /// 把 DPB 槽 NV12 双平面（PLANE_0=Y、PLANE_1=UV）由 <paramref name="oldLayout"/> 转至
    /// <see cref="ImageLayout.VideoDecodeDpbBitKhr"/>，供后续 CmdDecodeVideoKHR 访问。
    /// </summary>
    private void TransitionDpbSlotToDecodeLayout(int slot, ImageLayout oldLayout, PipelineStageFlags srcStage, AccessFlags srcAccess)
    {
        // 原版 vkCmdPipelineBarrier 仅接受核心 PipelineStageFlags/AccessFlags（Silk.NET 2.23.0 的
        // video-decode 阶段/访问位仅存在于 PipelineStageFlags2/AccessFlags2，对应 synchronization2）。
        // 用 AllCommandsBit + MemoryRead/WriteBit 的「全屏障」等价覆盖 video-decode 阶段与读写访问，规范合法且零依赖。
        var dstStage = PipelineStageFlags.AllCommandsBit;
        var dstAccess = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;

        for (int p = 0; p < 2; p++)
        {
            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.VideoDecodeDpbKhr,
                SrcQueueFamilyIndex = ~0u, // CONCURRENT：忽略所有权转移
                DstQueueFamilyIndex = ~0u,
                Image = _dpb[slot].Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = p == 0 ? ImageAspectFlags.Plane0BitKhr : ImageAspectFlags.Plane1BitKhr,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = (uint)slot,
                    LayerCount = 1,
                },
            };
            VulkanNative.CmdPipelineBarrier(_commandBuffer, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
        }
    }
}

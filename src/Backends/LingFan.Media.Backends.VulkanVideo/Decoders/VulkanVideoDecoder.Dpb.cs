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
        // 解码用途提示（FFmpeg 权威范式：profile.pNext 链 UsageInfo→H264Profile；
        // 缺 UsageInfo 时部分驱动按「无解码用途」处理格式支持集 → 06811）。
        VideoDecodeUsageInfoKHR decodeUsage;
        decodeUsage.SType = StructureType.VideoDecodeUsageInfoKhr;
        decodeUsage.PNext = &h264Profile;
        decodeUsage.VideoUsageHints = VideoDecodeUsageFlagsKHR.DefaultKhr;
        VideoProfileInfoKHR profile;
        profile.SType = StructureType.VideoProfileInfoKhr;
        profile.PNext = &decodeUsage;
        profile.VideoCodecOperation = VideoCodecOperationFlagsKHR.DecodeH264BitKhr;
        profile.ChromaSubsampling = VideoChromaSubsamplingFlagsKHR.Subsampling420BitKhr;
        profile.LumaBitDepth = VideoComponentBitDepthFlagsKHR.Depth8BitKhr;
        profile.ChromaBitDepth = VideoComponentBitDepthFlagsKHR.Depth8BitKhr;
        VideoProfileListInfoKHR profileList;
        profileList.SType = StructureType.VideoProfileListInfoKhr;
        profileList.PNext = null;
        profileList.ProfileCount = 1;
        profileList.PProfiles = &profile;

        // DPB 图像 usage/flags：以视频格式属性查询返回值为权威（VUID-06811 supportedVideoFormat）。
        // 查询输入必须是「最小 decode usage」（DPB|DST，Coincide 模式）——与 FFmpeg hwcontext_vulkan
        // 同口径；输入越宽驱动返回的支持集越窄（曾以含 SAMPLED/SRC 的宽 usage 查询得 flags=0）。
        // 创建图像用「返回支持集」本身（它已含驱动允许的 SAMPLED/TRANSFER_SRC/MUTABLE 等），
        // 仅剔除 SPARSE 位（未启用 sparse 特性）；不额外添加返回集之外的位（06811 根因）。
        PhysicalDeviceVideoFormatInfoKHR fmtInfo;
        fmtInfo.SType = StructureType.PhysicalDeviceVideoFormatInfoKhr;
        fmtInfo.PNext = &profileList;
        // 查询 usage 与 FFmpeg vulkan_decode.c 完全一致（Coincide：DPB|DST|TRANSFER_SRC|SAMPLED，
        // 不含 DECODE_SRC——SRC 是码流缓冲 usage，混入图像查询会改变驱动返回的 flags 支持集）。
        ImageUsageFlags queryUsage = ImageUsageFlags.VideoDecodeDpbBitKhr | ImageUsageFlags.VideoDecodeDstBitKhr
                           | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit;
        fmtInfo.ImageUsage = queryUsage;
        uint fmtCount = 0;
        Result fmtRes = VulkanNative.GetPhysicalDeviceVideoFormatPropertiesKHR(_physicalDevice, &fmtInfo, &fmtCount, null);
        bool fmtOk = fmtRes == Result.Success && fmtCount > 0;
        ImageUsageFlags supportedUsage = 0;
        ImageCreateFlags supportedFlags = 0;
        bool fmtSupported = false;
        if (fmtOk)
        {
            var fmtProps = stackalloc VideoFormatPropertiesKHR[(int)fmtCount];
            for (uint fi = 0; fi < fmtCount; fi++)
                fmtProps[(int)fi].SType = StructureType.VideoFormatPropertiesKhr;
            fmtRes = VulkanNative.GetPhysicalDeviceVideoFormatPropertiesKHR(_physicalDevice, &fmtInfo, &fmtCount, fmtProps);
            for (uint fi = 0; fi < fmtCount; fi++)
            {
                if (fmtProps[(int)fi].Format != Format.G8B8R82Plane420Unorm) continue;
                fmtSupported = true;
                supportedUsage = fmtProps[(int)fi].ImageUsageFlags;
                supportedFlags = fmtProps[(int)fi].ImageCreateFlags;
                Console.WriteLine($"[DIAG-FMT] 驱动支持集: usage=0x{(uint)supportedUsage:X} flags=0x{(uint)supportedFlags:X} " +
                                  $"imageType={fmtProps[(int)fi].ImageType} tiling={fmtProps[(int)fi].ImageTiling} " +
                                  $"components=({fmtProps[(int)fi].ComponentMapping.R},{fmtProps[(int)fi].ComponentMapping.G}," +
                                  $"{fmtProps[(int)fi].ComponentMapping.B},{fmtProps[(int)fi].ComponentMapping.A})");
                break;
            }
            if (!fmtSupported)
                Console.WriteLine($"[DIAG-FMT] 警告：查询未返回 G8B8R82Plane420Unorm（fmtCount={fmtCount}），DPB 格式可能不被支持。");
        }

        // 创建用支持集（decode 三位必须在内，否则创建期 fail-fast 回落，不静默假绿）：
        // 创建用支持集 ∩ decode-only（剔除 ENCODE 三位：未启用 video_encode_queue 扩展，
        // VUID-usage-parameter 要求 usage 位对应扩展已启用）：
        ImageUsageFlags encodeMask = ImageUsageFlags.VideoEncodeDstBitKhr | ImageUsageFlags.VideoEncodeSrcBitKhr
                                   | ImageUsageFlags.VideoEncodeDpbBitKhr;
        ImageUsageFlags dpbUsage = supportedUsage & ~encodeMask;
        ImageUsageFlags requiredDecode = ImageUsageFlags.VideoDecodeDpbBitKhr | ImageUsageFlags.VideoDecodeDstBitKhr;
        if ((dpbUsage & requiredDecode) != requiredDecode)
            throw new NotSupportedException(
                $"驱动视频格式支持集缺 decode 必需 usage（supported=0x{(uint)supportedUsage:X}），Vulkan 硬解不可用。");
        // flags：支持集剔除 SPARSE 三位（未启用 sparse 特性，VUID-01924/00987）。
        // per-plane R8/R8G8 视图必需 MUTABLE_FORMAT（VUID-12397/01564）；部分驱动（本机 AMD）
        // 支持集 flags=0 不含 MUTABLE——设备已启用 videoMaintenance1 时加 VIDEO_PROFILE_INDEPENDENT
        // 豁免位（规范 resources 章：该位不受「flags ⊆ 支持集」检查约束，VUID-08329 要求 usage 含
        // DECODE_DST，本 DPB 为 Coincide 模式恒含）。
        // 注意：豁免只覆盖 VIDEO_PROFILE_INDEPENDENT 一位——MUTABLE/EXTENDED_USAGE 仍须在支持集内，
        // 支持集 flags=0 时不可加（06811 检查 flags & allowed == flags）。此分支仅补豁免位本身，
        // 使 DPB 图像创建合法；per-plane 视图缺 MUTABLE 的问题由转换器侧另行处理（见勘察报告）。
        ImageCreateFlags sparseMask = ImageCreateFlags.CreateSparseBindingBit | ImageCreateFlags.CreateSparseResidencyBit
                                    | ImageCreateFlags.CreateSparseAliasedBit;
        ImageCreateFlags dpbCreateFlags = supportedFlags & ~sparseMask;
        if (_gpuContext!.SupportsVideoProfileIndependent)
        {
            dpbCreateFlags |= ImageCreateFlags.CreateVideoProfileIndependentBitKhr;
            Console.WriteLine("[DIAG-FMT] videoMaintenance1 已启用 → DPB 加 VIDEO_PROFILE_INDEPENDENT（豁免位，支持集 flags 之外的唯一合法附加位）");
        }
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
        // 链 VkImageViewUsageCreateInfo、usage 仅 decode DST|DPB（不含 SAMPLED）→ 避免 VUID-06415
        //（multiplanar 格式 + SAMPLED 须带 VkSamplerYcbcrConversionInfo，解码绑定视图不需要）。
        // 亦不得含 VIDEO_DECODE_SRC（VUID-08335：该位只属 image 不属 view，解码源是码流缓冲）。
        ImageViewUsageCreateInfo viewUsage;
        viewUsage.SType = StructureType.ImageViewUsageCreateInfo;
        viewUsage.PNext = null;
        viewUsage.Usage = ImageUsageFlags.VideoDecodeDstBitKhr | ImageUsageFlags.VideoDecodeDpbBitKhr;
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
    /// 确保指定 DPB 槽图像在视频解码前处于 <see cref="ImageLayout.VideoDecodeDpbKhr"/> 布局。
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
    /// <see cref="ImageLayout.VideoDecodeDpbKhr"/>，供后续 CmdDecodeVideoKHR 访问。
    /// </summary>
    /// <remarks>
    /// 优先走 synchronization2（<c>vkCmdPipelineBarrier2</c>）：64 位掩码可精确表达
    /// video-decode 阶段位（<c>VIDEO_DECODE</c>）与访问位（<c>VIDEO_DECODE_READ/WRITE</c>），
    /// 并附全局内存屏障同步解码引擎写入（FFmpeg vulkan_decode.c 同口径）。
    /// 核心 32 位掩码不含这些位，AllCommands 通用屏障对视频解码引擎缓存的可见性不保证等价。
    /// synchronization2 不可用时回落核心全屏障路径（规范合法，覆盖语义尽力）。
    /// </remarks>
    private void TransitionDpbSlotToDecodeLayout(int slot, ImageLayout oldLayout, PipelineStageFlags srcStage, AccessFlags srcAccess)
    {
        if (VulkanNative.HasPipelineBarrier2)
        {
            TransitionDpbSlotToDecodeLayoutSync2(slot, oldLayout);
            return;
        }

        // 回落：原版 vkCmdPipelineBarrier 仅接受核心 PipelineStageFlags/AccessFlags（32 位，
        // 不含 video-decode 阶段/访问位）。用 AllCommandsBit + MemoryRead/WriteBit 的「全屏障」覆盖。
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

    /// <summary>
    /// synchronization2 精确屏障路径：图像布局屏障（双平面）+ 全局内存屏障一次提交。
    /// 源阶段/访问按旧布局映射到 64 位掩码；目标恒为 VIDEO_DECODE 阶段 + VIDEO_DECODE_READ|WRITE 访问。
    /// </summary>
    /// <param name="slot">DPB 槽索引。</param>
    /// <param name="oldLayout">槽当前布局（映射源阶段/访问）。</param>
    private void TransitionDpbSlotToDecodeLayoutSync2(int slot, ImageLayout oldLayout)
    {
        // 旧布局 → 64 位源阶段/访问映射（与核心路径的 switch 同语义）。
        (PipelineStageFlags2 srcStage2, AccessFlags2 srcAccess2) = oldLayout switch
        {
            ImageLayout.Undefined => (PipelineStageFlags2.None, AccessFlags2.None),
            ImageLayout.ShaderReadOnlyOptimal => (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderReadBit),
            ImageLayout.TransferSrcOptimal => (PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit),
            ImageLayout.VideoDecodeDpbKhr => (PipelineStageFlags2.VideoDecodeBitKhr,
                AccessFlags2.VideoDecodeReadBitKhr | AccessFlags2.VideoDecodeWriteBitKhr),
            _ => (PipelineStageFlags2.None, AccessFlags2.None),
        };

        ImageMemoryBarrier2* barriers = stackalloc ImageMemoryBarrier2[2];
        for (int p = 0; p < 2; p++)
        {
            barriers[p] = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                PNext = null,
                SrcStageMask = srcStage2,
                SrcAccessMask = srcAccess2,
                DstStageMask = PipelineStageFlags2.VideoDecodeBitKhr,
                DstAccessMask = AccessFlags2.VideoDecodeReadBitKhr | AccessFlags2.VideoDecodeWriteBitKhr,
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
        }

        // 全局内存屏障：解码引擎先前写入 → 本次解码读写（FFmpeg ff_vk_decode_frame 的 mem_bar 同口径）。
        MemoryBarrier2 memBarrier = new()
        {
            SType = StructureType.MemoryBarrier2,
            PNext = null,
            SrcStageMask = PipelineStageFlags2.VideoDecodeBitKhr,
            SrcAccessMask = AccessFlags2.VideoDecodeWriteBitKhr,
            DstStageMask = PipelineStageFlags2.VideoDecodeBitKhr,
            DstAccessMask = AccessFlags2.VideoDecodeReadBitKhr | AccessFlags2.VideoDecodeWriteBitKhr,
        };

        DependencyInfo depInfo = new()
        {
            SType = StructureType.DependencyInfo,
            PNext = null,
            DependencyFlags = DependencyFlags.ByRegionBit,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &memBarrier,
            BufferMemoryBarrierCount = 0,
            PBufferMemoryBarriers = null,
            ImageMemoryBarrierCount = 2,
            PImageMemoryBarriers = barriers,
        };
        VulkanNative.CmdPipelineBarrier2(_commandBuffer, &depInfo);
    }
}

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

/// <summary>
/// Vulkan 硬件解码器（VK_KHR_video_decode_h264），复用渲染器共享 VkDevice 实现零拷贝上屏。
/// </summary>
/// <remarks>
/// <para><b>零拷贝链路</b>：解码产出的 NV12 <c>VkImage</c> 存于 DPB 槽（<see cref="VulkanVideoFrameResource"/>，非释放），
/// 渲染器经 pattern matching 直接 blit 同设备纹理上屏，绝不经 CPU 回读。</para>
/// <para><b>回落语义</b>：Initialize 在「无 Vulkan 渲染器 / 设备未启用 VK_KHR_video_decode_* /
/// SPS-PPS 解析失败 / 能力不支持」时抛 <see cref="NotSupportedException"/>，由管线换下一工厂回退；
/// 运行时单帧解码失败则告警并跳过该帧（非静默假绿）。</para>
/// <para><b>DPB 复用模型</b>：sliding-window 参考帧管理——维护 active reference 槽列表，
/// 输出槽选取「非 active 引用且已显示/空」的槽；输出帧若为本帧参考则加入 active 列表，
/// 超出 maxActiveRefs 时淘汰最旧。B 帧显示重排与 MMCO 长时参考为已知简化点（见类末注释）。</para>
/// <para><b>异步策略</b>：Initialize 同步（GPU 会话建立为同步原生调用）；DecodeAsync/FlushAsync 返回
/// <see cref="ValueTask"/>（CPU 密集，无 I/O），用 ValueTask.FromResult 同步完成。</para>
/// </remarks>
internal sealed unsafe class VulkanVideoDecoder : IVideoDecoder
{
    private readonly ILogger<VulkanVideoDecoder> _logger;
    private readonly IGpuDeviceContext? _gpuContext;
    private readonly VulkanVideoOptions? _options;

    private Device _device;
    private PhysicalDevice _physicalDevice;
    private uint _videoQueueFamilyIndex;
    private Queue _videoQueue;
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private Fence _fence;

    private VideoSessionKHR _videoSession;
    private VideoSessionParametersKHR _sessionParams;
    private H264ParameterSet? _paramSet;
    private int _nalLengthSize;

    // DPB 槽
    private DpbSlot[] _dpb = Array.Empty<DpbSlot>();
    private int _maxDpbSlots;
    private int _maxActiveRefs;
    private readonly List<int> _references = new();        // active reference 槽（按加入序）
    private bool[] _slotInRef = Array.Empty<bool>();
    private bool[] _slotEmpty = Array.Empty<bool>();
    private bool[] _slotDisplayDone = Array.Empty<bool>();
    private StdVideoDecodeH264ReferenceInfo[] _slotRefInfo = Array.Empty<StdVideoDecodeH264ReferenceInfo>();

    // 比特流缓冲（HOST_VISIBLE，每帧覆写于 offset 0）
    private Buffer _bitstreamBuf;
    private DeviceMemory _bitstreamMem;
    private void* _bitstreamMapped;
    private ulong _bitstreamSize;

    private int _codedW;
    private int _codedH;
    private ulong _minBitstreamSizeAlign = 1;
    private bool _firstFrame = true;
    private bool _disposed;
    private bool _initialized;

    public VideoCodec Codec { get; private set; } = VideoCodec.Unknown;
    public bool IsHardwareAccelerated { get; private set; }

    public VulkanVideoDecoder(ILogger<VulkanVideoDecoder> logger, IGpuDeviceContext? gpuContext = null, VulkanVideoOptions? options = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _gpuContext = gpuContext;
        _options = options;
    }

    // ── IVideoDecoder ──

    public void Initialize(VideoCodec codec, VideoSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("Vulkan 视频解码器已初始化");

        if (codec != VideoCodec.H264)
            throw new NotSupportedException($"VulkanVideoDecoder 仅支持 H.264（当前 {codec}）");
        Codec = codec;

        if (!settings.EnableHardwareAcceleration)
            throw new NotSupportedException("Vulkan 视频解码要求 EnableHardwareAcceleration=true");

        // 无共享 Vulkan 设备上下文 → 无法零拷贝 → 回落
        if (_gpuContext is null || _gpuContext.ApiType != GPUApiType.Vulkan || !_gpuContext.IsInitialized
            || _gpuContext.VideoQueueFamilyIndex == uint.MaxValue)
        {
            throw new NotSupportedException(
                "未找到可用 Vulkan 渲染器共享设备 / VideoQueueFamilyIndex 无效（Vulkan 硬解需共享 VkDevice + video-decode 队列族）。");
        }

        try
        {
            _device = (Device)_gpuContext.SharedDevice!;
            _physicalDevice = (PhysicalDevice)_gpuContext.SharedPhysicalDevice!;
            _videoQueueFamilyIndex = _gpuContext.VideoQueueFamilyIndex;

            VulkanNative.InitVideoDevice(_device); // 解析 VK_KHR_video_decode_*；不可用即 InvalidOperationException
            CreateVideoSession(settings);
            CreateCommandResources();
            CreateDpb();
            CreateBitstreamBuffer();
            _initialized = true;
            IsHardwareAccelerated = true;
            _logger.LogInformation("Vulkan H.264 硬解会话已建立（共享 VkDevice，DPB={Dpb} 槽）", _maxDpbSlots);
        }
        catch (InvalidOperationException ex)
        {
            // 设备不支持 video-decode 扩展：干净回落（非静默假绿）
            IsHardwareAccelerated = false;
            throw new NotSupportedException($"Vulkan 视频解码扩展不可用：{ex.Message}", ex);
        }
        catch (Exception ex)
        {
            IsHardwareAccelerated = false;
            throw new NotSupportedException($"Vulkan H.264 硬解初始化失败：{ex.Message}", ex);
        }
    }

    public ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Vulkan 视频解码器尚未初始化");

        VideoFrame? frame = DecodeCore(packet);
        return ValueTask.FromResult(frame);
    }

    public ValueTask<VideoFrame?> FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Vulkan 视频解码器尚未初始化");
        // H.264 逐包即解出，无内部缓冲；无需排干。
        return ValueTask.FromResult<VideoFrame?>(null);
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：无 I/O（GPU 会话建立已在 <see cref="Initialize"/> 同步完成），返回 <see cref="Task.CompletedTask"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Reset()
    {
        if (!_initialized) return;
        // 清空 active 参考（RESET 命令于下一帧开头重建 DPB），释放槽占用标记
        _references.Clear();
        Array.Clear(_slotInRef);
        Array.Clear(_slotDisplayDone);
        // DPB 图像仍存活，复位后整池视为空、可复用
        for (int i = 0; i < _slotEmpty.Length; i++) _slotEmpty[i] = true;
        _firstFrame = true;
        _logger.LogDebug("Vulkan 视频解码器已复位（重播/seek）");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_device.Handle != 0)
        {
            VulkanNative.DeviceWaitIdle(_device);

            for (int i = 0; i < _dpb.Length; i++)
            {
                if (_dpb[i].ImageView.Handle != 0) VulkanNative.DestroyImageView(_device, _dpb[i].ImageView, null);
                if (_dpb[i].Image.Handle != 0) VulkanNative.DestroyImage(_device, _dpb[i].Image, null);
                if (_dpb[i].Memory.Handle != 0) VulkanNative.FreeMemory(_device, _dpb[i].Memory, null);
            }

            if (_commandPool.Handle != 0) VulkanNative.DestroyCommandPool(_device, _commandPool, null);
            if (_fence.Handle != 0) VulkanNative.DestroyFence(_device, _fence, null);

            if (_sessionParams.Handle != 0) VulkanNative.DestroyVideoSessionParametersKHR(_device, _sessionParams, null);
            if (_videoSession.Handle != 0) VulkanNative.DestroyVideoSessionKHR(_device, _videoSession, null);

            if (_bitstreamMapped != null) VulkanNative.UnmapMemory(_device, _bitstreamMem);
            if (_bitstreamBuf.Handle != 0) VulkanNative.DestroyBuffer(_device, _bitstreamBuf, null);
            if (_bitstreamMem.Handle != 0) VulkanNative.FreeMemory(_device, _bitstreamMem, null);
        }

        _paramSet?.Dispose();
        _paramSet = null;
        _logger.LogInformation("Vulkan 视频解码器已释放");
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：无异步资源，委托 <see cref="Dispose"/> + <see cref="ValueTask.CompletedTask"/>。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ── 会话建立 ──

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
        VideoCapabilitiesKHR caps;
        caps.SType = StructureType.VideoCapabilitiesKhr;
        caps.PNext = null;
        var res = VulkanNative.GetPhysicalDeviceVideoCapabilitiesKHR(_physicalDevice, &profile, &caps);
        if (res != Result.Success)
            throw new NotSupportedException($"vkGetPhysicalDeviceVideoCapabilitiesKHR 失败：{res}");

        _maxDpbSlots = (int)Math.Min(caps.MaxDpbSlots, 32);
        if (_maxDpbSlots < 1) _maxDpbSlots = 1;
        _maxActiveRefs = (int)Math.Min(_maxDpbSlots - 1, Math.Min(caps.MaxActiveReferencePictures, _paramSet.MaxNumRefFrames == 0 ? _maxDpbSlots - 1 : _paramSet.MaxNumRefFrames));
        _minBitstreamSizeAlign = caps.MinBitstreamBufferSizeAlignment == 0 ? 1 : caps.MinBitstreamBufferSizeAlignment;

        _codedW = (int)(_paramSet.PicWidthInMbsMinus1 + 1) * 16;
        _codedH = (int)(_paramSet.PicHeightInMapUnitsMinus1 + 1) * 16 * (_paramSet.FrameMbsOnlyFlag == 1 ? 1 : 2);
        if (_codedW <= 0 || _codedH <= 0)
            throw new NotSupportedException("SPS 给出的图像尺寸非法");

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
    }

    private void CreateDpb()
    {
        _dpb = new DpbSlot[_maxDpbSlots];
        _slotInRef = new bool[_maxDpbSlots];
        _slotEmpty = new bool[_maxDpbSlots];
        _slotDisplayDone = new bool[_maxDpbSlots];
        _slotRefInfo = new StdVideoDecodeH264ReferenceInfo[_maxDpbSlots];
        for (int i = 0; i < _maxDpbSlots; i++) _slotEmpty[i] = true;

        var imageCi = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.G8B8R82Plane420Unorm,
            Extent = new Extent3D { Width = (uint)_codedW, Height = (uint)_codedH, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.VideoDecodeDstBitKhr | ImageUsageFlags.VideoDecodeDpbBitKhr
                  | ImageUsageFlags.VideoDecodeSrcBitKhr | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        var viewCi = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            ViewType = ImageViewType.Type2D,
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
                LayerCount = 1,
            },
        };

        for (int i = 0; i < _maxDpbSlots; i++)
        {
            if (VulkanNative.CreateImage(_device, ref imageCi, null, out var image) != Result.Success)
                throw new NotSupportedException($"创建 DPB 图像 #{i} 失败");
            MemoryRequirements memReq;
            VulkanNative.GetImageMemoryRequirements(_device, image, &memReq);
            uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
            var alloc = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memReq.Size,
                MemoryTypeIndex = memType,
            };
            if (VulkanNative.AllocateMemory(_device, ref alloc, null, out var mem) != Result.Success)
                throw new NotSupportedException($"分配 DPB 内存 #{i} 失败");
            if (VulkanNative.BindImageMemory(_device, image, mem, 0) != Result.Success)
                throw new NotSupportedException($"绑定 DPB 内存 #{i} 失败");

            viewCi.Image = image;
            ImageView view;
            if (VulkanNative.CreateImageView(_device, &viewCi, null, out view) != Result.Success)
                throw new NotSupportedException($"创建 DPB 图像视图 #{i} 失败");

            _dpb[i] = new DpbSlot { Image = image, Memory = mem, ImageView = view };
        }
    }

    private void CreateBitstreamBuffer()
    {
        _bitstreamSize = 1024 * 1024 * 4; // 初始 4MB，按需增长
        var bufCi = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = _bitstreamSize,
            Usage = BufferUsageFlags.VideoDecodeSrcBitKhr,
            SharingMode = SharingMode.Exclusive,
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

    private uint FindMemoryType(uint memoryTypeBits, MemoryPropertyFlags required)
    {
        PhysicalDeviceMemoryProperties props;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &props);
        for (uint i = 0; i < props.MemoryTypeCount; i++)
        {
            if ((memoryTypeBits & (1u << (int)i)) != 0
                && (props.MemoryTypes[(int)i].PropertyFlags & required) == required)
                return i;
        }
        // 退而求其次：仅类型位匹配
        for (uint i = 0; i < props.MemoryTypeCount; i++)
            if ((memoryTypeBits & (1u << (int)i)) != 0) return i;
        throw new NotSupportedException("找不到匹配的内存类型");
    }

    // ── 解码核心 ──

    private VideoFrame? DecodeCore(MediaPacket packet)
    {
        var bitstream = H264SliceInfo.BuildBitstream(packet.Data.Span, _nalLengthSize, out var sliceOffsets);
        if (sliceOffsets.Length == 0) return null; // 无切片数据

        // 确保比特流缓冲足够（按最小对齐增长）
        EnsureBitstreamCapacity((ulong)bitstream.Length);

        // 写入（offset 0，恒对齐）
        new ReadOnlySpan<byte>(bitstream, 0, bitstream.Length).CopyTo(new Span<byte>(_bitstreamMapped, bitstream.Length));

        // 解析首个 slice 头
        int firstOff = sliceOffsets[0];
        byte nalHeader = bitstream[firstOff];
        byte nalRefIdc = (byte)((nalHeader >> 5) & 0x3);
        byte nalUnitType = (byte)(nalHeader & 0x1F);
        int nextOff = sliceOffsets.Length > 1 ? sliceOffsets[1] : bitstream.Length;
        var firstSliceRbsp = H264SliceInfo.ToRbsp(bitstream.AsSpan(firstOff + 1, nextOff - firstOff - 1));
        H264SliceInfo.ReadPictureInfo(firstSliceRbsp, nalRefIdc, nalUnitType, _paramSet!,
            out var picInfo, out var refInfo);

        bool isIdr = nalUnitType == 5;
        bool isRef = picInfo.Flags.IsReference != 0;
        bool isIntra = picInfo.Flags.IsIntra != 0;

        // IDR：清空 active 参考（RESET 命令将重建 DPB）
        if (isIdr)
        {
            _references.Clear();
            Array.Clear(_slotInRef);
        }

        int outputSlot = PickFreeSlot();
        _slotEmpty[outputSlot] = false;
        _slotDisplayDone[outputSlot] = false;

        SubmitDecode(outputSlot, picInfo, refInfo, sliceOffsets, (ulong)bitstream.Length);

        // 维护 active 参考
        if (isRef)
        {
            _slotInRef[outputSlot] = true;
            _slotRefInfo[outputSlot] = refInfo;
            _references.Add(outputSlot);
            if (_references.Count > _maxActiveRefs)
            {
                int evicted = _references[0];
                _references.RemoveAt(0);
                _slotInRef[evicted] = false; // 图像保留至消费者释放（DisplayDone）
            }
        }

        // 产出帧（非释放 DPB 图像，经 onReleased 回调标记 DisplayDone）
        var resource = new VulkanVideoFrameResource(
            _device, _dpb[outputSlot].Image, _dpb[outputSlot].Memory,
            _codedW, _codedH, PixelFormat.NV12, outputSlot, OnFrameReleased);
        var frame = new VideoFrame();
        frame.Reset(_codedW, _codedH, PixelFormat.NV12, resource,
            packet.Timestamp, packet.Duration, isIdr || isIntra);
        return frame;
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

    private void SubmitDecode(
        int outputSlot, StdVideoDecodeH264PictureInfo picInfo, StdVideoDecodeH264ReferenceInfo refInfo,
        int[] sliceOffsets, ulong bitstreamLen)
    {
        int beginCount = _references.Count + 1; // active refs + setup 槽
        var beginSlots = stackalloc VideoReferenceSlotInfoKHR[beginCount];
        var beginResources = stackalloc VideoPictureResourceInfoKHR[beginCount];

        // setup 槽置于末
        FillDpbResource(ref beginResources[beginCount - 1], outputSlot);
        FillRefSlot(ref beginSlots[beginCount - 1], outputSlot, ref beginResources[beginCount - 1]);
        for (int i = 0; i < _references.Count; i++)
        {
            FillDpbResource(ref beginResources[i], _references[i]);
            FillRefSlot(ref beginSlots[i], _references[i], ref beginResources[i]);
        }

        // 解码参考槽 = active refs 仅（不含 setup），复用 beginSlots 前段
        VideoReferenceSlotInfoKHR* pDecodeSlots = _references.Count > 0 ? (VideoReferenceSlotInfoKHR*)Unsafe.AsPointer(ref beginSlots[0]) : null;

        VideoBeginCodingInfoKHR beginInfo;
        beginInfo.SType = StructureType.VideoBeginCodingInfoKhr;
        beginInfo.PNext = null;
        beginInfo.Flags = 0;
        beginInfo.VideoSession = _videoSession;
        beginInfo.VideoSessionParameters = _sessionParams;
        beginInfo.ReferenceSlotCount = (uint)beginCount;
        beginInfo.PReferenceSlots = (VideoReferenceSlotInfoKHR*)Unsafe.AsPointer(ref beginSlots[0]);

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
        decodeInfo.PNext = null;
        decodeInfo.Flags = 0;
        decodeInfo.SrcBuffer = _bitstreamBuf;
        decodeInfo.SrcBufferOffset = 0;
        decodeInfo.SrcBufferRange = alignedRange;
        decodeInfo.DstPictureResource = beginResources[beginCount - 1];
        decodeInfo.PSetupReferenceSlot = (VideoReferenceSlotInfoKHR*)Unsafe.AsPointer(ref beginSlots[beginCount - 1]);
        decodeInfo.ReferenceSlotCount = (uint)_references.Count;
        decodeInfo.PReferenceSlots = pDecodeSlots;

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
        {
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = pCb,
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

    private static ulong AlignUp(ulong value, ulong align) => align <= 1 ? value : (value + align - 1) & ~(align - 1);

    private void FillDpbResource(ref VideoPictureResourceInfoKHR res, int slotIndex)
    {
        res.SType = StructureType.VideoPictureResourceInfoKhr;
        res.PNext = null;
        res.CodedOffset = new Offset2D { X = 0, Y = 0 };
        res.CodedExtent = new Extent2D { Width = (uint)_codedW, Height = (uint)_codedH };
        res.BaseArrayLayer = 0;
        res.ImageViewBinding = _dpb[slotIndex].ImageView;
    }

    private void FillRefSlot(ref VideoReferenceSlotInfoKHR slot, int slotIndex, ref VideoPictureResourceInfoKHR resource)
    {
        slot.SType = StructureType.VideoReferenceSlotInfoKhr;
        slot.PNext = null;
        slot.SlotIndex = slotIndex;
        // PStdReferenceInfo 可选（驱动首次建参考时记录），此处传 null
        slot.PPictureResource = (VideoPictureResourceInfoKHR*)Unsafe.AsPointer(ref resource);
    }

    // ── DPB 槽状态 ──

    private sealed class DpbSlot
    {
        public Image Image;
        public DeviceMemory Memory;
        public ImageView ImageView;
    }
}

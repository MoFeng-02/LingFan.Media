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
    private uint _graphicsQueueFamilyIndex = uint.MaxValue;
    private Queue _videoQueue;
    private Queue _readbackQueue; // 诊断回读队列（graphics 族，支持 TRANSFER；与渲染器 graphics 队列区分索引避免并发提交竞争）
    private CommandPool _commandPool;
    // 跨队列同步信号量环：每个 DPB 槽一把，解码 video 队列 signal、渲染器 graphics 队列 wait；
    // 槽在被消费者释放(DisplayDone)前不重用 → 同槽信号量必已被 wait（unsignaled）后再 signal，消除 VUID-00067 重复 signal。
    private Silk.NET.Vulkan.Semaphore[] _decodeDoneSemaphores = Array.Empty<Silk.NET.Vulkan.Semaphore>();
    // 信号量环尺寸：每 DPB 槽一把，须 ≥ 最大 DPB 槽数；槽在被消费者释放(DisplayDone)前不重用 → 同槽信号量必已被 wait（unsignaled）后再 signal，消除 VUID-00067/03238。
    private const int DecodeDoneSemaphoreRingSize = 64;
    private CommandBuffer _commandBuffer;
    private Fence _fence;
    private VulkanVideoGpuReadbackContext? _readbackContext;

    private VideoSessionKHR _videoSession;
    private VideoSessionParametersKHR _sessionParams;
    private H264ParameterSet? _paramSet;
    private int _nalLengthSize;

    // DPB 槽（单一 arrayed 图像：各槽 = 不同 array layer；见 CreateDpb）
    private DpbSlot[] _dpb = Array.Empty<DpbSlot>();
    private Image _dpbImage;       // 单一 arrayed DPB 图像（所有槽共享）
    private DeviceMemory _dpbMemory;
    private ImageView _dpbView;    // 单一 layered 视图（aspect=COLOR_BIT，供解码 imageViewBinding）
    private int _maxDpbSlots;
    private int _maxActiveRefs;
    private readonly List<int> _references = new();        // active reference 槽（按加入序）
    private bool[] _slotInRef = Array.Empty<bool>();
    private bool[] _slotEmpty = Array.Empty<bool>();
    private bool[] _slotDisplayDone = Array.Empty<bool>();
    private StdVideoDecodeH264ReferenceInfo[] _slotRefInfo = Array.Empty<StdVideoDecodeH264ReferenceInfo>();

    // 各 DPB 槽当前图像布局（default=ImageLayout.Undefined=尚未由解码写入）。
    // 规范 VU：video decode 操作要求所有被访问图像子资源（解码输出/重建/参考）在解码前即处于
    // VK_IMAGE_LAYOUT_VIDEO_DECODE_DPB_KHR，解码器不会隐式转换——故每次 SubmitDecode 前须确保涉及槽处于此布局。
    private ImageLayout[] _slotLayout = Array.Empty<ImageLayout>();

    // 比特流缓冲（HOST_VISIBLE，每帧覆写于 offset 0）
    private Buffer _bitstreamBuf;
    private DeviceMemory _bitstreamMem;
    private void* _bitstreamMapped;
    private ulong _bitstreamSize;

    private int _codedW;
    private int _codedH;
    private ulong _minBitstreamSizeAlign = 1;
    private ulong _minBitstreamOffsetAlign = 1;
    private bool _firstFrame = true;
    private int _diagEmitted;
    private int _diagSubmitEmitted;
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
            _graphicsQueueFamilyIndex = _gpuContext.GraphicsQueueFamilyIndex;

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

            // DPB 为单一 arrayed 图像，三资源各一份，仅销毁一次（_dpb[] 各槽共享同一组句柄）
            if (_dpbView.Handle != 0) VulkanNative.DestroyImageView(_device, _dpbView, null);
            if (_dpbImage.Handle != 0) VulkanNative.DestroyImage(_device, _dpbImage, null);
            if (_dpbMemory.Handle != 0) VulkanNative.FreeMemory(_device, _dpbMemory, null);

            if (_commandPool.Handle != 0) VulkanNative.DestroyCommandPool(_device, _commandPool, null);
            if (_fence.Handle != 0) VulkanNative.DestroyFence(_device, _fence, null);
            foreach (var sem in _decodeDoneSemaphores)
                if (sem.Handle != 0) VulkanNative.DestroySemaphore(_device, sem, null);
            _readbackContext?.Dispose();

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
        // ── 规范 VU 硬约束（khronos/lunarg 官方 VU，不可省略）──
        // VUID-vkGetPhysicalDeviceVideoCapabilitiesKHR-pVideoProfile-07184：
        //   当 videoCodecOperation 为 VK_VIDEO_CODEC_OPERATION_DECODE_H264_BIT_KHR 时，
        //   pCapabilities 的 pNext 链【必须】含 VkVideoDecodeH264CapabilitiesKHR。
        // VUID-...-07183：任何 decode 操作时，pNext 链【必须】含 VkVideoDecodeCapabilitiesKHR。
        // 缺失则驱动/验证层返回 VK_ERROR_INITIALIZATION_FAILED（此前真机崩溃根因）。
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
        // 解码器无任何参数集 → 静默产出全零 NV12 DPB → 恒绿（绿屏根因，此前被"验证层未加载"假象掩盖）。
        // 字段名经反射实证的 Silk.NET 2.23.0 拼写为 MaxStdSpscount / MaxStdPpscount（末位 c 小写）。
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
        // 规范铁律（VK_KHR_video_decode_h264 + 本机 profile 不支持 SEPARATE_REFERENCE_IMAGES）：
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

        // ── DPB 图像 usage/flags：按 Khronos Vulkan-Video-Samples 权威范式 ──
        // 关键铁律：视频格式属性查询的「返回值」只能用来确认 format/tiling 支持，
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

    /// <summary>
    /// 查询指定队列族的队列数量（诊断回读选队列索引用）。
    /// </summary>
    private static uint GetQueueFamilyQueueCount(PhysicalDevice pd, uint familyIndex)
    {
        uint count = 0;
        VulkanNative.GetPhysicalDeviceQueueFamilyProperties(pd, ref count, null);
        if (count == 0 || familyIndex >= count) return 0;
        var props = stackalloc QueueFamilyProperties[(int)count];
        for (uint i = 0; i < count; i++) props[(int)i] = new QueueFamilyProperties();
        VulkanNative.GetPhysicalDeviceQueueFamilyProperties(pd, ref count, props);
        return props[(int)familyIndex].QueueCount;
    }

    /// <summary>
    /// 扫描物理设备找一个支持 TRANSFER 的队列族（诊断回读兜底）。
    /// 优先选同时具备 GRAPHICS_BIT 的族（与渲染器同族，提交语义一致）；无则退任何 TRANSFER 族。
    /// 找不到返回 <see cref="uint.MaxValue"/>。
    /// </summary>
    private static uint FindTransferQueueFamily(PhysicalDevice pd)
    {
        uint count = 0;
        VulkanNative.GetPhysicalDeviceQueueFamilyProperties(pd, ref count, null);
        if (count == 0) return uint.MaxValue;
        var props = stackalloc QueueFamilyProperties[(int)count];
        for (uint i = 0; i < count; i++) props[(int)i] = new QueueFamilyProperties();
        VulkanNative.GetPhysicalDeviceQueueFamilyProperties(pd, ref count, props);
        uint fallback = uint.MaxValue;
        for (uint i = 0; i < count; i++)
        {
            QueueFlags flags = props[(int)i].QueueFlags;
            if ((flags & QueueFlags.TransferBit) == 0) continue;
            if ((flags & QueueFlags.GraphicsBit) != 0) return i; // 优先 graphics 族
            if (fallback == uint.MaxValue) fallback = i;
        }
        return fallback;
    }

    // ── 解码核心 ──

    private VideoFrame? DecodeCore(MediaPacket packet)
    {
        var bitstream = H264SliceInfo.BuildBitstream(packet.Data.Span, _nalLengthSize, out var sliceOffsets, _minBitstreamOffsetAlign);
        if (sliceOffsets.Length == 0) return null; // 无切片数据

        // 确保比特流缓冲足够（按最小对齐增长）
        EnsureBitstreamCapacity((ulong)bitstream.Length);

        // 写入（offset 0，恒对齐）
        new ReadOnlySpan<byte>(bitstream, 0, bitstream.Length).CopyTo(new Span<byte>(_bitstreamMapped, bitstream.Length));

        // [DIAG] 码流落盘校验：CPU 侧比对映射首 4 字节与源首 4 字节（确认写入落到正确缓冲地址；
        // 本机 host-visible 内存经回读/渲染器验证为 coherent ⇒ 一致即 GPU 必然可见，问题在解码命令 VU）。
        if (_diagEmitted < 2)
        {
            var mp = (byte*)_bitstreamMapped;
            // 起始码自检：Vulkan H.264 解码要求 pSliceOffsets 指向 NAL 头（无起始码前缀）；
            // 若缓冲头部仍含 00 00 01 / 00 00 00 01，说明 BuildBitstream 仍误带起始码 → 解码器静默丢弃 → 绿屏。
            bool hasSc = bitstream.Length >= 4 &&
                         ((bitstream[0] == 0 && bitstream[1] == 0 && bitstream[2] == 1) ||
                          (bitstream[0] == 0 && bitstream[1] == 0 && bitstream[2] == 0 && bitstream[3] == 1));
            Console.WriteLine($"[DIAG-BS-CPU] src={bitstream[0]:X2} {bitstream[1]:X2} {bitstream[2]:X2} {bitstream[3]:X2} " +
                              $"mapped={mp[0]:X2} {mp[1]:X2} {mp[2]:X2} {mp[3]:X2} startCodePresent={hasSc}");
        }

        // 首个 slice：sliceOffsets 已指向 NAL 头（起始码已被 BuildBitstream 剥离），直接取 NAL 头与 RBSP。
        // 规范铁律（VK_KHR_video_decode_h264 §42.11.1）：pSliceOffsets 须指向「slice header 起点」（即 NAL 头字节），
        // 比特流缓冲绝不可含起始码前缀（00 00 01）——含起始码会让解码器把起始码首字节当 NAL 头读成 type=0
        // （未定义 NAL）→ 静默丢弃全部切片 → DPB 全零 NV12 → 恒绿（绿屏真因）。
        int nalHeaderOff = sliceOffsets[0];
        byte nalHeader = bitstream[nalHeaderOff];
        byte nalRefIdc = (byte)((nalHeader >> 5) & 0x3);
        byte nalUnitType = (byte)(nalHeader & 0x1F);
        int nextOff = sliceOffsets.Length > 1 ? sliceOffsets[1] : bitstream.Length; // 下一 NAL 起始码 = 本 NAL 结束边界
        var firstSliceRbsp = H264SliceInfo.ToRbsp(bitstream.AsSpan(nalHeaderOff + 1, nextOff - nalHeaderOff - 1));
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

        // 跨队列完成信号量：每 DPB 槽一把（按 outputSlot 取），槽复用受 OnFrameReleased(_slotDisplayDone) 门控
        // → 消费者（渲染器 graphics 队列）wait 并释放后才允许解码器重用该槽及其信号量，杜绝
        // VUID-00067/03238（信号量仍 signaled 未 wait 即重用）。（Vulkan-Video-Samples 权威范式：per-slot 门控。）
        int semIdx = outputSlot;

        SubmitDecode(outputSlot, picInfo, refInfo, sliceOffsets, (ulong)bitstream.Length, semIdx);

        // [DIAG] 解码取证：打印前 2 帧切片码流与切片头解析结果（只读、可逆）。
        if (_diagEmitted < 2)
        {
            _diagEmitted++;
            string hex = "";
            int hn = bitstream.Length < 24 ? bitstream.Length : 24;
            for (int hi = 0; hi < hn; hi++) hex += $"{bitstream[hi]:X2} ";
            Console.WriteLine($"[DIAG-BS#{_diagEmitted}] len={bitstream.Length} slices={sliceOffsets.Length} " +
                              $"firstOff={sliceOffsets[0]} nalH=0x{nalHeader:X2} type={nalUnitType} idr={isIdr} " +
                              $"intra={isIntra} ref={isRef} fnum={picInfo.FrameNum} poc0={picInfo.PicOrderCnt[0]} " +
                              $"spsId={picInfo.SeqParameterSetId} ppsId={picInfo.PicParameterSetId}");
            Console.WriteLine($"[DIAG-BS-HEX#{_diagEmitted}] {hex}");
        }

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

        // 产出帧（非释放 DPB 图像，经 onReleased 回调标记 DisplayDone）。
        // 解码后该槽处于 VK_IMAGE_LAYOUT_VIDEO_DECODE_DPB_KHR，渲染器经中性转换器
        //（VideoDecodeDpbKhr → ShaderReadOnlyOptimal）采样，故交付布局显式声明为 VideoDecodeDpbKhr。
        var resource = new VulkanVideoFrameResource(
            _device, _dpbImage, _dpbMemory,
            _codedW, _codedH, PixelFormat.NV12, outputSlot, OnFrameReleased,
            ImageLayout.VideoDecodeDpbKhr, _decodeDoneSemaphores[semIdx], _readbackContext);
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

        // 规范铁律（VK_KHR_video_queue + 官方 H.264 解码样板）：本版 VkVideoBeginCodingInfoKHR
        // 无 pSetupReferenceSlot 字段，setup 槽须出现在 pReferenceSlots 中，但须以 slotIndex=-1 标记
        // （表示该图本帧作为重建目标、尚未关联 DPB 槽，由解码命令随后关联）。若用真实输出槽索引
        // （+值）则驱动误判该空槽为“已激活参考”→ 首帧（无参考）即触发 VU、驱动静默拒绝写入 DPB
        // → 恒全零 NV12 → (0,135,0) 绿屏（绿屏真因）。
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
        // 规范铁律（VK_KHR_video_decode_h264）：解码命令的 H.264 图片信息
        // （pStdPictureInfo / SliceCount / pSliceOffsets——即真正要解码的切片）必须挂在
        // VkVideoDecodeInfoKHR.PNext 链上。VkVideoDecodeInfoKHR 无对应内嵌字段，PNext=null 时
        // vkCmdDecodeVideoKHR 收不到任何切片偏移与图片参数 → 静默产出全零 DPB → 恒绿（绿屏根因）。
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

    private static ulong AlignUp(ulong value, ulong align) => align <= 1 ? value : (value + align - 1) & ~(align - 1);

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
        // 规范铁律（VK_KHR_video_decode_h264 4.5/4.9 节）：setup 槽（重建帧）与 active 参考槽的
        // VkVideoReferenceSlotInfoKHR 必须经 pNext 链 VkVideoDecodeH264DpbSlotInfoKHR 携带
        // StdVideoDecodeH264ReferenceInfo；Silk.NET 2.23.0 的 VideoReferenceSlotInfoKHR 无 PStdReferenceInfo 直接字段。
        // pNext 链缺失时解码器对重建帧无参考信息 → 静默不落盘 → 全零 DPB → 恒绿（绿屏根因）。
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

    // ── DPB 槽状态 ──

    private sealed class DpbSlot
    {
        public Image Image;
        public DeviceMemory Memory;
        public ImageView ImageView;
    }
}

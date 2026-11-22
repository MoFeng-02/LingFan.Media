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
internal sealed unsafe partial class VulkanVideoDecoder : IVideoDecoder
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

    // IVideoDecoder

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

    // 会话建立

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

    // 解码核心

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
        // 规范要求（VK_KHR_video_decode_h264 42.11.1 节）：pSliceOffsets 须指向「slice header 起点」（即 NAL 头字节），
        // 比特流缓冲绝不可含起始码前缀（00 00 01）——含起始码会让解码器把起始码首字节当 NAL 头读成 type=0
        // （未定义 NAL）→ 静默丢弃全部切片 → DPB 全零 NV12 → 恒绿（绿屏成因）。
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

    private static ulong AlignUp(ulong value, ulong align) => align <= 1 ? value : (value + align - 1) & ~(align - 1);

    // DPB 槽状态

    private sealed class DpbSlot
    {
        public Image Image;
        public DeviceMemory Memory;
        public ImageView ImageView;
    }
}

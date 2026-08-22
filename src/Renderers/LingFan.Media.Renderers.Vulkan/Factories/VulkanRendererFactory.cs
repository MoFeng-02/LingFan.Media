using LingFan.Media.Abstractions;
using LingFan.Media.GPUShare.D3D11;
using LingFan.Media.Renderers.Shared;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// <see cref="IVideoRendererFactory"/> 的 Vulkan 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。持有共享的 <c>VkInstance</c>、
/// <c>VkPhysicalDevice</c>、<c>VkDevice</c>、<c>VkQueue</c>；
/// <see cref="Create"/> 返回<b>缓存单例</b> <see cref="VulkanRenderer"/>
/// （共享 GPU Device，与 D3D11RendererFactory 模式一致）。</para>
/// <para>Vulkan 是跨平台 API（Windows / Linux / Android；macOS/iOS 经 MoltenVK 覆盖——
/// 仅引入 MoltenVK 让 Vulkan 后端在 Apple 平台初始化/跑 SwapChain，无空域零拷贝上屏属第二类，待 Apple 合成栈落地）。
/// Surface 创建用 Vulkan 自己的 WSI 扩展（Win32 / Android / Metal），不需要平台互操作文件。</para>
    /// <para>WSI 扩展（Surface/Swapchain）由 <c>VulkanNative</c> 零反射绑定经三阶段解析
    /// （实例句柄解析实例级 / WSI 实例扩展，设备句柄解析设备级 / WSI 设备扩展），
    /// 不使用 Silk.NET 的 <c>Vk</c> / <c>Khr*</c> 对象与加载层。</para>
/// <para>AOT 兼容：sealed 类，零反射、无 Silk.NET 运行期依赖。</para>
/// <para><b>资源安全</b>：<see cref="EnsureDeviceCreated"/> 内部使用局部变量 + try-catch 统一清理，
/// 任何步骤失败都按创建逆序释放已分配的 Vulkan 资源，杜绝泄漏。所有字段仅在全部成功后赋值。</para>
/// </remarks>
public sealed unsafe class VulkanRendererFactory : IVideoRendererFactory, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<VulkanRendererFactory> _logger;
    private readonly object _deviceLock = new();
    private readonly object _singletonLock = new();

    // 共享 Vulkan 资源（Singleton，由本工厂管理生命周期）
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _queue;
    private uint _queueFamilyIndex;
    private Queue _videoQueue;
    private uint _videoQueueFamilyIndex = uint.MaxValue;
    private RenderContext? _renderContext;

    // 所选物理设备的身份（供 no-airspace 源验证「同 GPU 对齐」）。
    private byte[] _physicalDeviceUuid = [];
    private byte[] _physicalDeviceLuid = [];
    // 零拷贝跨 API 导入对齐：优先选择 LUID 与此一致的物理设备。
    // ffmpeg 的 D3D11VA 共享纹理由「默认 D3D11 适配器」创建，Vulkan 必须选同一 GPU 才能导入
    // （跨厂商/跨 GPU 导入会被驱动拒绝，报 ErrorOutOfDeviceMemory）。默认 null = 不强制，
    // 沿用「独显优先」启发式（纯 Vulkan 渲染性能最优）。
    private byte[]? _preferredAdapterLuid;
    // 零拷贝跨 API 导入对齐开关（自动路径）：true 时 EnsureDeviceCreated 内将自动查询默认 D3D11 适配器
    // LUID 并注入 _preferredAdapterLuid（与 PreferredAdapterLuid 手动 set 二选一，手动 set 优先）。
    // 仅 Windows 生效；非 Windows 跳过（无 DXGI LUID 概念）。
    private bool _alignToD3D11DefaultAdapter;
    // 是否已为 no-airspace 共享表面启用外部内存/信号量扩展。
    private bool _externalSharingEnabled;
    // Apple / MoltenVK：是否已启用 VK_EXT_metal_objects（无空域零拷贝经其导出 IOSurface / MTLSharedEvent）。
    private bool _metalObjectsSharingEnabled;

    // 无锁快路径发布哨兵（volatile，最后赋值，保证其余字段写入全部可见）
    private volatile bool _deviceReady;

    private VulkanRenderer? _singleton;
    private bool _disposed;

    /// <summary>软帧缩放模式（契约层 <see cref="AspectRatioMode"/>）。默认 <see cref="AspectRatioMode.Uniform"/>（信箱）。</summary>
    /// <remarks>创建渲染器单例时透传至其实例；运行时改此值对缓存单例立即生效。</remarks>
    public AspectRatioMode ScaleMode { get; set; } = AspectRatioMode.Uniform;

    /// <summary>
    /// 零拷贝跨 API 导入对齐：优先选择 LUID 与此字节数组（8 字节）一致的 Vulkan 物理设备。
    /// </summary>
    /// <remarks>
    /// <para>用于 D3D11VA 零拷贝场景——ffmpeg 的 D3D11 共享纹理由「默认 D3D11 适配器」创建，
    /// 若 Vulkan 渲染器选了另一张 GPU（如独显优先选中与 D3D11 默认适配器不同的卡），
    /// 导入 D3D11 共享句柄会被驱动拒绝（<c>ErrorOutOfDeviceMemory</c>）。设此值可强制 Vulkan
    /// 选与 D3D11 纹理同 GPU 的设备，使零拷贝成立。</para>
    /// <para>默认 null：不强制对齐，沿用「独显优先」启发式（纯 Vulkan 渲染性能最优）。</para>
    /// </remarks>
    public byte[]? PreferredAdapterLuid
    {
        get => _preferredAdapterLuid;
        set => _preferredAdapterLuid = value;
    }

    /// <summary>
    /// 零拷贝跨 API 导入对齐（自动路径）开关：为 <see langword="true"/> 时，
    /// <see cref="EnsureDeviceCreated"/> 内将自动查询默认 D3D11 适配器 LUID 并注入
    /// <see cref="PreferredAdapterLuid"/>（与手动 set <see cref="PreferredAdapterLuid"/> 二选一，手动优先）。
    /// </summary>
    /// <remarks>仅 Windows 生效；非 Windows 跳过（无 DXGI LUID 概念）。默认 <see langword="false"/>。</remarks>
    public bool AlignToD3D11DefaultAdapter
    {
        get => _alignToD3D11DefaultAdapter;
        set => _alignToD3D11DefaultAdapter = value;
    }

    public VulkanRendererFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = _loggerFactory.CreateLogger<VulkanRendererFactory>();
    }

    public RenderContext Context
    {
        get
        {
            EnsureDeviceCreated();
            return _renderContext!;
        }
    }

    // ── no-airspace 共享表面源（VulkanSharedSurfaceSource）访问共享 Vulkan 资源的内部入口 ──
    // 仅同程序集（Vulkan 模块）可见：源经工厂构造，直接复用本工厂的 VkInstance/Device/Queue，
    // 严守「各 Renderer 管好自身（无头/有头/无空域）」架构原则，不跨界泄露给其它层。
    internal Instance SharedInstance => _instance;
    internal PhysicalDevice SharedPhysicalDevice => _physicalDevice;
    internal Device SharedDevice => _device;
    internal Queue SharedQueue => _queue;
    internal uint SharedQueueFamilyIndex => _queueFamilyIndex;
    internal ReadOnlyMemory<byte> PhysicalDeviceUuid => _physicalDeviceUuid;
    internal ReadOnlyMemory<byte> PhysicalDeviceLuid => _physicalDeviceLuid;
    internal bool ExternalSharingEnabled => _externalSharingEnabled;
    internal bool MetalObjectsSharingEnabled => _metalObjectsSharingEnabled;

    /// <inheritdoc/>
    public IVideoRenderer Create()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDeviceCreated();
        lock (_singletonLock)
        {
            if (_singleton is null || _singleton.IsDisposed)
            {
                _singleton = new VulkanRenderer(
                    _instance, _physicalDevice, _device, _queue, _queueFamilyIndex,
                    _loggerFactory.CreateLogger<VulkanRenderer>());
                _singleton.ScaleMode = this.ScaleMode;
                _logger.LogDebug("Vulkan 渲染器单例已创建（ScaleMode={ScaleMode}）", this.ScaleMode);
            }
            return _singleton;
        }
    }

    /// <summary>
    /// 创建零拷贝帧生产者（<see cref="IGpuFrameProducer"/>），供解码后端经中立桥把原生解码输出导入为 Vulkan 纹理。
    /// </summary>
    /// <remarks>
    /// 确保共享 GPU 设备已创建（延迟、线程安全）；返回单例生产者（持有本工厂的 VkDevice / VkPhysicalDevice）。
    /// 解码后端仅依赖 <see cref="IGpuFrameProducer"/> 抽象，不感知 Vulkan 绑定细节（依赖倒置严守）。
    /// </remarks>
    public VulkanGpuFrameProducer CreateFrameProducer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDeviceCreated();
        return new VulkanGpuFrameProducer(
            _device, _physicalDevice, _loggerFactory.CreateLogger<VulkanGpuFrameProducer>());
    }

    private void EnsureDeviceCreated()
    {
        // 无锁快路径检查发布哨兵（volatile 读自带 acquire 语义，
        // 保证看见 _deviceReady 时其余字段写入已全部对本线程可见）。
        if (_deviceReady) return;

        lock (_deviceLock)
        {
            if (_deviceReady) return;

            VulkanNative.InitBootstrap();

            // 全部使用局部变量——仅在全部成功后才赋值给字段，确保失败时可安全清理
            Instance instance = default;
            PhysicalDevice physicalDevice = default;
            Device device = default;
            Queue queue = default;
            uint queueFamilyIndex = 0;
            RenderContext? renderContext = null;
            string devName = "Unknown";

            try
            {
                // ── 创建 VkInstance ──
                var extensions = GetPlatformExtensions();
                nint extPtr = VulkanNative.StringArrayToPtr(extensions);

                var appInfo = new ApplicationInfo
                {
                    SType = StructureType.ApplicationInfo,
                    ApiVersion = MakeVersion(1, 3, 0),
                };

                var instInfo = new InstanceCreateInfo
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &appInfo,
                    EnabledExtensionCount = (uint)extensions.Length,
                    PpEnabledExtensionNames = (byte**)extPtr,
                };

                // ── 诊断：显式启用 Vulkan 验证层（仅当 LF_VULKAN_VALIDATION=1）──
                // 比依赖 loader 的 VK_INSTANCE_LAYERS 环境变量可靠（本机 loader 未注入该层，
                // 导致此前"零 VUID"为假象）。显式加入启用层列表，loader 必加载，
                // 验证层默认将 VUID 报告到 stderr，供绿屏等硬解 bug 收口。默认不启用、零影响。
                nint layerPtr = IntPtr.Zero;
                if (Environment.GetEnvironmentVariable("LF_VULKAN_VALIDATION") == "1")
                {
                    layerPtr = VulkanNative.StringArrayToPtr(new[] { "VK_LAYER_KHRONOS_validation" });
                    instInfo.EnabledLayerCount = 1;
                    instInfo.PpEnabledLayerNames = (byte**)layerPtr;
                }

                // MoltenVK 要求实例创建时带 VK_KHR_portability_enumeration 标志，
                // 否则 vkCreateInstance 返回 VK_ERROR_INCOMPATIBLE_DRIVER。
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                    instInfo.Flags = InstanceCreateFlags.EnumeratePortabilityBitKhr;

                Result result;
                // try-finally 保护 extPtr 内存释放，防止 CreateInstance 异常时泄漏
                try
                {
                    result = VulkanNative.CreateInstance(ref instInfo, null, out instance);
                }
                finally
                {
                    // CreateInstance 后立即释放扩展名字符串内存，不再需要保留
                    VulkanNative.FreeStringArrayPtr(extPtr);
                    if (layerPtr != IntPtr.Zero) VulkanNative.FreeStringArrayPtr(layerPtr);
                }

                if (result != Result.Success)
                    throw new InvalidOperationException($"vkCreateInstance 失败: {result}");

                // 实例已创建（且已启用 WSI 扩展）→ 解析实例级函数 + KHR 实例扩展
                VulkanNative.InitInstance(instance);

                // ── 枚举物理设备 ──
                uint physCount = 0;
                // 检查 EnumeratePhysicalDevices 返回值
                Result enumResult = VulkanNative.EnumeratePhysicalDevices(instance, ref physCount, null);
                if (enumResult != Result.Success)
                    throw new InvalidOperationException($"vkEnumeratePhysicalDevices 失败: {enumResult}");
                if (physCount == 0)
                    throw new InvalidOperationException("未找到 Vulkan 物理设备。");

                var physDevices = new PhysicalDevice[physCount];
                fixed (PhysicalDevice* pDevices = physDevices)
                {
                    enumResult = VulkanNative.EnumeratePhysicalDevices(instance, ref physCount, pDevices);
                    if (enumResult != Result.Success)
                        throw new InvalidOperationException($"vkEnumeratePhysicalDevices (第二次) 失败: {enumResult}");
                }
                // 零拷贝跨 GPU 对齐（自动路径）：启用且 Windows 且尚未手动指定 LUID 时，
                // 查询默认 D3D11 适配器 LUID 并注入，强制 Vulkan 选与 D3D11VA 共享纹理同 GPU。
                if (_alignToD3D11DefaultAdapter && OperatingSystem.IsWindows() && _preferredAdapterLuid is null)
                {
                    byte[]? luid = D3D11AdapterLuid.QueryDefaultAdapterLuid();
                    if (luid is not null)
                    {
                        _preferredAdapterLuid = luid;
                        _logger.LogInformation("零拷贝跨 GPU 对齐：已对齐 Vulkan 物理设备选择到 D3D11 默认适配器 LUID（{LuidHex}）",
                            Convert.ToHexString(luid));
                    }
                    else
                    {
                        _logger.LogWarning("零拷贝跨 GPU 对齐：查询默认 D3D11 适配器 LUID 失败，跳过对齐（单卡/独显机器通常无需对齐）");
                    }
                }

                // ── 选择物理设备——不再盲取 physDevices[0] ──
                // 硬条件：具备图形队列族；偏好序：独显 > 集显 > 虚拟 GPU > 其他。
                // 注：Present 能力查询需要 Surface，而工厂在无 Surface 阶段创建共享设备，
                // 故此处以图形队列族为硬条件；实际 Present 兼容性由 CreateSurface 后的
                // SwapChain 创建路径校验（失败会抛明确异常）。
                physicalDevice = default;
                queueFamilyIndex = uint.MaxValue;
                int bestScore = -1;
                foreach (var candidate in physDevices)
                {
                    uint famIdx = FindGraphicsQueueFamily(candidate);
                    if (famIdx == uint.MaxValue)
                        continue;

                    PhysicalDeviceProperties candProps;
                    VulkanNative.GetPhysicalDeviceProperties(candidate, &candProps);
                    int score = candProps.DeviceType switch
                    {
                        PhysicalDeviceType.DiscreteGpu => 3,
                        PhysicalDeviceType.IntegratedGpu => 2,
                        PhysicalDeviceType.VirtualGpu => 1,
                        _ => 0,
                    };

                    // 零拷贝跨 API 导入对齐：若指定了首选适配器 LUID（D3D11 默认适配器），
                    // 命中则大幅提权，压过独显优先启发式——跨 GPU/厂商导入 D3D11 共享纹理会被驱动拒绝。
                    if (_preferredAdapterLuid is { } wantLuid)
                    {
                        PhysicalDeviceIDProperties candIdProps = new()
                        {
                            SType = StructureType.PhysicalDeviceIDProperties,
                        };
                        PhysicalDeviceProperties2 candProps2 = new()
                        {
                            SType = StructureType.PhysicalDeviceProperties2,
                            PNext = &candIdProps,
                        };
                        VulkanNative.GetPhysicalDeviceProperties2(candidate, &candProps2);
                        // 不校验 DeviceLuidValid：本 Silk.NET 版本无该字段；无效 LUID 恒为 0，
                        // 与真实 D3D11 适配器 LUID（非 0）比较必不命中，安全回落独显优先。
                        if (LuidEquals(candIdProps.DeviceLuid, wantLuid))
                        {
                            score += 100;
                            _logger.LogDebug("Vulkan 物理设备选择：候选命中首选适配器 LUID，提权对齐零拷贝导入（{Name}）",
                                GetDeviceNameSafe(candProps.DeviceName));
                        }
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        physicalDevice = candidate;
                        queueFamilyIndex = famIdx;
                    }
                }

                if (queueFamilyIndex == uint.MaxValue)
                    throw new InvalidOperationException("未找到具备图形队列族的 Vulkan 物理设备。");

                // ── 创建逻辑设备 ──
                // 设备扩展：基础 VK_KHR_swapchain + 按平台/可用性过滤的外部内存/信号量导出扩展
                // （no-airspace 共享表面源需要；VK_KHR_external_memory/semaphore 在 Vulkan 1.1 已 core，
                // 但其 win32/fd 变体是独立扩展，必须逐一确认可用，否则 vkCreateDevice 整失败）。
                var devExts = GetDeviceExtensions(physicalDevice);
                nint devExtPtr = VulkanNative.StringArrayToPtr(devExts);

                // Android AHB 零拷贝：YCbCr 采样转换特性（samplerYcbcrConversion）须在设备创建期启用。
                // 先经 vkGetPhysicalDeviceFeatures2 查询支持性——不支持时不链入（保持设备创建合法），
                // AHB 导入路径由生产者 TryImport 失败回落软解（能力自报 + 行为副作用双判据，不假绿）。
                PhysicalDeviceSamplerYcbcrConversionFeatures ycbcrFeatures = default;
                bool enableYcbcrFeatures = false;
                if (OperatingSystem.IsAndroid()
                    && Array.IndexOf(devExts, "VK_ANDROID_external_memory_android_hardware_buffer") >= 0)
                {
                    PhysicalDeviceSamplerYcbcrConversionFeatures probe = new()
                    {
                        SType = StructureType.PhysicalDeviceSamplerYcbcrConversionFeatures,
                    };
                    PhysicalDeviceFeatures2 features2 = new()
                    {
                        SType = StructureType.PhysicalDeviceFeatures2,
                        PNext = &probe,
                    };
                    enableYcbcrFeatures =
                        VulkanNative.GetPhysicalDeviceFeatures2(physicalDevice, &features2) == Result.Success
                        && probe.SamplerYcbcrConversion;
                    if (enableYcbcrFeatures)
                    {
                        ycbcrFeatures = new PhysicalDeviceSamplerYcbcrConversionFeatures
                        {
                            SType = StructureType.PhysicalDeviceSamplerYcbcrConversionFeatures,
                            SamplerYcbcrConversion = true,
                        };
                    }
                    else
                    {
                        _logger.LogWarning("Android AHB 零拷贝：物理设备不支持 samplerYcbcrConversion 特性，AHB 导入将回落软解");
                    }
                }

                // 视频解码队列族（B4 Vulkan Video 硬解复用同一设备；无则跳过，不影响现有渲染）。
                // 直接写入字段（与 _device/_queue 等同为方法级工作变量，确保 catch 之后的赋值块仍可见）。
                _videoQueueFamilyIndex = FindVideoDecodeQueueFamily(physicalDevice);
                bool videoOnSeparateFamily = _videoQueueFamilyIndex != uint.MaxValue && _videoQueueFamilyIndex != queueFamilyIndex;
                // 若 video-decode 与 graphics 同族（部分 GPU 的 graphics 族兼具 VIDEO_DECODE_BIT），
                // 该族需 2 条队列（idx0=graphics, idx1=video），否则 graphics 族仅 1 条。
                bool videoSameFamily = _videoQueueFamilyIndex != uint.MaxValue && _videoQueueFamilyIndex == queueFamilyIndex;

                float queuePriority = 1.0f;
                var queueInfo = new DeviceQueueCreateInfo
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = queueFamilyIndex,
                    QueueCount = videoSameFamily ? 2u : 1u,
                    PQueuePriorities = &queuePriority,
                };

                var videoQueueInfo = new DeviceQueueCreateInfo
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = _videoQueueFamilyIndex,
                    QueueCount = 1,
                    PQueuePriorities = &queuePriority,
                };

                // 队列创建信息数组：graphics 族必含；video 在独立族时追加一条。
                DeviceQueueCreateInfo[] queueInfos = videoOnSeparateFamily
                    ? new[] { queueInfo, videoQueueInfo }
                    : new[] { queueInfo };

                var devInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount = (uint)queueInfos.Length,
                    PQueueCreateInfos = null,
                    EnabledExtensionCount = (uint)devExts.Length,
                    PpEnabledExtensionNames = (byte**)devExtPtr,
                };
                // Android AHB 零拷贝：链入 samplerYcbcrConversion 特性（局部变量存活至 CreateDevice 返回）。
                if (enableYcbcrFeatures)
                    devInfo.PNext = &ycbcrFeatures;

                fixed (DeviceQueueCreateInfo* pQueueInfos = queueInfos)
                {
                    devInfo.PQueueCreateInfos = pQueueInfos;
                    // try-finally 保护 devExtPtr 内存释放
                    try
                    {
                        result = VulkanNative.CreateDevice(physicalDevice, ref devInfo, null, out device);
                    }
                    finally
                    {
                        VulkanNative.FreeStringArrayPtr(devExtPtr);
                    }
                }

                if (result != Result.Success)
                    throw new InvalidOperationException($"vkCreateDevice 失败: {result}");

                // 设备已创建（且已启用 VK_KHR_swapchain）→ 解析设备级函数 + KHR swapchain 扩展
                VulkanNative.InitDevice(device);

                VulkanNative.GetDeviceQueue(device, queueFamilyIndex, 0, out queue);
                if (_videoQueueFamilyIndex != uint.MaxValue)
                {
                    // video 队列索引用 idx1（与 graphics 同族，2 队列）或 idx0（独立族）。
                    uint videoQueueIndex = videoOnSeparateFamily ? 0u : 1u;
                    VulkanNative.GetDeviceQueue(device, _videoQueueFamilyIndex, videoQueueIndex, out _videoQueue);
                }

                // ── 填充所选物理设备身份（供 no-airspace 共享表面源「同 GPU 对齐」）──
                // vkGetPhysicalDeviceProperties2 + pNext=PhysicalDeviceIDProperties 取 deviceUUID(16) / deviceLUID(8)。
                // 这些字段是稀疏固定的（多 GPU 机器上合成器与主 GPU 的身份必须一致才能跨设备导入）。
                PhysicalDeviceIDProperties idProps = new()
                {
                    SType = StructureType.PhysicalDeviceIDProperties,
                };
                PhysicalDeviceProperties2 props2 = new()
                {
                    SType = StructureType.PhysicalDeviceProperties2,
                    PNext = &idProps,
                };
                VulkanNative.GetPhysicalDeviceProperties2(physicalDevice, &props2);
                _physicalDeviceUuid = new byte[16];
                _physicalDeviceLuid = new byte[8];
                unsafe
                {
                    fixed (byte* pUuid = _physicalDeviceUuid, pLuid = _physicalDeviceLuid)
                    {
                        // DeviceUuid/DeviceLuid 是固定缓冲字段，不可再 fixed；
                        // 固定缓冲在 unsafe 上下文中可直接隐式转为 byte*。
                        byte* sUuid = idProps.DeviceUuid;
                        byte* sLuid = idProps.DeviceLuid;
                        for (int i = 0; i < 16; i++) pUuid[i] = sUuid[i];
                        for (int i = 0; i < 8; i++) pLuid[i] = sLuid[i];
                    }
                }
                // 外部共享是否真正可用（win32/fd 变体扩展须已启用），供源在 Create 时干净回退。
                bool hasExternalMem = OperatingSystem.IsWindows()
                    ? Array.IndexOf(devExts, "VK_KHR_external_memory_win32") >= 0
                    : (Array.IndexOf(devExts, "VK_KHR_external_memory_fd") >= 0);
                _externalSharingEnabled = hasExternalMem;
                // Apple / MoltenVK：无空域零拷贝经 VK_EXT_metal_objects 导出 IOSurface / MTLSharedEvent，
                // 与 Windows / Linux·Android 的 external_memory 路径并行；供源在 Create 时干净回退。
                bool hasMetalObjects = Array.IndexOf(devExts, "VK_EXT_metal_objects") >= 0;
                _metalObjectsSharingEnabled = hasMetalObjects;

                // ── KHR WSI 扩展由 VulkanNative 三阶段零反射解析
                //    （InitInstance 解析实例级 / WSI 实例扩展，InitDevice 解析设备级 / WSI 设备扩展），
                //    无需像 Silk.NET 那样用 TryGetInstanceExtension/TryGetDeviceExtension 加载扩展对象；
                //    运行时 CreateSurface/CreateSwapchain 直接调用 VulkanNative。 ──

                // ── 查询设备能力 ──
                PhysicalDeviceProperties props;
                VulkanNative.GetPhysicalDeviceProperties(physicalDevice, &props);
                // deviceName 是 256 字节 null-terminated UTF-8 数组——
                // new string(sbyte*) 按 ANSI 代码页解码会把非 ASCII 设备名解成乱码；
                // 且以 256 为硬上限截断，不无限信任非规范驱动的 NUL 结尾承诺。
                ReadOnlySpan<byte> nameSpan = new(props.DeviceName, 256);
                int nulIndex = nameSpan.IndexOf((byte)0);
                devName = System.Text.Encoding.UTF8.GetString(nulIndex >= 0 ? nameSpan[..nulIndex] : nameSpan);

                PhysicalDeviceMemoryProperties memProps;
                VulkanNative.GetPhysicalDeviceMemoryProperties(physicalDevice, &memProps);
                ulong heapSize = memProps.MemoryHeaps[0].Size;

                // 从 VkPhysicalDeviceProperties.limits 查询真实最大纹理尺寸
                int maxTextureSize = (int)props.Limits.MaxImageDimension2D;

                // DeviceHandle / ContextHandle 恒为 Zero：Vulkan 上下文不持有 D3D11 设备/上下文，
                // 而 MF / FFmpeg 的 D3D11VA 硬解路径会把 IGpuDeviceContext.DeviceHandle 当作 ID3D11Device*
                // 作 QueryInterface；若填入 Vulkan 原生句柄，该 QI 会对非 COM 指针解引用而访问违规。
                // 填 Zero 让这些路径在「无 D3D11 设备可共享」分支干净回落软解（契约文档亦规定无设备即 Zero）。
                // SharedDevice 仍保留 Vulkan 设备对象，供能力/诊断查询使用。
                // 视频解码支持标志：video-decode 扩展 + 视频解码队列族均存在时为真
                //（B4 后端据此走零拷贝硬解，否则软解兜底）。
                bool videoDecodeSupported = _videoQueueFamilyIndex != uint.MaxValue;
                renderContext = new RenderContext(
                    GPUApiType.Vulkan,
                    new GpuDeviceCapabilities(devName, heapSize, 0, maxTextureSize, true, videoDecodeSupported, -1),
                    IntPtr.Zero,
                    device,
                    IntPtr.Zero,
                    physicalDevice,
                    _videoQueueFamilyIndex,
                    graphicsQueueFamilyIndex: queueFamilyIndex);
            }
            catch
            {
                // 统一清理——按创建逆序释放已分配的资源，杜绝泄漏
                if (device.Handle != 0)
                    VulkanNative.DestroyDevice(device, null);
                if (instance.Handle != 0)
                    VulkanNative.DestroyInstance(instance, null);
                throw;
            }

            // 全部成功——赋值给字段。
            // _deviceReady 是无锁快路径的发布哨兵，必须【最后】赋值：
            // volatile 写自带 release 语义，保证其余字段（含 _renderContext）写入
            // 全部对走快路径的线程可见，避免「哨兵已置位但字段未写完」导致的 NRE。
            _instance = instance;
            _physicalDevice = physicalDevice;
            _device = device;
            _queue = queue;
            _queueFamilyIndex = queueFamilyIndex;
            _renderContext = renderContext;
            // volatile 写作为发布哨兵，必须【最后】赋值（acquire/release 语义保证其余字段写入可见）。
            _deviceReady = true;

            _logger.LogDebug("Vulkan 设备已创建（共享 Singleton）：{DeviceName}", devName);
        }
    }

    // 独立的图形队列族查找（供候选设备逐一评估复用）。
    private static unsafe uint FindGraphicsQueueFamily(PhysicalDevice device)
    {
        uint familyCount = 0;
        VulkanNative.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, null);
        if (familyCount == 0)
            return uint.MaxValue;

        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* pFamilies = families)
        {
            VulkanNative.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, pFamilies);
        }

        for (uint i = 0; i < familyCount; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                return i;
        }
        return uint.MaxValue;
    }

    // 独立的 video-decode 队列族查找（供 B4 Vulkan Video 硬解复用同一物理设备）。
    // VK_QUEUE_VIDEO_DECODE_BIT_KHR = 0x00000020；自定义绑定未单独特化该枚举值时按原始位比对。
    private static unsafe uint FindVideoDecodeQueueFamily(PhysicalDevice device)
    {
        uint familyCount = 0;
        VulkanNative.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, null);
        if (familyCount == 0)
            return uint.MaxValue;

        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* pFamilies = families)
        {
            VulkanNative.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, pFamilies);
        }

        const uint VideoDecodeQueueBit = 0x00000020;
        for (uint i = 0; i < familyCount; i++)
        {
            if (((uint)families[i].QueueFlags & VideoDecodeQueueBit) != 0)
                return i;
        }
        return uint.MaxValue;
    }

    /// <summary>从 Vulkan 固定 256 字节设备名缓冲安全取 UTF-8 字符串（诊断用）。</summary>
    private static unsafe string GetDeviceNameSafe(byte* name)
    {
        if (name is null) return "(unknown)";
        ReadOnlySpan<byte> span = new(name, 256);
        int nul = span.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(nul >= 0 ? span[..nul] : span);
    }

    /// <summary>比较 Vulkan 设备 LUID（8 字节固定缓冲）与目标 LUID 字节数组（8 字节）是否一致。</summary>
    private static unsafe bool LuidEquals(byte* a, byte[] b)
    {
        if (a is null || b is null || b.Length < 8) return false;
        for (int i = 0; i < 8; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    // 先枚举设备实际支持的扩展再过滤——直接请求未支持的扩展会让
    // vkCreateDevice 整体失败（ErrorExtensionNotPresent）。外部内存/信号量导出扩展
    // （win32/fd 变体）是独立扩展，须按平台 + 可用性过滤。
    private static unsafe string[] GetDeviceExtensions(PhysicalDevice physicalDevice)
    {
        var avail = new HashSet<string>(StringComparer.Ordinal);
        uint count = 0;
        if (VulkanNative.EnumerateDeviceExtensionProperties(physicalDevice, (byte*)null, ref count, (ExtensionProperties*)null)
                == Result.Success && count > 0)
        {
            var props = new ExtensionProperties[count];
            fixed (ExtensionProperties* p = props)
            {
                if (VulkanNative.EnumerateDeviceExtensionProperties(physicalDevice, (byte*)null, ref count, p) == Result.Success)
                {
                    for (uint i = 0; i < count; i++)
                    {
                        ReadOnlySpan<byte> nameSpan = new(p[i].ExtensionName, 256);
                        int nul = nameSpan.IndexOf((byte)0);
                        avail.Add(System.Text.Encoding.UTF8.GetString(nul >= 0 ? nameSpan[..nul] : nameSpan));
                    }
                }
            }
        }

        var exts = new List<string>(6) { "VK_KHR_swapchain" };
        void AddIfAvail(string name)
        {
            if (avail.Contains(name))
                exts.Add(name);
        }

        // no-airspace 共享表面源所需的外部内存/信号量导出扩展（VK_KHR_external_memory/
        // semaphore 在 Vulkan 1.1 已 core，但其 win32/fd 变体是独立扩展）。
        AddIfAvail("VK_KHR_external_memory");
        AddIfAvail("VK_KHR_external_semaphore");
        AddIfAvail("VK_KHR_dedicated_allocation");
        if (OperatingSystem.IsWindows())
        {
            AddIfAvail("VK_KHR_external_memory_win32");
            AddIfAvail("VK_KHR_external_semaphore_win32");
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
        {
            AddIfAvail("VK_KHR_external_memory_fd");
            AddIfAvail("VK_KHR_external_semaphore_fd");
            // Android AHB 零拷贝导入（MediaCodec 硬解帧 → Vulkan）：VK_ANDROID_external_memory_android_hardware_buffer。
            // 按可用性过滤（缺失则 AHB 导入路径由生产者 TryImport 返回 false 回落软解，不影响渲染）。
            if (OperatingSystem.IsAndroid())
                AddIfAvail("VK_ANDROID_external_memory_android_hardware_buffer");
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
        {
            // Apple / MoltenVK：无空域零拷贝经 VK_EXT_metal_objects 把 Vulkan 图像/信号量
            // 导出为 IOSurface / MTLSharedEvent（不使用 external_memory/external_semaphore 扩展）。
            AddIfAvail("VK_EXT_metal_objects");
        }

        // B4 Vulkan Video 硬解（Vulkan 闭环零拷贝）：条件启用 video-decode 扩展。
        // 按物理设备实际支持过滤，缺失则静默跳过——vkCreateDevice 不会因 video 扩展不可用而失败，
        // 现有渲染路径完全不受影响（不支持 video 解码的 GPU 仅由 B4 后端回落软件解码）。
        AddIfAvail("VK_KHR_video_queue");
        AddIfAvail("VK_KHR_video_decode_queue");
        AddIfAvail("VK_KHR_video_decode_h264");
        AddIfAvail("VK_KHR_video_decode_h265");

        return exts.ToArray();
    }

    // 先枚举实例实际支持的扩展再过滤——直接请求未支持的扩展会让
    // vkCreateInstance 整体失败（ErrorExtensionNotPresent），例如 Linux 无 X11 的
    // 纯 Wayland 环境请求 VK_KHR_xlib_surface 即全盘失败。
    private static unsafe string[] GetPlatformExtensions()
    {
        var availSet = new HashSet<string>(StringComparer.Ordinal);
        uint availCount = 0;
        if (VulkanNative.EnumerateInstanceExtensionProperties((byte*)null, &availCount, null) == Result.Success && availCount > 0)
        {
            var props = new ExtensionProperties[availCount];
            fixed (ExtensionProperties* pProps = props)
            {
                if (VulkanNative.EnumerateInstanceExtensionProperties((byte*)null, &availCount, pProps) == Result.Success)
                {
                    for (uint i = 0; i < availCount; i++)
                    {
                        ReadOnlySpan<byte> nameSpan = new(pProps[i].ExtensionName, 256);
                        int nul = nameSpan.IndexOf((byte)0);
                        availSet.Add(System.Text.Encoding.UTF8.GetString(nul >= 0 ? nameSpan[..nul] : nameSpan));
                    }
                }
            }
        }

        if (!availSet.Contains("VK_KHR_surface"))
            throw new InvalidOperationException("Vulkan 实例不支持 VK_KHR_surface 扩展，无法进行窗口渲染。");

        var exts = new List<string>(3) { "VK_KHR_surface" };
        void AddIfAvailable(string name)
        {
            if (availSet.Contains(name))
                exts.Add(name);
        }

        if (OperatingSystem.IsWindows())
            AddIfAvailable("VK_KHR_win32_surface");
        else if (OperatingSystem.IsLinux())
        {
            AddIfAvailable("VK_KHR_xlib_surface");
            AddIfAvailable("VK_KHR_wayland_surface");
        }
        else if (OperatingSystem.IsAndroid())
            AddIfAvailable("VK_KHR_android_surface");
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
        {
            // Apple / MoltenVK：Metal Surface + portability enumeration 扩展。
            // VK_KHR_portability_enumeration 须与工厂创建实例时的
            // InstanceCreateFlags.EnumeratePortabilityBitKhr 配对。
            AddIfAvailable("VK_EXT_metal_surface");
            AddIfAvailable("VK_KHR_portability_enumeration");
        }
        // B4 Vulkan Video 硬解：实例级启用 VK_KHR_video_queue。
        // 该扩展虽分类为 device extension，但 vkGetPhysicalDeviceVideoCapabilitiesKHR /
        // vkGetPhysicalDeviceVideoFormatPropertiesKHR 是【实例级分派函数】（首参 VkPhysicalDevice），
        // 规范要求其所在扩展在实例启用后方可正确调用（FFmpeg hwcontext_vulkan 亦在设备级启用，双保险）。
        // 条件式过滤——缺失则静默跳过，不会令 vkCreateInstance 因 ErrorExtensionNotPresent 整体失败。
        // 注：真正的 VU 硬约束在 VulkanVideoDecoder.CreateVideoSession 能力查询处——
        // pCapabilities 的 pNext 链必须挂 VkVideoDecodeCapabilitiesKHR + VkVideoDecodeH264CapabilitiesKHR
        // （VU 07183/07184），缺失则返回 VK_ERROR_INITIALIZATION_FAILED（此前真机崩溃根因）。
        AddIfAvailable("VK_KHR_video_queue");
        return exts.ToArray();
    }

    private static uint MakeVersion(uint major, uint minor, uint patch)
        => (major << 22) | (minor << 12) | patch;

    /// <summary>
    /// 释放共享 Vulkan 设备、实例与缓存的渲染器单例。
    /// </summary>
    /// <remarks>
    /// 已知限制：无 finalizer 兜底。Vulkan 原生资源（VkInstance/VkDevice）不使用 SafeHandle 封装，
    /// 若 DI 容器未正确 Dispose（如应用异常退出）将泄漏。DI Singleton 生命周期确保正常场景 Dispose 被调用。
    /// 可考虑用 SafeHandle 封装 VkInstance/VkDevice 以获得 CLR Critical Finalizer 兜底。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _singleton?.Dispose();
        _singleton = null;

        if (_device.Handle != 0)
        {
            // 先 DeviceWaitIdle 确保 GPU 完成所有工作再释放
            VulkanNative.DeviceWaitIdle(_device);
            VulkanNative.DestroyDevice(_device, null);
        }
        if (_instance.Handle != 0)
            VulkanNative.DestroyInstance(_instance, null);

        _logger.LogDebug("Vulkan 设备已释放");
    }
}

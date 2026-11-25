using LingFan.Media.Abstractions;
using LingFan.Media.GPUShare.D3D11;
using LingFan.Media.Renderers.Shared;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanRendererFactory 的设备创建职责段（部分类，按职责拆分，编译期等价）。
// 实例/逻辑设备创建（外部共享 device 采用 + 自建两条路径），发布哨兵 _deviceReady 最后写；
// 扩展枚举在 Extensions 段，工厂入口与字段在主文件。
public sealed unsafe partial class VulkanRendererFactory
{
    private void EnsureDeviceCreated()
    {
        // 无锁快路径检查发布哨兵（volatile 读自带 acquire 语义，
        // 保证看见 _deviceReady 时其余字段写入已全部对本线程可见）。
        if (_deviceReady) return;

        lock (_deviceLock)
        {
            if (_deviceReady) return;

            // 外部 device 分支：宿主注入共享 device 时直接采用
            // 背景：Android 上宿主（Avalonia）与我们曾各建一套 VkInstance/VkDevice，共享表面源的
            // dma_buf fd 从 Device B 导入到 Avalonia 的 Device A = **跨实例导入**，Adreno 对此实现
            // 有缺陷（vkAllocateMemory 报 INITIALIZATION_FAILED，参数全对齐仍失败）。
            // 统一为同一 device 后 fd 导入变成同 device（驱动内部路径），规避跨实例缺陷。
            // 方式：仅拿外部句柄重新解析函数表（InitInstance/InitDevice），不自建、不销毁外部资源。
            if (_externalDevice.Handle != 0 && _externalInstance.Handle != 0)
            {
                VulkanNative.InitBootstrap();
                VulkanNative.InitInstance(_externalInstance);
                VulkanNative.InitDevice(_externalDevice, samplerYcbcrFeatureEnabled: true);
                VulkanNative.GetDeviceQueue(_externalDevice, _externalQueueFamilyIndex, 0, out Queue extQueue);

                _instance = _externalInstance;
                _physicalDevice = _externalPhysicalDevice;
                _device = _externalDevice;
                _queue = extQueue;
                _queueFamilyIndex = _externalQueueFamilyIndex;
                // 外部 device 由平台模块自建（LingFan.Media.Avalonia.Android.VulkanDeviceFactory），
                // 设备级已启用
                // VK_KHR_external_memory_fd / external_semaphore_fd —— 与自建分支同判据置位，
                // 否则 VulkanSharedSurfaceSourceFactory.Create 会因 ExternalSharingEnabled=false 直接抛
                // NotSupportedException（外部分支跳过自建流程时最初漏了这一步）。
                _externalSharingEnabled = !OperatingSystem.IsWindows();
                _metalObjectsSharingEnabled = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();
                // 填充物理设备身份（deviceUUID/deviceLUID），否则 VulkanSharedSurfaceSourceFactory.Create 的
                // 「同 GPU 对齐」校验会因 PhysicalDeviceUuid 为空而抛「UUID 不匹配」（合成器上报了 UUID、
                // 我们是空数组 ⇒ 判不匹配）。统一走 VulkanDeviceSelector.GetPhysicalDeviceIdentity。
                (_physicalDeviceUuid, _physicalDeviceLuid) =
                    VulkanDeviceSelector.GetPhysicalDeviceIdentity(_externalPhysicalDevice);
                _renderContext = new RenderContext(
                    GPUApiType.Vulkan,
                    new GpuDeviceCapabilities("External(宿主共享 Vulkan device)", 0, 0, 0, true, true, -1),
                    IntPtr.Zero,
                    _externalDevice,
                    IntPtr.Zero,
                    _externalPhysicalDevice,
                    _externalQueueFamilyIndex,
                    graphicsQueueFamilyIndex: _externalQueueFamilyIndex);
                _deviceReady = true; // 发布哨兵最后写
                _logger.LogInformation(
                    "Vulkan 采用宿主注入的共享 device（同 device 零拷贝）：VkDevice=0x{Device:X} VkInstance=0x{Instance:X} 队列族={Family}",
                    _externalDevice.Handle, _externalInstance.Handle, _externalQueueFamilyIndex);
                return;
            }

            VulkanNative.InitBootstrap();

            // 全部使用局部变量——仅在全部成功后才赋值给字段，确保失败时可安全清理
            Instance instance = default;
            PhysicalDevice physicalDevice = default;
            Device device = default;
            Queue queue = default;
            uint queueFamilyIndex = 0;
            RenderContext? renderContext = null;
            string devName = "Unknown";
            // videoMaintenance1 启用状态（try 内探测/启用，try 外 RenderContext 构造消费，故声明在 try 外）。
            bool videoProfileIndependentEnabled = false;

            try
            {
                // 创建 VkInstance
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

                // 诊断：显式启用 Vulkan 验证层（仅当 LF_VULKAN_VALIDATION=1）
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

                // 零拷贝跨 GPU 对齐（自动路径）：启用且 Windows 且尚未手动指定 LUID 时，
                // 查询解码侧「首选适配器」（独显优先，与 FFmpeg D3D11VA 自有设备同策略）LUID 并注入，
                // 强制 Vulkan 选与 D3D11VA 共享纹理同 GPU。不取「默认适配器」——那是主显示器所在 GPU，
                // 混合显卡上常为核显，与独显优先的解码设备不同卡，跨厂商导入会被驱动拒绝。
                if (_alignToD3D11DefaultAdapter && OperatingSystem.IsWindows() && _preferredAdapterLuid is null)
                {
                    var preferred = D3D11AdapterLuid.QueryPreferredAdapter();
                    if (preferred is { } pa)
                    {
                        _preferredAdapterLuid = pa.Luid;
                        _logger.LogInformation("零拷贝跨 GPU 对齐：已对齐 Vulkan 到解码侧首选适配器（{Description} vendor=0x{Vendor:X4}）",
                            pa.Description, pa.Vendor);
                    }
                    else
                    {
                        _logger.LogWarning("零拷贝跨 GPU 对齐：查询首选适配器失败，跳过对齐（单卡/独显机器通常无需对齐）");
                    }
                }

                // 选择物理设备——统一走 GPUShare.Vulkan 的 VulkanDeviceSelector（选卡策略唯一实现）：
                // 硬条件 = 具备图形队列族；偏好序 = 独显 > 集显 > 虚拟 GPU > 其他；
                // Linux VAAPI 同设备对齐（Intel vendor 提权）与 D3D11 LUID 对齐提权均在选择器内判定。
                (physicalDevice, queueFamilyIndex) = VulkanDeviceSelector.SelectPhysicalDevice(
                    instance, _preferredAdapterLuid, _logger);

                // 创建逻辑设备
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
                // samplerYcbcrConversion 需求方：① Android AHB 外部格式直采；② Vulkan Video 硬解
                // DPB 的 NV12 整图 YCbCr 采样（videoMaintenance1 路径，无需 MUTABLE 的 per-plane 视图）。
                bool needsYcbcr = (OperatingSystem.IsAndroid()
                    && Array.IndexOf(devExts, "VK_ANDROID_external_memory_android_hardware_buffer") >= 0)
                    || Array.IndexOf(devExts, "VK_KHR_video_decode_queue") >= 0;
                if (needsYcbcr)
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
                    Result probeResult = VulkanNative.GetPhysicalDeviceFeatures2(physicalDevice, &features2);
                    bool probeSupported = probeResult == Result.Success && probe.SamplerYcbcrConversion;

                    // 规范兜底：samplerYcbcrConversion 自 Vulkan 1.1 起为核心<b>必选</b>特性——
                    // apiVersion ≥ 1.1 的设备报告不支持属驱动/探测假阴性（部分 Adreno 设备上
                    // Features2 探测返回 false，与其 1.3 核心地位矛盾）。此时按规范直接启用：
                    // 1.1+ 一致性驱动必接受；极端非一致性驱动拒建设备 → 工厂抛异常 → 渲染回退链落 Skia，
                    // 与探测不支持时行为等价（不会更糟）。
                    PhysicalDeviceProperties devProps;
                    VulkanNative.GetPhysicalDeviceProperties(physicalDevice, &devProps);
                    bool specGuaranteed = devProps.ApiVersion >= MakeVersion(1, 1, 0);

                    enableYcbcrFeatures = probeSupported || specGuaranteed;
                    if (enableYcbcrFeatures)
                    {
                        ycbcrFeatures = new PhysicalDeviceSamplerYcbcrConversionFeatures
                        {
                            SType = StructureType.PhysicalDeviceSamplerYcbcrConversionFeatures,
                            SamplerYcbcrConversion = true,
                        };
                        // 诊断：探测原始结果 + apiVersion + 设备名（定位 Features2 假阴性之谜）
                        string ahbDevName = VulkanDeviceSelector.GetDeviceNameSafe(devProps.DeviceName);
                        _logger.LogInformation(
                            "Android AHB 零拷贝：samplerYcbcrConversion 已启用（探测={Probe}，probeResult={Result}，" +
                            "probeValue={ProbeValue}，apiVersion=0x{Api:X}，规范兜底={Spec}，device={Device}）",
                            probeSupported, probeResult, probe.SamplerYcbcrConversion, devProps.ApiVersion,
                            specGuaranteed, ahbDevName);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Android AHB 零拷贝：物理设备不支持 samplerYcbcrConversion 特性（探测 result={Result}，" +
                            "apiVersion=0x{Api:X}），AHB 导入将回落软解", probeResult, devProps.ApiVersion);
                    }
                }

                // 视频解码队列族（Vulkan Video 硬解复用同一设备；无则跳过，不影响现有渲染）。
                // 直接写入字段（与 _device/_queue 等同为方法级工作变量，确保 catch 之后的赋值块仍可见）。
                _videoQueueFamilyIndex = VulkanDeviceSelector.FindVideoDecodeQueueFamily(physicalDevice);
                bool videoOnSeparateFamily = _videoQueueFamilyIndex != uint.MaxValue && _videoQueueFamilyIndex != queueFamilyIndex;
                // 若 video-decode 与 graphics 同族（部分 GPU 的 graphics 族兼具 VIDEO_DECODE_BIT），
                // 该族需 2 条队列（idx0=graphics, idx1=video），否则 graphics 族仅 1 条。
                bool videoSameFamily = _videoQueueFamilyIndex != uint.MaxValue && _videoQueueFamilyIndex == queueFamilyIndex;

                // videoMaintenance1 特性探测（能力自报）：扩展在列表且 Features2 报告支持才链入启用。
                // 用途：DPB 图像带 VIDEO_PROFILE_INDEPENDENT 位绕过「flags ⊆ 驱动格式支持集」检查
                // （部分驱动支持集 flags=0 不含 MUTABLE，per-plane 视图必需的 MUTABLE 会触发 VUID-06811）。
                PhysicalDeviceVideoMaintenance1FeaturesKHR videoMaintenance1Features = default;
                bool enableVideoMaintenance1 = false;
                if (_videoQueueFamilyIndex != uint.MaxValue
                    && Array.IndexOf(devExts, "VK_KHR_video_maintenance1") >= 0)
                {
                    PhysicalDeviceVideoMaintenance1FeaturesKHR vm1Probe = new()
                    {
                        SType = StructureType.PhysicalDeviceVideoMaintenance1FeaturesKhr,
                    };
                    PhysicalDeviceFeatures2 vm1Features2 = new()
                    {
                        SType = StructureType.PhysicalDeviceFeatures2,
                        PNext = &vm1Probe,
                    };
                    Result vm1Result = VulkanNative.GetPhysicalDeviceFeatures2(physicalDevice, &vm1Features2);
                    enableVideoMaintenance1 = vm1Result == Result.Success && vm1Probe.VideoMaintenance1;
                    if (enableVideoMaintenance1)
                    {
                        videoMaintenance1Features = new PhysicalDeviceVideoMaintenance1FeaturesKHR
                        {
                            SType = StructureType.PhysicalDeviceVideoMaintenance1FeaturesKhr,
                            VideoMaintenance1 = true,
                        };
                    }
                    _logger.LogInformation(
                        "Vulkan videoMaintenance1 探测：扩展在列表=True，特性支持={Supported}（probeResult={Result}）→ 启用={Enabled}",
                        enableVideoMaintenance1, vm1Result, enableVideoMaintenance1);
                }
                videoProfileIndependentEnabled = enableVideoMaintenance1;

                // synchronization2 特性探测（能力自报）：Vulkan 1.3 核心必选特性，但经
                // vkCreateDevice（非 1.3 实例路径）创建的设备仍须显式链入 Features2 启用，
                // 否则 vkCmdPipelineBarrier2 触发 VUID-03848（feature not enabled）。
                // 用途：video-decode 精确阶段/访问屏障（64 位掩码，核心 32 位不含 video 位）。
                PhysicalDeviceVulkan13Features vulkan13Features = default;
                bool enableSync2 = false;
                if (_videoQueueFamilyIndex != uint.MaxValue)
                {
                    PhysicalDeviceVulkan13Features sync2Probe = new()
                    {
                        SType = StructureType.PhysicalDeviceVulkan13Features,
                    };
                    PhysicalDeviceFeatures2 sync2Features2 = new()
                    {
                        SType = StructureType.PhysicalDeviceFeatures2,
                        PNext = &sync2Probe,
                    };
                    Result sync2Result = VulkanNative.GetPhysicalDeviceFeatures2(physicalDevice, &sync2Features2);
                    enableSync2 = sync2Result == Result.Success && sync2Probe.Synchronization2;
                    if (enableSync2)
                    {
                        vulkan13Features = new PhysicalDeviceVulkan13Features
                        {
                            SType = StructureType.PhysicalDeviceVulkan13Features,
                            Synchronization2 = true,
                        };
                    }
                    _logger.LogInformation("Vulkan synchronization2 探测：特性支持={Supported}（probeResult={Result}）→ 启用={Enabled}",
                        enableSync2, sync2Result, enableSync2);
                }

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
                // videoMaintenance1：链头插入（pNext 链：vm1 → ycbcr → null；两者独立可并存）。
                if (enableVideoMaintenance1)
                {
                    videoMaintenance1Features.PNext = devInfo.PNext;
                    devInfo.PNext = &videoMaintenance1Features;
                }
                // synchronization2（Vulkan 1.3 特性结构）：链头插入，与 vm1/ycbcr 独立并存。
                if (enableSync2)
                {
                    vulkan13Features.PNext = devInfo.PNext;
                    devInfo.PNext = &vulkan13Features;
                }

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
                // samplerYcbcrFeatureEnabled：生产者 HasSamplerYcbcrConversion 双判据之一（函数可解析 ≠ 特性已启用）。
                VulkanNative.InitDevice(device, enableYcbcrFeatures);

                // 诊断：确认 OPAQUE_FD 零拷贝导出链路所需扩展状态。
                // Android Adreno 若实例级 external_memory_capabilities 缺失、或设备级 external_memory_fd
                // 未启用，vkBindImageMemory(OPAQUE_FD) 会报 ErrorInvalidExternalHandle，整条导出塌掉回退 Skia。
                // 下次设备运行据此判定（Fd=false→设备级扩展未进列表；Caps=false→实例级未启用）。
                _logger.LogInformation(
                    "Vulkan 共享设备扩展核对 [OPAQUE_FD 链路]：实例 external_memory_capabilities={Caps}，" +
                    "external_semaphore_capabilities={SemCaps}；设备 external_memory_fd={Fd}，" +
                    "external_semaphore_fd={SemFd}，android_hardware_buffer={Ahb}",
                    extensions.Contains("VK_KHR_external_memory_capabilities"),
                    extensions.Contains("VK_KHR_external_semaphore_capabilities"),
                    devExts.Contains("VK_KHR_external_memory_fd"),
                    devExts.Contains("VK_KHR_external_semaphore_fd"),
                    devExts.Contains("VK_ANDROID_external_memory_android_hardware_buffer"));

                VulkanNative.GetDeviceQueue(device, queueFamilyIndex, 0, out queue);
                if (_videoQueueFamilyIndex != uint.MaxValue)
                {
                    // video 队列索引用 idx1（与 graphics 同族，2 队列）或 idx0（独立族）。
                    uint videoQueueIndex = videoOnSeparateFamily ? 0u : 1u;
                    VulkanNative.GetDeviceQueue(device, _videoQueueFamilyIndex, videoQueueIndex, out _videoQueue);
                }

                // 填充所选物理设备身份（供 no-airspace 共享表面源「同 GPU 对齐」）
                // vkGetPhysicalDeviceProperties2 + pNext=PhysicalDeviceIDProperties 取 deviceUUID(16) / deviceLUID(8)。
                // 这些字段是稀疏固定的（多 GPU 机器上合成器与主 GPU 的身份必须一致才能跨设备导入）。
                (_physicalDeviceUuid, _physicalDeviceLuid) =
                    VulkanDeviceSelector.GetPhysicalDeviceIdentity(physicalDevice);
                // 外部共享是否真正可用（win32/fd 变体扩展须已启用），供源在 Create 时干净回退。
                bool hasExternalMem = OperatingSystem.IsWindows()
                    ? Array.IndexOf(devExts, "VK_KHR_external_memory_win32") >= 0
                    : (Array.IndexOf(devExts, "VK_KHR_external_memory_fd") >= 0);
                _externalSharingEnabled = hasExternalMem;
                // Apple / MoltenVK：无空域零拷贝经 VK_EXT_metal_objects 导出 IOSurface / MTLSharedEvent，
                // 与 Windows / Linux·Android 的 external_memory 路径并行；供源在 Create 时干净回退。
                bool hasMetalObjects = Array.IndexOf(devExts, "VK_EXT_metal_objects") >= 0;
                _metalObjectsSharingEnabled = hasMetalObjects;

                // KHR WSI 扩展由 VulkanNative 三阶段零反射解析
                //    （InitInstance 解析实例级 / WSI 实例扩展，InitDevice 解析设备级 / WSI 设备扩展），
                //    无需像 Silk.NET 那样用 TryGetInstanceExtension/TryGetDeviceExtension 加载扩展对象；
                //    运行时 CreateSurface/CreateSwapchain 直接调用 VulkanNative。

                // 查询设备能力
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
                //（解码后端据此走零拷贝硬解，否则软解兜底）。
                bool videoDecodeSupported = _videoQueueFamilyIndex != uint.MaxValue;
                renderContext = new RenderContext(
                    GPUApiType.Vulkan,
                    new GpuDeviceCapabilities(devName, heapSize, 0, maxTextureSize, true, videoDecodeSupported, -1),
                    IntPtr.Zero,
                    device,
                    IntPtr.Zero,
                    physicalDevice,
                    _videoQueueFamilyIndex,
                    graphicsQueueFamilyIndex: queueFamilyIndex,
                    supportsVideoProfileIndependent: videoProfileIndependentEnabled);
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
            // 设备创建完成踪迹（stderr 直写）：用于区分停摆发生在工厂、渲染器构造还是 Attach 链；
            // 设备名同时确认 lavapipe/硬件 ICD。
            Console.Error.WriteLine($"[VULKAN-INIT] 共享设备创建完成：{devName}");
        }
    }
}

using LingFan.Media.Abstractions;
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
/// <para>Vulkan 是跨平台 API（Windows / Linux / Android；macOS/MoltenVK 待开发），
/// 不需要平台互操作文件——Surface 创建用 Vulkan 自己的 WSI 扩展。</para>
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
    private RenderContext? _renderContext;

    // 所选物理设备的身份（供 no-airspace 源验证「同 GPU 对齐」）。
    private byte[] _physicalDeviceUuid = [];
    private byte[] _physicalDeviceLuid = [];
    // 是否已为 no-airspace 共享表面启用外部内存/信号量扩展。
    private bool _externalSharingEnabled;

    // 无锁快路径发布哨兵（volatile，最后赋值，保证其余字段写入全部可见）
    private volatile bool _deviceReady;

    private VulkanRenderer? _singleton;
    private bool _disposed;

    /// <summary>软帧缩放模式（契约层 <see cref="AspectRatioMode"/>）。默认 <see cref="AspectRatioMode.Uniform"/>（信箱）。</summary>
    /// <remarks>创建渲染器单例时透传至其实例；运行时改此值对缓存单例立即生效。</remarks>
    public AspectRatioMode ScaleMode { get; set; } = AspectRatioMode.Uniform;

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
    // 严守「各 Renderer 管好自身（无头/有头/无空域）」宪法，不跨界泄露给其它层。
    internal Instance SharedInstance => _instance;
    internal PhysicalDevice SharedPhysicalDevice => _physicalDevice;
    internal Device SharedDevice => _device;
    internal Queue SharedQueue => _queue;
    internal uint SharedQueueFamilyIndex => _queueFamilyIndex;
    internal ReadOnlyMemory<byte> PhysicalDeviceUuid => _physicalDeviceUuid;
    internal ReadOnlyMemory<byte> PhysicalDeviceLuid => _physicalDeviceLuid;
    internal bool ExternalSharingEnabled => _externalSharingEnabled;

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

                float queuePriority = 1.0f;
                var queueInfo = new DeviceQueueCreateInfo
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = queueFamilyIndex,
                    QueueCount = 1,
                    PQueuePriorities = &queuePriority,
                };

                var devInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = (uint)devExts.Length,
                    PpEnabledExtensionNames = (byte**)devExtPtr,
                };

                // try-finally 保护 devExtPtr 内存释放
                try
                {
                    result = VulkanNative.CreateDevice(physicalDevice, ref devInfo, null, out device);
                }
                finally
                {
                    VulkanNative.FreeStringArrayPtr(devExtPtr);
                }

                if (result != Result.Success)
                    throw new InvalidOperationException($"vkCreateDevice 失败: {result}");

                // 设备已创建（且已启用 VK_KHR_swapchain）→ 解析设备级函数 + KHR swapchain 扩展
                VulkanNative.InitDevice(device);

                VulkanNative.GetDeviceQueue(device, queueFamilyIndex, 0, out queue);

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
                renderContext = new RenderContext(
                    GPUApiType.Vulkan,
                    new GpuDeviceCapabilities(devName, heapSize, 0, maxTextureSize, true, false, -1),
                    IntPtr.Zero,
                    device,
                    IntPtr.Zero);
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
        }
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

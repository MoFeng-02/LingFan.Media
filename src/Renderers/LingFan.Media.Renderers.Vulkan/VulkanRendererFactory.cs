using LingFan.Media.Renderers.Shared;
using Silk.NET.Vulkan.Extensions.KHR;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// <see cref="IVideoRendererFactory"/> 的 Vulkan 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。持有共享的 <c>Vk</c> API、<c>VkInstance</c>、
/// <c>VkPhysicalDevice</c>、<c>VkDevice</c>、<c>VkQueue</c>、KHR 扩展对象，
/// <see cref="Create"/> 返回<b>缓存单例</b> <see cref="VulkanRenderer"/>
/// （共享 GPU Device，与 D3D11RendererFactory 模式一致）。</para>
/// <para>Vulkan 是跨平台 API（Windows / Linux / Android；macOS/MoltenVK 待开发），
/// 不需要平台互操作文件——Surface 创建用 Vulkan 自己的 WSI 扩展。</para>
/// <para>WSI 扩展（Surface/Swapchain）通过 <c>Vk.TryGetInstanceExtension</c> /
/// <c>Vk.TryGetDeviceExtension</c> 加载，Silk.NET 源生成绑定，AOT 友好。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
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
    private Vk? _vk;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _queue;
    private uint _queueFamilyIndex;
    private RenderContext? _renderContext;

    // KHR WSI 扩展对象
    private KhrSurface? _khrSurface;
    private KhrSwapchain? _khrSwapchain;
    private KhrWin32Surface? _khrWin32Surface;
    private KhrXlibSurface? _khrXlibSurface;
    private KhrWaylandSurface? _khrWaylandSurface;
    private KhrAndroidSurface? _khrAndroidSurface;

    private VulkanRenderer? _singleton;
    private bool _disposed;

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
                    _vk!, _instance, _physicalDevice, _device, _queue, _queueFamilyIndex,
                    _khrSurface!, _khrSwapchain!,
                    _khrWin32Surface, _khrXlibSurface, _khrWaylandSurface, _khrAndroidSurface,
                    _loggerFactory.CreateLogger<VulkanRenderer>());
                _logger.LogDebug("Vulkan 渲染器单例已创建");
            }
            return _singleton;
        }
    }

    private void EnsureDeviceCreated()
    {
        // 无锁快路径必须 Volatile.Read——与赋值段末尾的 Volatile.Write 配对，
        // 保证看见 _vk 非空时，其余字段（含 _renderContext）的写入已全部对本线程可见。
        if (System.Threading.Volatile.Read(ref _vk) is not null) return;

        lock (_deviceLock)
        {
            if (_vk is not null) return;

            var vk = Vk.GetApi();

            // 全部使用局部变量——仅在全部成功后才赋值给字段，确保失败时可安全清理
            Instance instance = default;
            PhysicalDevice physicalDevice = default;
            Device device = default;
            Queue queue = default;
            uint queueFamilyIndex = 0;
            KhrSurface? khrSurface = null;
            KhrSwapchain? khrSwapchain = null;
            KhrWin32Surface? khrWin32Surface = null;
            KhrXlibSurface? khrXlibSurface = null;
            KhrWaylandSurface? khrWaylandSurface = null;
            KhrAndroidSurface? khrAndroidSurface = null;
            RenderContext? renderContext = null;
            string devName = "Unknown";

            try
            {
                // ── 创建 VkInstance ──
                var extensions = GetPlatformExtensions(vk);
                nint extPtr = SilkMarshal.StringArrayToPtr(extensions);

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
                    result = vk.CreateInstance(ref instInfo, null, out instance);
                }
                finally
                {
                    // CreateInstance 后立即释放扩展名字符串内存，不再需要保留
                    SilkMarshal.Free(extPtr);
                }

                if (result != Result.Success)
                    throw new InvalidOperationException($"vkCreateInstance 失败: {result}");

                // ── 枚举物理设备 ──
                uint physCount = 0;
                // 检查 EnumeratePhysicalDevices 返回值
                Result enumResult = vk.EnumeratePhysicalDevices(instance, ref physCount, null);
                if (enumResult != Result.Success)
                    throw new InvalidOperationException($"vkEnumeratePhysicalDevices 失败: {enumResult}");
                if (physCount == 0)
                    throw new InvalidOperationException("未找到 Vulkan 物理设备。");

                var physDevices = new PhysicalDevice[physCount];
                fixed (PhysicalDevice* pDevices = physDevices)
                {
                    enumResult = vk.EnumeratePhysicalDevices(instance, ref physCount, pDevices);
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
                    uint famIdx = FindGraphicsQueueFamily(vk, candidate);
                    if (famIdx == uint.MaxValue)
                        continue;

                    PhysicalDeviceProperties candProps;
                    vk.GetPhysicalDeviceProperties(candidate, &candProps);
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
                var devExts = new[] { "VK_KHR_swapchain" };
                nint devExtPtr = SilkMarshal.StringArrayToPtr(devExts);

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
                    result = vk.CreateDevice(physicalDevice, ref devInfo, null, out device);
                }
                finally
                {
                    SilkMarshal.Free(devExtPtr);
                }

                if (result != Result.Success)
                    throw new InvalidOperationException($"vkCreateDevice 失败: {result}");

                vk.GetDeviceQueue(device, queueFamilyIndex, 0, out queue);

                // ── 加载 KHR WSI 扩展（失败时 catch 块统一清理）──
                if (!vk.TryGetInstanceExtension(instance, out khrSurface, "VK_KHR_surface"))
                    throw new InvalidOperationException("VK_KHR_surface 扩展不可用。");
                if (!vk.TryGetDeviceExtension(instance, device, out khrSwapchain, "VK_KHR_swapchain"))
                    throw new InvalidOperationException("VK_KHR_swapchain 扩展不可用。");

                // 平台 Surface 扩展（可选，失败不抛异常——运行时 CreateSurface 会检查 null）
                if (OperatingSystem.IsWindows())
                    vk.TryGetInstanceExtension(instance, out khrWin32Surface, "VK_KHR_win32_surface");
                else if (OperatingSystem.IsLinux())
                {
                    vk.TryGetInstanceExtension(instance, out khrXlibSurface, "VK_KHR_xlib_surface");
                    vk.TryGetInstanceExtension(instance, out khrWaylandSurface, "VK_KHR_wayland_surface");
                }
                else if (OperatingSystem.IsAndroid())
                    vk.TryGetInstanceExtension(instance, out khrAndroidSurface, "VK_KHR_android_surface");

                // ── 查询设备能力 ──
                PhysicalDeviceProperties props;
                vk.GetPhysicalDeviceProperties(physicalDevice, &props);
                // deviceName 是 256 字节 null-terminated UTF-8 数组——
                // new string(sbyte*) 按 ANSI 代码页解码会把非 ASCII 设备名解成乱码；
                // 且以 256 为硬上限截断，不无限信任非规范驱动的 NUL 结尾承诺。
                ReadOnlySpan<byte> nameSpan = new(props.DeviceName, 256);
                int nulIndex = nameSpan.IndexOf((byte)0);
                devName = System.Text.Encoding.UTF8.GetString(nulIndex >= 0 ? nameSpan[..nulIndex] : nameSpan);

                PhysicalDeviceMemoryProperties memProps;
                vk.GetPhysicalDeviceMemoryProperties(physicalDevice, &memProps);
                ulong heapSize = memProps.MemoryHeaps[0].Size;

                // 从 VkPhysicalDeviceProperties.limits 查询真实最大纹理尺寸
                int maxTextureSize = (int)props.Limits.MaxImageDimension2D;

                renderContext = new RenderContext(
                    GPUApiType.Vulkan,
                    new GpuDeviceCapabilities(devName, heapSize, 0, maxTextureSize, true, false, -1),
                    (IntPtr)(long)device.Handle,
                    device,
                    (IntPtr)(long)queue.Handle);
            }
            catch
            {
                // 统一清理——按创建逆序释放已分配的资源，杜绝泄漏
                if (device.Handle != 0)
                    vk.DestroyDevice(device, null);
                if (instance.Handle != 0)
                    vk.DestroyInstance(instance, null);
                vk.Dispose();
                throw;
            }

            // 全部成功——赋值给字段。
            // _vk 是无锁快路径的发布哨兵，必须【最后】赋值且用 Volatile.Write——
            // 旧代码 _vk 排在最前，线程 A 赋完 _vk、未赋 _renderContext 时，
            // 线程 B 走快路径见 _vk != null 直接返回 → Context 返回 null → NRE
            //（程序序问题，x86 也会中招；ARM 弱内存序还叠加发布重排风险）。
            _instance = instance;
            _physicalDevice = physicalDevice;
            _device = device;
            _queue = queue;
            _queueFamilyIndex = queueFamilyIndex;
            _khrSurface = khrSurface;
            _khrSwapchain = khrSwapchain;
            _khrWin32Surface = khrWin32Surface;
            _khrXlibSurface = khrXlibSurface;
            _khrWaylandSurface = khrWaylandSurface;
            _khrAndroidSurface = khrAndroidSurface;
            _renderContext = renderContext;
            System.Threading.Volatile.Write(ref _vk, vk);

            _logger.LogDebug("Vulkan 设备已创建（共享 Singleton）：{DeviceName}", devName);
        }
    }

    // 独立的图形队列族查找（供候选设备逐一评估复用）。
    private static unsafe uint FindGraphicsQueueFamily(Vk vk, PhysicalDevice device)
    {
        uint familyCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, null);
        if (familyCount == 0)
            return uint.MaxValue;

        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* pFamilies = families)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, pFamilies);
        }

        for (uint i = 0; i < familyCount; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                return i;
        }
        return uint.MaxValue;
    }

    // 先枚举实例实际支持的扩展再过滤——直接请求未支持的扩展会让
    // vkCreateInstance 整体失败（ErrorExtensionNotPresent），例如 Linux 无 X11 的
    // 纯 Wayland 环境请求 VK_KHR_xlib_surface 即全盘失败。
    private static unsafe string[] GetPlatformExtensions(Vk vk)
    {
        var availSet = new HashSet<string>(StringComparer.Ordinal);
        uint availCount = 0;
        if (vk.EnumerateInstanceExtensionProperties((byte*)null, &availCount, null) == Result.Success && availCount > 0)
        {
            var props = new ExtensionProperties[availCount];
            fixed (ExtensionProperties* pProps = props)
            {
                if (vk.EnumerateInstanceExtensionProperties((byte*)null, &availCount, pProps) == Result.Success)
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

        if (_vk is not null)
        {
            var vk = _vk;
            // 先 DeviceWaitIdle 确保 GPU 完成所有工作再释放
            if (_device.Handle != 0)
            {
                vk.DeviceWaitIdle(_device);
                vk.DestroyDevice(_device, null);
            }
            if (_instance.Handle != 0)
                vk.DestroyInstance(_instance, null);
            vk.Dispose();
            _vk = null;
        }

        _logger.LogDebug("Vulkan 设备已释放");
    }
}

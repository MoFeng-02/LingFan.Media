using System.Runtime.InteropServices;
using LingFan.Media.GPUShare.Vulkan;
using Silk.NET.Vulkan;

// ─────────────────────────────────────────────────────────────────────────────
// R2 里程碑 1 原型（2026-09-02）：自建 Vulkan instance + device，注入
// Avalonia.Vulkan.VulkanOptions.CustomSharedDevice，让 Avalonia 与视频管线共用同一 VkDevice。
// 验证目标：Avalonia 用我们的 device 能正常启动、UI 正常渲染（M1）。
// 后续里程碑：M2 同 device 建视频纹理 → M3 渲染流程内直绘 → M4 坏块验收（峰值 73/96 → <20/96）。
// 本文件属测试 App（验证后按正式落点迁移），全部 Android 托管 API + VulkanNative，无手写新 P/Invoke。
// ─────────────────────────────────────────────────────────────────────────────

namespace LingFan.Media.AvaloniaTools.Android;

/// <summary>Avalonia <c>IVulkanInstance</c> 适配器：包装我们自建的 VkInstance。</summary>
internal sealed class LingFanVulkanInstanceAdapter : global::Avalonia.Vulkan.IVulkanInstance
{
    private readonly nint _handle;
    private readonly string[] _extensions;

    public LingFanVulkanInstanceAdapter(nint handle, string[] extensions)
    {
        _handle = handle;
        _extensions = extensions;
    }

    public nint Handle => _handle;

    public IEnumerable<string> EnabledExtensions => _extensions;

    // 直接转发 VulkanNative 的 proc-addr 封装（与 Avalonia 内部语义一致：实例级/设备级分派）。
    public nint GetInstanceProcAddress(nint instance, string name) =>
        VulkanNative.GetInstanceProcAddress(instance, name);

    public nint GetDeviceProcAddress(nint device, string name) =>
        VulkanNative.GetDeviceProcAddress(device, name);

    public void Dispose()
    {
        // 生命周期与 App 进程一致；实例销毁须在 device 销毁之后，原型阶段交由进程退出回收。
    }

    public object? TryGetFeature(Type featureType) => null;
}

/// <summary>Avalonia <c>IVulkanDevice</c> 适配器：包装我们自建的 VkDevice / 主队列。</summary>
internal sealed class LingFanVulkanDeviceAdapter : global::Avalonia.Vulkan.IVulkanDevice
{
    private readonly object _sync = new();

    public LingFanVulkanDeviceAdapter(
        nint deviceHandle,
        nint physicalDeviceHandle,
        nint mainQueueHandle,
        uint graphicsQueueFamilyIndex,
        global::Avalonia.Vulkan.IVulkanInstance instance,
        string[] extensions)
    {
        Handle = deviceHandle;
        PhysicalDeviceHandle = physicalDeviceHandle;
        MainQueueHandle = mainQueueHandle;
        GraphicsQueueFamilyIndex = graphicsQueueFamilyIndex;
        Instance = instance;
        EnabledExtensions = extensions;
    }

    public nint Handle { get; }

    public nint PhysicalDeviceHandle { get; }

    public nint MainQueueHandle { get; }

    public uint GraphicsQueueFamilyIndex { get; }

    public global::Avalonia.Vulkan.IVulkanInstance Instance { get; }

    public bool IsLost => false; // 原型阶段：不做 device lost 恢复

    public IEnumerable<string> EnabledExtensions { get; }

    /// <summary>设备级锁：Avalonia 渲染线程与我们的管线线程经此串行化 VkDevice 访问。</summary>
    public IDisposable Lock()
    {
        if (System.Threading.Interlocked.Exchange(ref _lockProbed, 1) == 0)
            Console.WriteLine("[R2PROBE] [5] Lock() 被调用 ⇒ Avalonia 正在使用我们的 device（注入生效）。");
        System.Threading.Monitor.Enter(_sync);
        return new LockScope(_sync);
    }
    private int _lockProbed;

    public void Dispose()
    {
        // 生命周期与 App 进程一致；原型阶段不主动销毁。
    }

    public object? TryGetFeature(Type featureType) => null;

    private sealed class LockScope(object sync) : IDisposable
    {
        public void Dispose() => System.Threading.Monitor.Exit(sync);
    }
}

/// <summary>自建 Vulkan instance / physicalDevice / device / 主队列（复用 VulkanNative 引导能力）。</summary>
internal static class LingFanVulkanDeviceFactory
{
    private static readonly List<nint> _stringAllocs = new();

    public static unsafe (LingFanVulkanInstanceAdapter Instance, LingFanVulkanDeviceAdapter Device) Create()
    {
        Console.WriteLine("[R2PROBE] [0] LingFanVulkanDeviceFactory.Create 开始（自建 Vulkan instance/device）。");
        VulkanNative.InitBootstrap();

        // ── instance ──
        string[] instExt =
        [
            "VK_KHR_surface",
            "VK_KHR_android_surface",
            "VK_KHR_get_physical_device_properties2",
            "VK_KHR_external_memory_capabilities",
            "VK_KHR_external_semaphore_capabilities",
        ];
        InstanceCreateInfo ici = new()
        {
            SType = StructureType.InstanceCreateInfo,
            EnabledExtensionCount = (uint)instExt.Length,
            PpEnabledExtensionNames = AllocStringPtrArray(instExt),
        };
        Silk.NET.Vulkan.Result r = VulkanNative.CreateInstance(ref ici, null, out Instance instance);
        if (r != Silk.NET.Vulkan.Result.Success)
            throw new InvalidOperationException($"vkCreateInstance 失败: {r}");
        VulkanNative.InitInstance(instance);

        // ── 物理设备 ──
        uint physCount = 0;
        VulkanNative.EnumeratePhysicalDevices(instance, ref physCount, null);
        if (physCount == 0)
            throw new InvalidOperationException("无可用 Vulkan 物理设备。");
        var physicalDevices = new PhysicalDevice[physCount];
        unsafe
        {
            fixed (PhysicalDevice* pPhys = physicalDevices)
                VulkanNative.EnumeratePhysicalDevices(instance, ref physCount, pPhys);
        }

        // ── 图形队列族 ──
        uint family = 0;
        bool found = false;
        uint famCount = 0;
        VulkanNative.GetPhysicalDeviceQueueFamilyProperties(physicalDevices[0], ref famCount, null);
        var fams = new QueueFamilyProperties[famCount];
        unsafe
        {
            fixed (QueueFamilyProperties* pFam = fams)
                VulkanNative.GetPhysicalDeviceQueueFamilyProperties(physicalDevices[0], ref famCount, pFam);
        }
        for (uint i = 0; i < famCount; i++)
        {
            if ((fams[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
            {
                family = i;
                found = true;
                break;
            }
        }
        if (!found)
            throw new InvalidOperationException("物理设备无图形队列族。");

        // ── device ──（扩展首版清单：swapchain 是 Avalonia 呈现必需；其余为视频/外部内存/同步所需；
        //   缺什么由真机日志迭代补齐——Avalonia 初始化失败会明确指出缺口）
        List<string> devExt =
        [
            "VK_KHR_swapchain",
            "VK_KHR_external_memory",
            "VK_KHR_external_memory_fd",
            "VK_KHR_external_semaphore",
            "VK_KHR_external_semaphore_fd",
            "VK_KHR_get_memory_requirements2",
            "VK_KHR_bind_memory2",
            "VK_KHR_dedicated_allocation",
            "VK_KHR_sampler_ycbcr_conversion",
            "VK_KHR_maintenance1",
        ];
        // M4（2026-09-02）：AHB 零拷贝帧导入（VK_ANDROID_external_memory_android_hardware_buffer）。
        // GLES 桥接产出的 AHardwareBuffer 帧须经此扩展导入 Vulkan 采样；未启用时
        // vkGetAndroidHardwareBufferPropertiesANDROID 设备级函数不可解析（HasAndroidHardwareBufferProperties=false），
        // AHB 帧路径整体不可用（解码器自动回退 ByteBuffer CPU 档）。按物理设备支持条件启用，
        // 不硬塞以免 CreateDevice 失败。
        if (DeviceSupportsExtension(physicalDevices[0], "VK_ANDROID_external_memory_android_hardware_buffer"))
        {
            devExt.Add("VK_ANDROID_external_memory_android_hardware_buffer");
            Console.WriteLine("[R2PROBE] [7] 已启用 VK_ANDROID_external_memory_android_hardware_buffer（AHB 零拷贝帧导入就绪）。");
        }
        else
        {
            Console.WriteLine("[R2PROBE] [7] 物理设备不支持 AHB 外部内存扩展，AHB 零拷贝不可用（将回退 CPU 帧）。");
        }
        float queuePriority = 1.0f;
        DeviceQueueCreateInfo qci = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = family,
            QueueCount = 1,
            PQueuePriorities = &queuePriority,
        };
        DeviceCreateInfo dci = new()
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &qci,
            EnabledExtensionCount = (uint)devExt.Count,
            PpEnabledExtensionNames = AllocStringPtrArray(devExt.ToArray()),
        };
        r = VulkanNative.CreateDevice(physicalDevices[0], ref dci, null, out Device device);
        if (r != Silk.NET.Vulkan.Result.Success)
            throw new InvalidOperationException($"vkCreateDevice 失败: {r}");
        VulkanNative.InitDevice(device, samplerYcbcrFeatureEnabled: true);
        VulkanNative.GetDeviceQueue(device, family, 0, out Queue queue);

        Console.WriteLine("[R2PROBE] [0b] 自建完成：instance/device/队列已就绪，等待注入 Avalonia。");
        var instanceAdapter = new LingFanVulkanInstanceAdapter(instance.Handle, instExt);
        var deviceAdapter = new LingFanVulkanDeviceAdapter(
            device.Handle, physicalDevices[0].Handle, queue.Handle, family, instanceAdapter, devExt.ToArray());
        return (instanceAdapter, deviceAdapter);
    }

    /// <summary>枚举物理设备扩展并判断是否支持指定扩展名（UTF-8 逐字节比较）。</summary>
    private static unsafe bool DeviceSupportsExtension(PhysicalDevice dev, string name)
    {
        uint count = 0;
        if (VulkanNative.EnumerateDeviceExtensionProperties(dev, null, ref count, null) != Silk.NET.Vulkan.Result.Success
            || count == 0)
            return false;
        var props = new ExtensionProperties[count];
        fixed (ExtensionProperties* pProps = props)
        {
            if (VulkanNative.EnumerateDeviceExtensionProperties(dev, null, ref count, pProps) != Silk.NET.Vulkan.Result.Success)
                return false;
        }
        byte[] expected = System.Text.Encoding.UTF8.GetBytes(name);
        for (int i = 0; i < count; i++)
        {
            bool match = true;
            for (int j = 0; j < expected.Length; j++)
            {
                if (props[i].ExtensionName[j] != expected[j])
                {
                    match = false;
                    break;
                }
            }
            if (match && props[i].ExtensionName[expected.Length] == 0)
                return true;
        }
        return false;
    }

    /// <summary>分配 UTF8 字符串指针数组（生命周期与 App 一致，原型阶段不主动释放）。</summary>
    private static unsafe byte** AllocStringPtrArray(string[] items)
    {
        nint block = Marshal.AllocHGlobal(sizeof(nint) * items.Length);
        _stringAllocs.Add(block);
        for (int i = 0; i < items.Length; i++)
        {
            nint strPtr = Marshal.StringToCoTaskMemUTF8(items[i]);
            _stringAllocs.Add(strPtr);
            ((nint*)block)[i] = strPtr;
        }
        return (byte**)block;
    }
}

/// <summary>
/// R2 注入通道（2026-09-02）：Application（Android 入口）创建适配器后存入此处；
/// 共享 App 构建 DI 后取 VulkanRendererFactory 调 UseExternalDevice 注入。
/// 生命周期：App 级静态（与进程一致），适配器由本类保活防 GC 悬空。
/// </summary>
internal sealed class LingFanVulkanBootstrap :
    global::LingFan.Media.GPUShare.Vulkan.IVulkanSharedDeviceProvider
{
    public static LingFanVulkanInstanceAdapter? InstanceAdapter { get; private set; }
    public static LingFanVulkanDeviceAdapter? DeviceAdapter { get; private set; }

    public static LingFanVulkanBootstrap Instance { get; } = new();

    public static void Initialize()
    {
        if (DeviceAdapter is not null)
            return;
        (InstanceAdapter, DeviceAdapter) = LingFanVulkanDeviceFactory.Create();
    }

    public global::LingFan.Media.GPUShare.Vulkan.VulkanSharedDeviceHandles GetSharedDevice() =>
        new(InstanceAdapter!.Handle, DeviceAdapter!.PhysicalDeviceHandle,
            DeviceAdapter.Handle, DeviceAdapter.GraphicsQueueFamilyIndex);
}

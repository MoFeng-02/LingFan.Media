using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Silk.NET.Vulkan;

namespace LingFan.Media.Avalonia.Android;

/// <summary>
/// 自建 Vulkan instance / physicalDevice / device / 主队列（复用 <see cref="LingFan.Media.GPUShare.Vulkan.VulkanNative"/> 引导能力）。
/// </summary>
/// <remarks>
/// <para>存在理由：Android 上 Avalonia 与视频管线必须共用同一 VkDevice —— Avalonia 只支持经
/// <c>VulkanOptions.CustomSharedDevice</c> 在 AppBuilder 阶段注入外部 device（之后无法补注），
/// 因此 device 的创建必须前置于 Avalonia 初始化，由本工厂完成。</para>
/// <para>共用 device 后，共享表面源的 dma_buf fd 导入从「跨实例」变为「同 device」，
/// 规避 Adreno 等驱动对跨实例导入的兼容缺陷（<c>vkAllocateMemory</c> 返回
/// <c>ErrorInitializationFailed</c>）。</para>
/// </remarks>
[SupportedOSPlatform("android23.0")]
public static unsafe class VulkanDeviceFactory
{
    private static readonly List<nint> _stringAllocs = [];

    /// <summary>创建 Vulkan instance 与 device（含图形队列），包装为 Avalonia 适配器。</summary>
    /// <exception cref="InvalidOperationException">Vulkan 初始化任一步失败。</exception>
    public static (AvaloniaVulkanInstanceAdapter Instance, AvaloniaVulkanDeviceAdapter Device) Create()
    {
        LingFan.Media.GPUShare.Vulkan.VulkanNative.InitBootstrap();

        // instance 扩展：surface 呈现 + 物理设备/外部内存能力枚举。
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
        Silk.NET.Vulkan.Result r = LingFan.Media.GPUShare.Vulkan.VulkanNative.CreateInstance(ref ici, null, out Instance instance);
        if (r != Silk.NET.Vulkan.Result.Success)
            throw new InvalidOperationException($"vkCreateInstance 失败: {r}");
        LingFan.Media.GPUShare.Vulkan.VulkanNative.InitInstance(instance);

        // 物理设备（取首个——Android 单 GPU 设备）。
        uint physCount = 0;
        LingFan.Media.GPUShare.Vulkan.VulkanNative.EnumeratePhysicalDevices(instance, ref physCount, null);
        if (physCount == 0)
            throw new InvalidOperationException("无可用 Vulkan 物理设备。");
        var physicalDevices = new PhysicalDevice[physCount];
        fixed (PhysicalDevice* pPhys = physicalDevices)
            LingFan.Media.GPUShare.Vulkan.VulkanNative.EnumeratePhysicalDevices(instance, ref physCount, pPhys);

        // 图形队列族。
        uint family = 0;
        bool found = false;
        uint famCount = 0;
        LingFan.Media.GPUShare.Vulkan.VulkanNative.GetPhysicalDeviceQueueFamilyProperties(physicalDevices[0], ref famCount, null);
        var fams = new QueueFamilyProperties[famCount];
        fixed (QueueFamilyProperties* pFam = fams)
            LingFan.Media.GPUShare.Vulkan.VulkanNative.GetPhysicalDeviceQueueFamilyProperties(physicalDevices[0], ref famCount, pFam);
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

        // device 扩展：swapchain 是 Avalonia 呈现必需；其余为外部内存/同步/采样所需。
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
        // AHB 零拷贝帧导入：GLES 桥接产出的 AHardwareBuffer 帧须经此扩展导入 Vulkan 采样；
        // 未启用时 vkGetAndroidHardwareBufferPropertiesANDROID 不可解析，AHB 帧路径整体不可用
        //（解码器自动回退 ByteBuffer CPU 档）。按物理设备支持条件启用，不硬塞以免 CreateDevice 失败。
        if (DeviceSupportsExtension(physicalDevices[0], "VK_ANDROID_external_memory_android_hardware_buffer"))
        {
            devExt.Add("VK_ANDROID_external_memory_android_hardware_buffer");
            Console.WriteLine("[ANDROID-VULKAN] 已启用 VK_ANDROID_external_memory_android_hardware_buffer（AHB 零拷贝帧导入就绪）。");
        }
        else
        {
            Console.WriteLine("[ANDROID-VULKAN] 物理设备不支持 AHB 外部内存扩展，AHB 零拷贝不可用（解码器自动回退 CPU 帧）。");
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
            PpEnabledExtensionNames = AllocStringPtrArray([.. devExt]),
        };
        r = LingFan.Media.GPUShare.Vulkan.VulkanNative.CreateDevice(physicalDevices[0], ref dci, null, out Device device);
        if (r != Silk.NET.Vulkan.Result.Success)
            throw new InvalidOperationException($"vkCreateDevice 失败: {r}");
        LingFan.Media.GPUShare.Vulkan.VulkanNative.InitDevice(device, samplerYcbcrFeatureEnabled: true);
        LingFan.Media.GPUShare.Vulkan.VulkanNative.GetDeviceQueue(device, family, 0, out Queue queue);

        var instanceAdapter = new AvaloniaVulkanInstanceAdapter(instance.Handle, instExt);
        var deviceAdapter = new AvaloniaVulkanDeviceAdapter(
            device.Handle, physicalDevices[0].Handle, queue.Handle, family, instanceAdapter, [.. devExt]);
        return (instanceAdapter, deviceAdapter);
    }

    /// <summary>枚举物理设备扩展并判断是否支持指定扩展名（UTF-8 逐字节比较）。</summary>
    private static bool DeviceSupportsExtension(PhysicalDevice dev, string name)
    {
        uint count = 0;
        if (LingFan.Media.GPUShare.Vulkan.VulkanNative.EnumerateDeviceExtensionProperties(dev, null, ref count, null) != Silk.NET.Vulkan.Result.Success
            || count == 0)
            return false;
        var props = new ExtensionProperties[count];
        fixed (ExtensionProperties* pProps = props)
        {
            if (LingFan.Media.GPUShare.Vulkan.VulkanNative.EnumerateDeviceExtensionProperties(dev, null, ref count, pProps) != Silk.NET.Vulkan.Result.Success)
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

    /// <summary>分配 UTF8 字符串指针数组（生命周期与 App 进程一致，进程退出统一回收）。</summary>
    private static byte** AllocStringPtrArray(string[] items)
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

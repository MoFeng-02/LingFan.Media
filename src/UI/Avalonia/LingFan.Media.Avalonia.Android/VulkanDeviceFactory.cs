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

        // instance 扩展：surface 呈现 + 物理设备/外部内存能力枚举；提升函数 KHR 来源扩展
        // 按实例层可用性条件补齐（严格 loader/驱动上提升函数经别名解析需要来源扩展）。
        List<string> instExt =
        [
            "VK_KHR_surface",
            "VK_KHR_android_surface",
            "VK_KHR_get_physical_device_properties2",
            "VK_KHR_external_memory_capabilities",
            "VK_KHR_external_semaphore_capabilities",
        ];
        var availableInstanceExts = LingFan.Media.GPUShare.Vulkan.VulkanNative.EnumerateInstanceExtensionNames();
        foreach (var ext in LingFan.Media.GPUShare.Vulkan.VulkanNative.PromotedKhrInstanceExtensions)
            if (!instExt.Contains(ext) && availableInstanceExts.Contains(ext))
                instExt.Add(ext);
        string[] instExtArr = [.. instExt];

        // 声明实例 apiVersion=1.1：未设 ApplicationInfo 时实例被 loader 视为 1.0，使用 1.1 提升
        // 扩展/函数属规范违规。部分严格 loader/驱动对提升函数核心名派发的拒绝独立于此声明，
        // KHR 别名回退（见 VulkanNative 解析层）才是兼容主通道；本声明合规且无害。
        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            ApiVersion = (1u << 22) | (1u << 12), // VK_API_VERSION_1_1（major<<22 | minor<<12 | patch）
        };
        InstanceCreateInfo ici = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)instExtArr.Length,
            PpEnabledExtensionNames = AllocStringPtrArray(instExtArr),
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
        // foreign 队列族扩展：VK_ANDROID_external_memory_android_hardware_buffer 的规范依赖
        //（MediaCodec/GLES 持有 AHardwareBuffer 期间图像处于 VK_QUEUE_FAMILY_FOREIGN_EXT 队列族，
        // 相关所有权转移须启用本扩展才符合规范）。Avalonia 官方 Android Vulkan 契约亦要求
        // {AHB, foreign} 成对启用；宽松驱动缺它也放行，严格驱动按规范校验，缺失可致 AHB 导入失效。
        // 按物理设备支持条件启用，不硬塞以免 CreateDevice 失败。
        bool foreignSupported = DeviceSupportsExtension(physicalDevices[0], "VK_EXT_queue_family_foreign");
        if (foreignSupported)
            devExt.Add("VK_EXT_queue_family_foreign");

        // AHB 零拷贝帧导入：GLES 桥接产出的 AHardwareBuffer 帧须经此扩展导入 Vulkan 采样；
        // 未启用时 vkGetAndroidHardwareBufferPropertiesANDROID 不可解析，AHB 帧路径整体不可用
        //（解码器自动回退 ByteBuffer CPU 档）。按物理设备支持条件启用，不硬塞以免 CreateDevice 失败。
        bool ahbSupported = DeviceSupportsExtension(physicalDevices[0], "VK_ANDROID_external_memory_android_hardware_buffer");
        if (ahbSupported)
            devExt.Add("VK_ANDROID_external_memory_android_hardware_buffer");
        if (!ahbSupported)
            Console.WriteLine("[ANDROID-VULKAN] 物理设备不支持 AHB 外部内存扩展，AHB 零拷贝不可用（解码器自动回退 CPU 帧）。");
        else if (!foreignSupported)
            Console.WriteLine("[ANDROID-VULKAN] 物理设备不支持 foreign 队列族扩展（AHB 规范依赖），AHB 导入在此驱动上可能失效。");

        // 1.1/1.2/1.3 提升函数的 KHR 来源扩展：按物理设备支持条件启用——严格 loader/驱动上
        // 提升函数经别名解析（见 VulkanNative 解析层）需要来源扩展处于启用态。
        foreach (var ext in LingFan.Media.GPUShare.Vulkan.VulkanNative.PromotedKhrDeviceExtensions)
            if (!devExt.Contains(ext) && DeviceSupportsExtension(physicalDevices[0], ext))
                devExt.Add(ext);

        Console.WriteLine($"[ANDROID-VULKAN] device 扩展绑定清单：{string.Join(", ", devExt)}");

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

        // 派发探测：1.1 核心函数能否经本 device 解析（含 KHR 别名回退），是 Skia GrContext
        // 创建成败的直接前兆。NULL 表示核心名与别名双路均被 loader/驱动拒绝。
        nint probe11 = LingFan.Media.GPUShare.Vulkan.VulkanNative.GetDeviceProcAddress(
            device.Handle, "vkGetImageMemoryRequirements2");
        Console.WriteLine($"[ANDROID-VULKAN] instance apiVersion=1.1（ApplicationInfo）；" +
            $"1.1 核心函数派发探测（含 KHR 别名回退，vkGetImageMemoryRequirements2）={(probe11 != 0 ? "可解析" : "NULL")}");

        var instanceAdapter = new AvaloniaVulkanInstanceAdapter(instance.Handle, instExtArr);
        var deviceAdapter = new AvaloniaVulkanDeviceAdapter(
            device.Handle, physicalDevices[0].Handle, queue.Handle, family, instanceAdapter, [.. devExt]);
        // Avalonia 的 Skia getProc 兜底会以 NULL 实例解析设备级函数（Android loader 非法），
        // 适配器以本应用唯一共享 device 二次解析（见 AvaloniaVulkanInstanceAdapter）。
        instanceAdapter.SetDeviceFallback(device.Handle);
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

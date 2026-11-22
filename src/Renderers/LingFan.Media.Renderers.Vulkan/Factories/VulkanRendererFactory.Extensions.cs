using LingFan.Media.Abstractions;
using LingFan.Media.GPUShare.D3D11;
using LingFan.Media.Renderers.Shared;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanRendererFactory 的扩展枚举职责段（部分类，按职责拆分，编译期等价）。
// 设备/实例扩展按平台与实际支持过滤（直接请求未支持的扩展会让 vkCreateDevice/vkCreateInstance 整体失败）；
// 设备创建编排在 Device 段，工厂入口与字段在主文件。
public sealed unsafe partial class VulkanRendererFactory
{
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
            // VK_EXT_queue_family_foreign 是 AHB 扩展的规范依赖（外部持有 AHardwareBuffer 期间
            // 图像处于 foreign 队列族），须与 AHB 成对按可用性启用。
            if (OperatingSystem.IsAndroid())
            {
                AddIfAvail("VK_EXT_queue_family_foreign");
                AddIfAvail("VK_ANDROID_external_memory_android_hardware_buffer");
                // 1.1/1.2/1.3 提升函数的 KHR 来源扩展：按可用性启用（严格 loader/驱动上提升函数
                // 经 KHR 别名解析需要来源扩展；VulkanNative 解析层统一回退）。
                foreach (var ext in VulkanNative.PromotedKhrDeviceExtensions)
                    AddIfAvail(ext);
            }
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
        // 外部内存/信号量能力查询扩展（VK_KHR_external_memory_capabilities /
        // external_semaphore_capabilities）：VK_KHR_external_memory_fd（OPAQUE_FD）设备扩展
        // 正确运作的前置依赖——Avalonia 合成器在实例级强制启用了它们。缺失时部分驱动
        // （如 Android Adreno）会在绑定外部图像时返回 ErrorInvalidExternalHandle，导致 OPAQUE_FD
        // 导出链路整条塌掉、回退 Skia。Linux/Android 经 AddIfAvailable 启用（Vulkan 1.1+ 多为 core，
        // 枚举不到则静默跳过，零影响）。
        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
        {
            AddIfAvailable("VK_KHR_external_memory_capabilities");
            AddIfAvailable("VK_KHR_external_semaphore_capabilities");
        }
        // B4 Vulkan Video 硬解：实例级启用 VK_KHR_video_queue。
        // 该扩展虽分类为 device extension，但 vkGetPhysicalDeviceVideoCapabilitiesKHR /
        // vkGetPhysicalDeviceVideoFormatPropertiesKHR 是【实例级分派函数】（首参 VkPhysicalDevice），
        // 规范要求其所在扩展在实例启用后方可正确调用（FFmpeg hwcontext_vulkan 亦在设备级启用，双保险）。
        // 条件式过滤——缺失则静默跳过，不会令 vkCreateInstance 因 ErrorExtensionNotPresent 整体失败。
        // 注：真正的 VU 硬约束在 VulkanVideoDecoder.CreateVideoSession 能力查询处——
        // pCapabilities 的 pNext 链必须挂 VkVideoDecodeCapabilitiesKHR + VkVideoDecodeH264CapabilitiesKHR
        // （VU 07183/07184），缺失则返回 VK_ERROR_INITIALIZATION_FAILED（此前崩溃的直接原因）。
        AddIfAvailable("VK_KHR_video_queue");
        return exts.ToArray();
    }

    private static uint MakeVersion(uint major, uint minor, uint patch)
        => (major << 22) | (minor << 12) | patch;
}
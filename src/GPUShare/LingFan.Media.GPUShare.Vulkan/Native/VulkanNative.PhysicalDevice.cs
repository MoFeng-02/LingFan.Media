using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 PhysicalDevice 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
    /// <summary>枚举实例下的物理设备（vkEnumeratePhysicalDevices；两段式：先取数量再取列表）。</summary>
    public static unsafe Result EnumeratePhysicalDevices(Instance instance, ref uint pPhysicalDeviceCount, PhysicalDevice* pPhysicalDevices)
    {
        fixed (uint* p = &pPhysicalDeviceCount)
            return _enumeratePhysicalDevices(instance, p, pPhysicalDevices);
    }

    /// <summary>查询物理设备基础属性（vkGetPhysicalDeviceProperties：名称/类型/vendorID/限制）。</summary>
    public static unsafe void GetPhysicalDeviceProperties(PhysicalDevice physicalDevice, PhysicalDeviceProperties* pProperties)
        => _getPhysicalDeviceProperties(physicalDevice, pProperties);

    /// <summary>查询物理设备内存属性（vkGetPhysicalDeviceMemoryProperties：堆与内存类型，供 FindMemoryType 匹配）。</summary>
    public static unsafe void GetPhysicalDeviceMemoryProperties(PhysicalDevice physicalDevice, PhysicalDeviceMemoryProperties* pMemoryProperties)
        => _getPhysicalDeviceMemoryProperties(physicalDevice, pMemoryProperties);

    /// <summary>查询物理设备队列族属性（vkGetPhysicalDeviceQueueFamilyProperties；两段式）。</summary>
    public static unsafe void GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice physicalDevice, ref uint pQueueFamilyPropertyCount, QueueFamilyProperties* pQueueFamilyProperties)
    {
        fixed (uint* p = &pQueueFamilyPropertyCount)
            _getPhysicalDeviceQueueFamilyProperties(physicalDevice, p, pQueueFamilyProperties);
    }

    /// <summary>
    /// 查询物理设备特性（Vulkan 1.1 core 实例级函数；<paramref name="pFeatures"/> 的 pNext 可链
    /// VkPhysicalDeviceSamplerYcbcrConversionFeatures 等扩展特性结构）。
    /// </summary>
    public static unsafe Result GetPhysicalDeviceFeatures2(PhysicalDevice physicalDevice, PhysicalDeviceFeatures2* pFeatures)
    {
        if (_getPhysicalDeviceFeatures2 == null)
            throw new InvalidOperationException("vkGetPhysicalDeviceFeatures2 不可用（Vulkan 1.0 设备且无 KHR 扩展）。");
        return _getPhysicalDeviceFeatures2(physicalDevice, pFeatures);
    }

    /// <summary>查询物理设备属性（Vulkan 1.1 core；pNext 可链 UUID/LUID 等扩展属性结构；未解析时抛异常）。</summary>
    public static unsafe Result GetPhysicalDeviceProperties2(PhysicalDevice physicalDevice, PhysicalDeviceProperties2* pProperties2)
    {
        if (_getPhysicalDeviceProperties2 == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetPhysicalDeviceProperties2（请确认 Vulkan 1.1+ 可用）。");
        return _getPhysicalDeviceProperties2(physicalDevice, pProperties2);
    }
}

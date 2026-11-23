using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Device 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
    /// <summary>在物理设备上创建逻辑设备（vkCreateDevice；队列/扩展经 DeviceCreateInfo 声明）。</summary>
    public static unsafe Result CreateDevice(PhysicalDevice physicalDevice, ref DeviceCreateInfo pCreateInfo, AllocationCallbacks* pAllocator, out Device pDevice)
    {
        fixed (DeviceCreateInfo* p = &pCreateInfo)
        {
            Device tmp;
            var r = _createDevice(physicalDevice, p, pAllocator, &tmp);
            pDevice = tmp;
            return r;
        }
    }

    /// <summary>销毁逻辑设备（vkDestroyDevice；调用方须先确保设备空闲）。</summary>
    public static unsafe void DestroyDevice(Device device, AllocationCallbacks* pAllocator) => _destroyDevice(device, pAllocator);

    /// <summary>取设备队列句柄（vkGetDeviceQueue；队列族/索引须已在创建时声明，队列随设备销毁）。</summary>
    public static unsafe void GetDeviceQueue(Device device, uint queueFamilyIndex, uint queueIndex, out Queue pQueue)
    {
        Queue tmp;
        _getDeviceQueue(device, queueFamilyIndex, queueIndex, &tmp);
        pQueue = tmp;
    }

    /// <summary>阻塞等待设备全部队列空闲（vkDeviceWaitIdle；重量级同步点）。</summary>
    public static unsafe Result DeviceWaitIdle(Device device) => _deviceWaitIdle(device);

    /// <summary>枚举物理设备支持的扩展属性（vkEnumerateDeviceExtensionProperties；未解析时抛异常）。</summary>
    public static unsafe Result EnumerateDeviceExtensionProperties(PhysicalDevice physicalDevice, byte* pLayerName, ref uint pPropertyCount, ExtensionProperties* pProperties)
    {
        if (_enumerateDeviceExtensionProperties == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkEnumerateDeviceExtensionProperties。");
        fixed (uint* p = &pPropertyCount)
            return _enumerateDeviceExtensionProperties(physicalDevice, pLayerName, p, pProperties);
    }

    // 外部内存/信号量导出（no-airspace 共享表面源）
}

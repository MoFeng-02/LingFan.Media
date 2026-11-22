using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Device 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
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

    public static unsafe void DestroyDevice(Device device, AllocationCallbacks* pAllocator) => _destroyDevice(device, pAllocator);

    public static unsafe void GetDeviceQueue(Device device, uint queueFamilyIndex, uint queueIndex, out Queue pQueue)
    {
        Queue tmp;
        _getDeviceQueue(device, queueFamilyIndex, queueIndex, &tmp);
        pQueue = tmp;
    }

    public static unsafe Result DeviceWaitIdle(Device device) => _deviceWaitIdle(device);

    public static unsafe Result EnumerateDeviceExtensionProperties(PhysicalDevice physicalDevice, byte* pLayerName, ref uint pPropertyCount, ExtensionProperties* pProperties)
    {
        if (_enumerateDeviceExtensionProperties == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkEnumerateDeviceExtensionProperties。");
        fixed (uint* p = &pPropertyCount)
            return _enumerateDeviceExtensionProperties(physicalDevice, pLayerName, p, pProperties);
    }

    // 外部内存/信号量导出（no-airspace 共享表面源）
}

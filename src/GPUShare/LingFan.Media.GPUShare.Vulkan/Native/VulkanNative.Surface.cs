using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Surface 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
    public static unsafe Result CreateWin32SurfaceKHR(Instance instance, ref Win32SurfaceCreateInfoKHR pCreateInfo, AllocationCallbacks* pAllocator, out SurfaceKHR pSurface)
    {
        fixed (Win32SurfaceCreateInfoKHR* p = &pCreateInfo)
        {
            SurfaceKHR tmp;
            var r = _createWin32SurfaceKHR(instance, p, pAllocator, &tmp);
            pSurface = tmp;
            return r;
        }
    }

    public static unsafe Result CreateAndroidSurfaceKHR(Instance instance, ref AndroidSurfaceCreateInfoKHR pCreateInfo, AllocationCallbacks* pAllocator, out SurfaceKHR pSurface)
    {
        fixed (AndroidSurfaceCreateInfoKHR* p = &pCreateInfo)
        {
            SurfaceKHR tmp;
            var r = _createAndroidSurfaceKHR(instance, p, pAllocator, &tmp);
            pSurface = tmp;
            return r;
        }
    }

    // VK_EXT_metal_surface（Apple / MoltenVK）：Silk.NET 静态包装不含 vkCreateMetalSurfaceEXT，
    //    经 vkGetInstanceProcAddr 运行时解析（见 InitInstance）。PLayer 指向宿主提供的 CAMetalLayer*。

    public static unsafe Result CreateMetalSurfaceEXT(Instance instance, ref MetalSurfaceCreateInfoEXT pCreateInfo, AllocationCallbacks* pAllocator, out SurfaceKHR pSurface)
    {
        if (_createMetalSurfaceEXT == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCreateMetalSurfaceEXT（请确认已在 Apple 平台启用 VK_EXT_metal_surface 扩展）。");
        fixed (MetalSurfaceCreateInfoEXT* p = &pCreateInfo)
        {
            SurfaceKHR tmp;
            var r = _createMetalSurfaceEXT(instance, p, pAllocator, &tmp);
            pSurface = tmp;
            return r;
        }
    }

    // KHR WSI Linux 扩展（X11 / Wayland）

    public static unsafe Result CreateXlibSurfaceKHR(Instance instance, ref XlibSurfaceCreateInfoKHR pCreateInfo, AllocationCallbacks* pAllocator, out SurfaceKHR pSurface)
    {
        if (_createXlibSurfaceKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCreateXlibSurfaceKHR（请确认已启用 VK_KHR_xlib_surface 扩展）。");
        fixed (XlibSurfaceCreateInfoKHR* p = &pCreateInfo)
        {
            SurfaceKHR tmp;
            var r = _createXlibSurfaceKHR(instance, p, pAllocator, &tmp);
            pSurface = tmp;
            return r;
        }
    }

    public static unsafe Result CreateWaylandSurfaceKHR(Instance instance, ref WaylandSurfaceCreateInfoKHR pCreateInfo, AllocationCallbacks* pAllocator, out SurfaceKHR pSurface)
    {
        if (_createWaylandSurfaceKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCreateWaylandSurfaceKHR（请确认已启用 VK_KHR_wayland_surface 扩展）。");
        fixed (WaylandSurfaceCreateInfoKHR* p = &pCreateInfo)
        {
            SurfaceKHR tmp;
            var r = _createWaylandSurfaceKHR(instance, p, pAllocator, &tmp);
            pSurface = tmp;
            return r;
        }
    }

    public static unsafe void DestroySurfaceKHR(Instance instance, SurfaceKHR surface, AllocationCallbacks* pAllocator)
        => _destroySurfaceKHR(instance, surface, pAllocator);

    public static unsafe Result GetPhysicalDeviceSurfaceCapabilitiesKHR(PhysicalDevice physicalDevice, SurfaceKHR surface, SurfaceCapabilitiesKHR[] pSurfaceCapabilities)
    {
        fixed (SurfaceCapabilitiesKHR* p = pSurfaceCapabilities)
            return _getPhysicalDeviceSurfaceCapabilitiesKHR(physicalDevice, surface, p);
    }

    public static unsafe Result GetPhysicalDeviceSurfaceFormatsKHR(PhysicalDevice physicalDevice, SurfaceKHR surface, ref uint pSurfaceFormatCount, SurfaceFormatKHR[] pSurfaceFormats)
    {
        fixed (uint* p = &pSurfaceFormatCount)
        fixed (SurfaceFormatKHR* f = pSurfaceFormats)
            return _getPhysicalDeviceSurfaceFormatsKHR(physicalDevice, surface, p, f);
    }

    public static unsafe Result GetPhysicalDeviceSurfaceFormatsKHR(PhysicalDevice physicalDevice, SurfaceKHR surface, ref uint pSurfaceFormatCount, SurfaceFormatKHR* pSurfaceFormats)
    {
        fixed (uint* p = &pSurfaceFormatCount)
            return _getPhysicalDeviceSurfaceFormatsKHR(physicalDevice, surface, p, pSurfaceFormats);
    }
}

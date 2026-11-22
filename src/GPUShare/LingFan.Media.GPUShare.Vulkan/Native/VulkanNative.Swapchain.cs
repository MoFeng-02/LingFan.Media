using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Swapchain 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
    public static unsafe Result CreateSwapchainKHR(Device device, ref SwapchainCreateInfoKHR pCreateInfo, AllocationCallbacks* pAllocator, out SwapchainKHR pSwapchain)
    {
        fixed (SwapchainCreateInfoKHR* p = &pCreateInfo)
        {
            SwapchainKHR tmp;
            var r = _createSwapchainKHR(device, p, pAllocator, &tmp);
            pSwapchain = tmp;
            return r;
        }
    }

    public static unsafe void DestroySwapchainKHR(Device device, SwapchainKHR swapchain, AllocationCallbacks* pAllocator)
        => _destroySwapchainKHR(device, swapchain, pAllocator);

    public static unsafe Result QueuePresentKHR(Queue queue, Span<PresentInfoKHR> pPresentInfo)
    {
        fixed (PresentInfoKHR* p = pPresentInfo)
            return _queuePresentKHR(queue, p);
    }

    // 物理设备能力查询（UUID/LUID 对齐、设备扩展枚举）

    public static unsafe Result GetSwapchainImagesKHR(Device device, SwapchainKHR swapchain, ref uint pSwapchainImageCount, Image[] pSwapchainImages)
    {
        fixed (uint* p = &pSwapchainImageCount)
        fixed (Image* imgs = pSwapchainImages)
            return _getSwapchainImagesKHR(device, swapchain, p, imgs);
    }

    public static unsafe Result GetSwapchainImagesKHR(Device device, SwapchainKHR swapchain, ref uint pSwapchainImageCount, Image* pSwapchainImages)
    {
        fixed (uint* p = &pSwapchainImageCount)
            return _getSwapchainImagesKHR(device, swapchain, p, pSwapchainImages);
    }

    public static unsafe Result AcquireNextImageKHR(Device device, SwapchainKHR swapchain, ulong timeout, Semaphore semaphore, Fence fence, Span<uint> pImageIndex)
    {
        fixed (uint* p = pImageIndex)
            return _acquireNextImageKHR(device, swapchain, timeout, semaphore, fence, p);
    }
}

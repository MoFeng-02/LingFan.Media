using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Swapchain 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
    /// <summary>创建交换链（vkCreateSwapchainKHR；须已启用 VK_KHR_swapchain 且表面能力匹配）。</summary>
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

    /// <summary>销毁交换链（vkDestroySwapchainKHR；其图像随链销毁，不得单独销毁）。</summary>
    public static unsafe void DestroySwapchainKHR(Device device, SwapchainKHR swapchain, AllocationCallbacks* pAllocator)
        => _destroySwapchainKHR(device, swapchain, pAllocator);

    /// <summary>把交换链图像提交呈现（vkQueuePresentKHR；等待 pPresentInfo 中的信号量后上屏）。</summary>
    public static unsafe Result QueuePresentKHR(Queue queue, Span<PresentInfoKHR> pPresentInfo)
    {
        fixed (PresentInfoKHR* p = pPresentInfo)
            return _queuePresentKHR(queue, p);
    }

    // 物理设备能力查询（UUID/LUID 对齐、设备扩展枚举）

    /// <summary>取交换链图像数组（vkGetSwapchainImagesKHR 数组重载；两段式：先取数量再取列表）。</summary>
    public static unsafe Result GetSwapchainImagesKHR(Device device, SwapchainKHR swapchain, ref uint pSwapchainImageCount, Image[] pSwapchainImages)
    {
        fixed (uint* p = &pSwapchainImageCount)
        fixed (Image* imgs = pSwapchainImages)
            return _getSwapchainImagesKHR(device, swapchain, p, imgs);
    }

    /// <summary>取交换链图像（vkGetSwapchainImagesKHR 指针重载；pSwapchainImages 为 null 时仅回写数量）。</summary>
    public static unsafe Result GetSwapchainImagesKHR(Device device, SwapchainKHR swapchain, ref uint pSwapchainImageCount, Image* pSwapchainImages)
    {
        fixed (uint* p = &pSwapchainImageCount)
            return _getSwapchainImagesKHR(device, swapchain, p, pSwapchainImages);
    }

    /// <summary>获取下一张可渲染交换链图像索引（vkAcquireNextImageKHR；timeout 纳秒，完成时 signal 给定信号量/围栏）。</summary>
    public static unsafe Result AcquireNextImageKHR(Device device, SwapchainKHR swapchain, ulong timeout, Semaphore semaphore, Fence fence, Span<uint> pImageIndex)
    {
        fixed (uint* p = pImageIndex)
            return _acquireNextImageKHR(device, swapchain, timeout, semaphore, fence, p);
    }
}

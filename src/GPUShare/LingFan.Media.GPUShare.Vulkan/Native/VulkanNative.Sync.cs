using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Sync 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
    public static unsafe Result CreateSemaphore(Device device, ref SemaphoreCreateInfo pCreateInfo, AllocationCallbacks* pAllocator, out Semaphore pSemaphore)
    {
        fixed (SemaphoreCreateInfo* p = &pCreateInfo)
        {
            Semaphore tmp;
            var r = _createSemaphore(device, p, pAllocator, &tmp);
            pSemaphore = tmp;
            return r;
        }
    }

    public static unsafe void DestroySemaphore(Device device, Semaphore semaphore, AllocationCallbacks* pAllocator)
        => _destroySemaphore(device, semaphore, pAllocator);

    public static unsafe Result CreateFence(Device device, FenceCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out Fence pFence)
    {
        Fence tmp;
        var r = _createFence(device, pCreateInfo, pAllocator, &tmp);
        pFence = tmp;
        return r;
    }

    public static unsafe void DestroyFence(Device device, Fence fence, AllocationCallbacks* pAllocator)
        => _destroyFence(device, fence, pAllocator);

    public static unsafe Result WaitForFences(Device device, uint fenceCount, Fence* pFences, uint waitAll, ulong timeout)
        => _waitForFences(device, fenceCount, pFences, waitAll, timeout);

    public static unsafe Result ResetFences(Device device, uint fenceCount, Fence* pFences)
        => _resetFences(device, fenceCount, pFences);

    /// <summary>把 VkSemaphore 导出为 Windows HANDLE（外部信号量 NT 句柄）。</summary>
    public static unsafe Result GetSemaphoreWin32HandleKHR(Device device, SemaphoreGetWin32HandleInfoKHR* pInfo, out nint handle)
    {
        if (_getSemaphoreWin32HandleKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetSemaphoreWin32HandleKHR（请确认已启用 VK_KHR_external_semaphore_win32）。");
        void* h;
        Result r = _getSemaphoreWin32HandleKHR(device, pInfo, &h);
        handle = (nint)h;
        return r;
    }

    /// <summary>把 VkSemaphore 导出为 POSIX 文件描述符（外部信号量 fd 句柄）。</summary>
    public static unsafe Result GetSemaphoreFdKHR(Device device, SemaphoreGetFdInfoKHR* pInfo, out int fd)
    {
        if (_getSemaphoreFdKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetSemaphoreFdKHR（请确认已启用 VK_KHR_external_semaphore_fd）。");
        int f;
        Result r = _getSemaphoreFdKHR(device, pInfo, &f);
        fd = f;
        return r;
    }

    // UTF-8 字符串 / 字符串数组 marshalling（替代 SilkMarshal.StringToPtr / StringArrayToPtr / Free）
}

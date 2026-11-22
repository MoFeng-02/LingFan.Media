using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Memory 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
    public static unsafe void GetImageMemoryRequirements(Device device, Image image, MemoryRequirements* pMemoryRequirements)
        => _getImageMemoryRequirements(device, image, pMemoryRequirements);

    /// <summary>
    /// 外部内存导入纹理的需求查询必须用 v2（带回 <see cref="MemoryDedicatedRequirements"/>）取权威 size / memoryTypeBits。
    /// Vulkan 1.1+ 为 core；若设备未提供（1.0 且未启用 VK_KHR_get_memory_requirements2）则为 null，
    /// 调用方应回退到 <see cref="GetImageMemoryRequirements"/>。
    /// </summary>
    public static bool HasImageMemoryRequirements2 => _getImageMemoryRequirements2 != null;

    public static unsafe void GetImageMemoryRequirements2(Device device, ImageMemoryRequirementsInfo2* pInfo, MemoryRequirements2* pMemoryRequirements)
    {
        if (_getImageMemoryRequirements2 == null)
            throw new InvalidOperationException("vkGetImageMemoryRequirements2 不可用（请确认 Vulkan 1.1+ 或已启用 VK_KHR_get_memory_requirements2）。");
        _getImageMemoryRequirements2(device, pInfo, pMemoryRequirements);
    }

    public static unsafe Result AllocateMemory(Device device, ref MemoryAllocateInfo pAllocateInfo, AllocationCallbacks* pAllocator, out DeviceMemory pMemory)
    {
        fixed (MemoryAllocateInfo* p = &pAllocateInfo)
        {
            DeviceMemory tmp;
            var r = _allocateMemory(device, p, pAllocator, &tmp);
            pMemory = tmp;
            return r;
        }
    }

    public static unsafe Result AllocateMemory(Device device, MemoryAllocateInfo* pAllocateInfo, AllocationCallbacks* pAllocator, out DeviceMemory pMemory)
    {
        DeviceMemory tmp;
        var r = _allocateMemory(device, pAllocateInfo, pAllocator, &tmp);
        pMemory = tmp;
        return r;
    }

    public static unsafe void FreeMemory(Device device, DeviceMemory memory, AllocationCallbacks* pAllocator) => _freeMemory(device, memory, pAllocator);

    public static unsafe Result BindImageMemory(Device device, Image image, DeviceMemory memory, ulong memoryOffset)
        => _bindImageMemory(device, image, memory, memoryOffset);

    /// <summary>
    /// 查询 AHardwareBuffer 的内存属性（allocationSize / memoryTypeBits）与格式属性（externalFormat / 采样器建议值）。
    /// <paramref name="buffer"/> 为 AHardwareBuffer 指针（借用，不取得所有权）。
    /// </summary>
    public static unsafe Result GetAndroidHardwareBufferPropertiesANDROID(Device device, nint buffer, AndroidHardwareBufferPropertiesANDROID* pProperties)
    {
        if (_getAndroidHardwareBufferPropertiesANDROID == null)
            throw new InvalidOperationException("vkGetAndroidHardwareBufferPropertiesANDROID 不可用（非 Android 或未启用 VK_ANDROID_external_memory_android_hardware_buffer）。");
        return _getAndroidHardwareBufferPropertiesANDROID(device, buffer, pProperties);
    }

    /// <summary>
    /// 把已分配（且带 <c>VK_ANDROID_external_memory_android_hardware_buffer</c> 导出标志）的 Vulkan 设备内存
    /// 导出为 <c>AHardwareBuffer</c> 指针（消费侧经 <c>ICompositionGpuInterop</c> 直接导入采样）。
    /// </summary>
    /// <param name="device">Vulkan 逻辑设备。</param>
    /// <param name="pInfo">导出信息（含目标 <see cref="DeviceMemory"/>）。</param>
    /// <param name="pBuffer">导出的 AHardwareBuffer 指针（由调用方拥有，须由 Android NDK <c>AHardwareBuffer_release</c> 释放）。</param>
    public static unsafe Result GetMemoryAndroidHardwareBufferANDROID(Device device, MemoryGetAndroidHardwareBufferInfoANDROID* pInfo, out nint pBuffer)
    {
        if (_getMemoryAndroidHardwareBufferANDROID == null)
            throw new InvalidOperationException("vkGetMemoryAndroidHardwareBufferANDROID 不可用（非 Android 或未启用 VK_ANDROID_external_memory_android_hardware_buffer）。");
        nint local;
        Result r = _getMemoryAndroidHardwareBufferANDROID(device, pInfo, &local);
        pBuffer = local;
        return r;
    }

    public static unsafe void GetBufferMemoryRequirements(Device device, Buffer buffer, MemoryRequirements* pMemoryRequirements)
        => _getBufferMemoryRequirements(device, buffer, pMemoryRequirements);

    public static unsafe Result BindBufferMemory(Device device, Buffer buffer, DeviceMemory memory, ulong memoryOffset)
        => _bindBufferMemory(device, buffer, memory, memoryOffset);

    public static unsafe Result MapMemory(Device device, DeviceMemory memory, ulong offset, ulong size, uint flags, void** ppData)
        => _mapMemory(device, memory, offset, size, flags, ppData);

    public static unsafe void UnmapMemory(Device device, DeviceMemory memory) => _unmapMemory(device, memory);

    /// <summary>把 VkDeviceMemory 导出为 Windows HANDLE（外部内存 NT 句柄）。</summary>
    public static unsafe Result GetMemoryWin32HandleKHR(Device device, MemoryGetWin32HandleInfoKHR* pInfo, out nint handle)
    {
        if (_getMemoryWin32HandleKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetMemoryWin32HandleKHR（请确认已启用 VK_KHR_external_memory_win32）。");
        void* h;
        Result r = _getMemoryWin32HandleKHR(device, pInfo, &h);
        handle = (nint)h;
        return r;
    }

    /// <summary>把 VkDeviceMemory 导出为 POSIX 文件描述符（外部内存 fd 句柄）。</summary>
    public static unsafe Result GetMemoryFdKHR(Device device, MemoryGetFdInfoKHR* pInfo, out int fd)
    {
        if (_getMemoryFdKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetMemoryFdKHR（请确认已启用 VK_KHR_external_memory_fd）。");
        int f;
        Result r = _getMemoryFdKHR(device, pInfo, &f);
        fd = f;
        return r;
    }

    /// <summary>查询导入 fd 在本设备上的内存属性（有效 memoryTypeBits）。
    /// 诊断用途：与宿主合成器侧 image requirements 的 memoryTypeBits 对表。</summary>
    public static unsafe Result GetMemoryFdPropertiesKHR(Device device, ExternalMemoryHandleTypeFlags handleType, int fd, MemoryFdPropertiesKHR* pProperties)
    {
        if (_getMemoryFdPropertiesKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetMemoryFdPropertiesKHR（请确认已启用 VK_KHR_external_memory_fd）。");
        return _getMemoryFdPropertiesKHR(device, handleType, fd, pProperties);
    }
}

using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanGpuFrameProducer 的 Helpers 职责段（部分类，由 VulkanGpuFrameProducer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/TryImport 分派/Dispose）在主文件；导入/转换/回读各路径零反射原生调用。
public sealed unsafe partial class VulkanGpuFrameProducer
{
    /// <summary>关闭原生共享句柄（单一责任人）。Windows 关 NT HANDLE；Linux 关 dma_buf fd。</summary>
    private static void CloseHandleIfNeeded(GpuFrameImportSource source, bool isWindows)
    {
        if (isWindows)
            CloseHandle(source.Handle);
        else
            CloseFd((int)source.Handle);
    }

    /// <summary>关闭 Linux dma_buf 文件描述符（导入完成后由导入方负责关闭，防 fd 泄漏）。</summary>
    [LibraryImport("libc")]
    private static partial int close(int fd);

    private static void CloseFd(int fd)
    {
        if (fd >= 0) _ = close(fd);
    }

    private static bool TryMapFormat(PixelFormat format, out Format vkFormat)
    {
        switch (format)
        {
            // D3D11 共享纹理恒为 B8G8R8A8_UNORM（见 D3D11Nv12ToRgbaConverter 输出格式）。ffmpeg 导出端标记
            // RGBA32，但真实 DXGI 格式为 B8G8R8A8_UNORM（BGRA 字节序）；故 RGBA32 与 BGRA32 均须映射为
            // B8G8R8A8Unorm 以匹配共享纹理，否则 vkBindImageMemory 因格式不匹配而失败（零拷贝未接受）。
            case PixelFormat.BGRA32:
            case PixelFormat.RGBA32: vkFormat = Format.B8G8R8A8Unorm; return true;
            default: vkFormat = default; return false;
        }
    }

    private unsafe uint FindMemoryType(uint memoryTypeBits)
    {
        PhysicalDeviceMemoryProperties props;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &props);
        for (uint i = 0; i < props.MemoryTypeCount; i++)
        {
            if ((memoryTypeBits & (1u << (int)i)) != 0 &&
                (props.MemoryTypes[(int)i].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
            {
                return i;
            }
        }
        // 兜底：任意满足 memoryTypeBits 的类型
        for (uint i = 0; i < props.MemoryTypeCount; i++)
            if ((memoryTypeBits & (1u << (int)i)) != 0)
                return i;
        return 0;
    }

    /// <summary>
    /// 外部内存导入专用内存类型选择（D3D11/dma_buf 共享纹理 → Vulkan VkImage）。
    /// <para><b>为何不能复用 <see cref="FindMemoryType"/> 的 DeviceLocalBit 过滤</b>：外部导入纹理的
    /// <c>memoryTypeBits</c> 已由驱动编码「与导入句柄真正兼容」的类型集合；NVIDIA 上该集合内的类型未必带
    /// <c>VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT</c> 标志位（其 heapIndex 虽指向设备本地堆）。若再按
    /// DeviceLocalBit 过滤会漏掉唯一兼容类型、兜底选到不兼容类型，导致 vkAllocateMemory 把导入误判为真实分配
    /// → <c>VK_ERROR_OUT_OF_DEVICE_MEMORY</c>（Khronos 论坛同名案例）。</para>
    /// <para>策略：优先 memoryTypeBits 中带 DeviceLocalBit 的置位（若设备本地堆确实带标志位），否则直接取
    /// 最低置位（驱动已保证兼容）。绝不再附加 DeviceLocalBit 硬过滤。</para>
    /// </summary>
    private unsafe uint ExternalCompatibleMemoryType(uint memoryTypeBits)
    {
        PhysicalDeviceMemoryProperties props;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &props);
        uint fallback = uint.MaxValue;
        for (uint i = 0; i < props.MemoryTypeCount; i++)
        {
            if ((memoryTypeBits & (1u << (int)i)) == 0) continue;
            if (fallback == uint.MaxValue) fallback = i;
            if ((props.MemoryTypes[(int)i].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
                return i;
        }
        return fallback == uint.MaxValue ? 0u : fallback;
    }

    /// <summary>取 memoryTypeBits 中第 <paramref name="index"/> 个类型的属性标志（诊断用）。</summary>
    private unsafe uint GetMemoryTypeFlags(uint memoryTypeBits, uint index)
    {
        if (index >= 32) return 0;
        PhysicalDeviceMemoryProperties props;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &props);
        if (index >= props.MemoryTypeCount) return 0;
        return (uint)props.MemoryTypes[(int)index].PropertyFlags;
    }

    /// <summary>取 Vulkan 物理设备名（诊断多 GPU 选卡：须与 D3D11VA 解码设备同 GPU 才能导入 D3D11 共享纹理）。</summary>
    private unsafe string GetDeviceName()
    {
        PhysicalDeviceProperties props;
        VulkanNative.GetPhysicalDeviceProperties(_physicalDevice, &props);
        return Marshal.PtrToStringAnsi((nint)props.DeviceName) ?? "(unknown)";
    }

    /// <summary>
    /// 取 image 的内存需求。外部内存导入纹理优先用 vkGetImageMemoryRequirements2 + VkMemoryDedicatedRequirements
    /// 取权威 size / memoryTypeBits / dedicated 标志；Vulkan 1.0 未提供 v2 时回退 v1。
    /// <para><b>为何必须 v2</b>：带 VK_EXTERNAL_MEMORY 的 image，v1 vkGetImageMemoryRequirements 返回的
    /// memoryTypeBits / size 在导入场景下不可靠（NVIDIA 上尤甚）——v1 的 memoryTypeBits 常不含与导入句柄
    /// 真正兼容的设备本地内存类型，导致 vkAllocateMemory 误判为真实分配而返回 ErrorOutOfDeviceMemory。</para>
    /// </summary>
    private unsafe (ulong Size, uint MemoryTypeBits, bool RequiresDedicated) QueryImageMemoryRequirements(Image image)
    {
        if (VulkanNative.HasImageMemoryRequirements2)
        {
            ImageMemoryRequirementsInfo2 reqInfo2 = new()
            {
                SType = StructureType.ImageMemoryRequirementsInfo2,
                Image = image,
            };
            MemoryDedicatedRequirements dedicatedReq = new()
            {
                SType = StructureType.MemoryDedicatedRequirements,
            };
            reqInfo2.PNext = &dedicatedReq;
            MemoryRequirements2 memReq2 = new()
            {
                SType = StructureType.MemoryRequirements2,
            };
            VulkanNative.GetImageMemoryRequirements2(_device, &reqInfo2, &memReq2);
            return (memReq2.MemoryRequirements.Size, memReq2.MemoryRequirements.MemoryTypeBits,
                dedicatedReq.RequiresDedicatedAllocation || dedicatedReq.PrefersDedicatedAllocation);
        }
        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, image, &memReq);
        return (memReq.Size, memReq.MemoryTypeBits, false);
    }
}

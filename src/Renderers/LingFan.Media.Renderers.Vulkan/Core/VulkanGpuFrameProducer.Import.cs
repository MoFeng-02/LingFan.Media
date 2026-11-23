using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanGpuFrameProducer 的 Import 职责段（部分类，由 VulkanGpuFrameProducer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/TryImport 分派/Dispose）在主文件；导入/转换/回读各路径零反射原生调用。
public sealed unsafe partial class VulkanGpuFrameProducer
{
    private unsafe bool TryImportWin32(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;
        if (!TryMapFormat(source.Format, out Format vkFormat))
        {
            if (_diagRemain > 0) { _diagRemain--; _logger?.LogWarning($"[VKFDIAG] TryMapFormat 失败: fmt={source.Format}"); }
            CloseHandle(source.Handle); // 失败出口：句柄尚未被消费，须关闭防泄漏（单一责任人）
            return false;
        }

        var extMem = new ExternalMemoryImageCreateInfo
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
        };
        var ci = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = vkFormat,
            Extent = new Extent3D((uint)source.Width, (uint)source.Height, 1),
            MipLevels = 1,
            ArrayLayers = (uint)Math.Max(1, source.ArrayLayers),
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        ExternalMemoryImageCreateInfo* pExt = &extMem;
        ci.PNext = pExt;

        Image image;
        Result createResult = VulkanNative.CreateImage(_device, &ci, null, out image);
        if (createResult != Result.Success)
        {
            if (_diagRemain > 0) { _diagRemain--; _logger?.LogWarning($"[VKFDIAG] vkCreateImage 失败: {createResult} (fmt={vkFormat}, w={source.Width} h={source.Height} handle={(long)source.Handle:X})"); }
            CloseHandle(source.Handle);
            return false;
        }

        // memory 提升到 try 外：catch 需据其是否已分配决定是否 FreeMemory（防异常路径内存泄漏）
        DeviceMemory memory = default;
        try
        {
            var req = QueryImageMemoryRequirements(image);

            var imp = new ImportMemoryWin32HandleInfoKHR
            {
                SType = StructureType.ImportMemoryWin32HandleInfoKhr,
                HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
                Handle = source.Handle,
            };
            // D3D11 共享纹理为 dedicated 分配；导入 Vulkan 必须显式声明 VkMemoryDedicatedAllocateInfo，
            // 否则 vkBindImageMemory 因内存非 dedicated 而失败（零拷贝未接受、回落软解 OOM）。
            // NVIDIA 即使 requirements 未标 requires 也强制 dedicated，故 Win32 恒声明。
            var dedicated = new MemoryDedicatedAllocateInfo
            {
                SType = StructureType.MemoryDedicatedAllocateInfo,
                Image = image,
            };
            imp.PNext = &dedicated;
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = &imp,
                AllocationSize = req.Size,
                MemoryTypeIndex = ExternalCompatibleMemoryType(req.MemoryTypeBits),
            };

            Result allocResult = VulkanNative.AllocateMemory(_device, &ai, null, out memory);
            if (allocResult != Result.Success)
            {
                if (_diagRemain > 0)
                {
                    _diagRemain--;
                    _logger?.LogWarning($"[VKFDIAG] vkAllocateMemory 失败: {allocResult} | v2={VulkanNative.HasImageMemoryRequirements2} size={req.Size} memTypeBits=0x{req.MemoryTypeBits:X} reqDed={req.RequiresDedicated} 选用memType={ai.MemoryTypeIndex} flags=0x{GetMemoryTypeFlags(req.MemoryTypeBits, ai.MemoryTypeIndex):X} vkGpu={GetDeviceName()} handle={(long)source.Handle:X}");
                }
                VulkanNative.DestroyImage(_device, image, null);
                CloseHandle(source.Handle);
                return false;
            }

            Result bindResult = VulkanNative.BindImageMemory(_device, image, memory, 0);
            if (bindResult != Result.Success)
            {
                if (_diagRemain > 0) { _diagRemain--; _logger?.LogWarning($"[VKFDIAG] vkBindImageMemory 失败: {bindResult} (handle={(long)source.Handle:X})"); }
                VulkanNative.FreeMemory(_device, memory, null);
                VulkanNative.DestroyImage(_device, image, null);
                CloseHandle(source.Handle);
                return false;
            }

            // NT 共享句柄：vkAllocateMemory 已把句柄导入为 VkDeviceMemory（独立引用），此处关闭句柄不销毁资源，防内核句柄泄漏。
            // D3D11 共享纹理真实格式为 B8G8R8A8_UNORM（BGRA 字节序），交付 BGRA32 使 Blit 路径
            // srcVkFormat=B8G8R8A8Unorm 与 VkImage/swapchain 一致，避免 R/B 通道偏蓝。
            texture = new VulkanImageResource(_device, image, memory,
                source.Width, source.Height, PixelFormat.BGRA32, source.SubresourceIndex, ImageLayout.Undefined);
            CloseHandle(source.Handle);
            return true;
        }
        catch
        {
            if (memory.Handle != 0) VulkanNative.FreeMemory(_device, memory, null); // 异常出口：已分配内存须释放防泄漏
            VulkanNative.DestroyImage(_device, image, null);
            CloseHandle(source.Handle);
            throw;
        }
    }

    private unsafe bool TryImportLinux(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;
        if (!TryMapFormat(source.Format, out Format vkFormat))
        {
            CloseFd((int)source.Handle); // 失败出口：fd 尚未被消费，须关闭防泄漏（单一责任人）
            return false;
        }

        var extMem = new ExternalMemoryImageCreateInfo
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.DmaBufBitExt,
        };
        var ci = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = vkFormat,
            Extent = new Extent3D((uint)source.Width, (uint)source.Height, 1),
            MipLevels = 1,
            ArrayLayers = (uint)Math.Max(1, source.ArrayLayers),
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        ExternalMemoryImageCreateInfo* pExt = &extMem;
        ci.PNext = pExt;

        Image image;
        if (VulkanNative.CreateImage(_device, &ci, null, out image) != Result.Success)
        {
            CloseFd((int)source.Handle); // 失败出口：fd 尚未被消费，须关闭防泄漏（单一责任人）
            return false;
        }

        // memory 提升到 try 外：catch 需据其是否已分配决定是否 FreeMemory（防异常路径内存泄漏）
        DeviceMemory memory = default;
        try
        {
            var req = QueryImageMemoryRequirements(image);

            var imp = new ImportMemoryFdInfoKHR
            {
                SType = StructureType.ImportMemoryFDInfoKhr,
                HandleType = ExternalMemoryHandleTypeFlags.DmaBufBitExt,
                Fd = (int)source.Handle,
            };
            MemoryDedicatedAllocateInfo dedicated = new()
            {
                SType = StructureType.MemoryDedicatedAllocateInfo,
                Image = image,
            };
            if (req.RequiresDedicated)
                imp.PNext = &dedicated;
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = &imp,
                AllocationSize = req.Size,
                MemoryTypeIndex = ExternalCompatibleMemoryType(req.MemoryTypeBits),
            };

            if (VulkanNative.AllocateMemory(_device, &ai, null, out memory) != Result.Success)
            {
                VulkanNative.DestroyImage(_device, image, null);
                CloseFd((int)source.Handle); // 失败出口：fd 未导入，须关闭防泄漏（单一责任人）
                return false;
            }

            if (VulkanNative.BindImageMemory(_device, image, memory, 0) != Result.Success)
            {
                VulkanNative.FreeMemory(_device, memory, null);
                VulkanNative.DestroyImage(_device, image, null);
                CloseFd((int)source.Handle); // 失败出口：fd 未导入，须关闭防泄漏（单一责任人）
                return false;
            }

            texture = new VulkanImageResource(_device, image, memory,
                source.Width, source.Height, source.Format, source.SubresourceIndex, ImageLayout.Undefined);
            CloseFd((int)source.Handle); // 成功出口：导入消费 fd，关闭不销毁 dma_buf（VkDeviceMemory 持引用），防 fd 泄漏
            return true;
        }
        catch
        {
            if (memory.Handle != 0) VulkanNative.FreeMemory(_device, memory, null); // 异常出口：已分配内存须释放防泄漏
            VulkanNative.DestroyImage(_device, image, null);
            CloseFd((int)source.Handle); // 异常出口：fd 须关闭防泄漏（单一责任人）
            throw;
        }
    }

}

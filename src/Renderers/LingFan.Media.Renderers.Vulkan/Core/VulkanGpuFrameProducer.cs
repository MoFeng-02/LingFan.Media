namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan 零拷贝帧生产者：把解码后端原生输出（Windows D3D11 共享句柄 / Linux VAAPI dma_buf）
/// 经 Vulkan 外部内存导入为 VkImage（<see cref="VulkanImageResource"/>），实现零拷贝上屏。
/// </summary>
/// <remarks>
/// <para>注册为 <see cref="IGpuFrameProducer"/>，供解码器经中立桥调用，严守依赖倒置（后端不感知 Vulkan 绑定细节）。</para>
/// <para><b>导入机制</b>：创建带 VK_KHR_external_memory 的 VkImage，经 vkAllocateMemory +
/// <c>ImportMemoryWin32HandleInfoKHR</c>（Windows）/ <c>ImportMemoryFdInfoKHR</c>（Linux）绑定原生共享句柄，
/// vkBindImageMemory 后即为零拷贝 Vulkan 纹理，由 <see cref="VulkanRenderer.BlitVulkanImageResource"/> 直接 blit 到 SwapChain。</para>
/// <para><b>能力自报 + 行为副作用双判据（S_OK≠被接受）</b>：扩展不可用 / 句柄无效 / 导入失败 →
/// <see cref="TryImport"/> 返回 <see langword="false"/>，调用方回落软解并计 [FRAMEPATH] 统计，绝不报"已就绪"假绿。</para>
/// <para><b>AOT</b>：VulkanNative 为零反射绑定（vkGetDeviceProcAddr 解析外部内存函数指针）；
/// 无 [DllImport]/[ComImport]/反射；跨平台经 OperatingSystem.IsXxx() 运行时分发，无 #if。</para>
/// <para><b>v1 范围</b>：Windows(D3D11→VK) + Linux(VAAPI→VK)。Android(AHardwareBuffer)/Apple(IOSurface) 为后续端点，
/// 当前返回 false（调用方回落软解），不阻断播放。</para>
    /// <para><b>多切片</b>：按 <see cref="GpuFrameImportSource.ArrayLayers"/> 创建整数组 VkImage，并据
    /// <see cref="GpuFrameImportSource.SubresourceIndex"/> 在 <see cref="VulkanRenderer.BlitVulkanImageResource"/>
    /// 选 <c>baseArrayLayer</c>，正确处理 D3D11VA 纹理数组（切片索引=avFrame-&gt;data[1]）。</para>
/// <para><b>句柄所有权契约</b>：导入成功后原生共享句柄（HANDLE / fd）由 Vulkan 消费，调用方（解码器）不得关闭；
/// 导入失败则返回 false，调用方须关闭句柄。生产者不在内部关闭句柄。</para>
/// </remarks>
public sealed class VulkanGpuFrameProducer : IGpuFrameProducer
{
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly ILogger? _logger;

    /// <inheritdoc/>
    public GPUApiType ApiType => GPUApiType.Vulkan;

    public VulkanGpuFrameProducer(Device device, PhysicalDevice physicalDevice, ILogger? logger = null)
    {
        _device = device;
        _physicalDevice = physicalDevice;
        _logger = logger;
    }

    /// <inheritdoc/>
    public unsafe bool TryImport(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;
        try
        {
            if (source.Handle == IntPtr.Zero || source.Width <= 0 || source.Height <= 0)
                return false;

            if (OperatingSystem.IsWindows() && source.Kind == GpuFrameImportKind.D3D11SharedHandle)
                return TryImportWin32(source, out texture);
            if (OperatingSystem.IsLinux() && source.Kind == GpuFrameImportKind.VaApiDmaBuf)
                return TryImportLinux(source, out texture);

            // Android / iOS：后续端点（AHardwareBuffer / IOSurface），当前回落软解。
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Vulkan 零拷贝导入失败，回落软件解码（S_OK≠被接受：导入行为副作用未成立）。");
            texture?.Dispose();
            texture = null;
            return false;
        }
    }

    private unsafe bool TryImportWin32(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;
        if (!TryMapFormat(source.Format, out Format vkFormat))
            return false;

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
        if (VulkanNative.CreateImage(_device, &ci, null, out image) != Result.Success)
            return false;

        try
        {
            MemoryRequirements memReq;
            VulkanNative.GetImageMemoryRequirements(_device, image, &memReq);

            var imp = new ImportMemoryWin32HandleInfoKHR
            {
                SType = StructureType.ImportMemoryWin32HandleInfoKhr,
                HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
                Handle = source.Handle,
            };
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = &imp,
                AllocationSize = memReq.Size,
                MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits),
            };

            DeviceMemory memory;
            if (VulkanNative.AllocateMemory(_device, &ai, null, out memory) != Result.Success)
                return false;

            if (VulkanNative.BindImageMemory(_device, image, memory, 0) != Result.Success)
            {
                VulkanNative.FreeMemory(_device, memory, null);
                return false;
            }

            texture = new VulkanImageResource(_device, image, memory,
                source.Width, source.Height, source.Format, source.SubresourceIndex, ImageLayout.Undefined);
            return true;
        }
        catch
        {
            VulkanNative.DestroyImage(_device, image, null);
            throw;
        }
    }

    private unsafe bool TryImportLinux(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;
        if (!TryMapFormat(source.Format, out Format vkFormat))
            return false;

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
            return false;

        try
        {
            MemoryRequirements memReq;
            VulkanNative.GetImageMemoryRequirements(_device, image, &memReq);

            var imp = new ImportMemoryFdInfoKHR
            {
                SType = StructureType.ImportMemoryFDInfoKhr,
                HandleType = ExternalMemoryHandleTypeFlags.DmaBufBitExt,
                Fd = (int)source.Handle,
            };
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = (void*)&imp,
                AllocationSize = memReq.Size,
                MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits),
            };

            DeviceMemory memory;
            if (VulkanNative.AllocateMemory(_device, &ai, null, out memory) != Result.Success)
                return false;

            if (VulkanNative.BindImageMemory(_device, image, memory, 0) != Result.Success)
            {
                VulkanNative.FreeMemory(_device, memory, null);
                return false;
            }

            texture = new VulkanImageResource(_device, image, memory,
                source.Width, source.Height, source.Format, source.SubresourceIndex, ImageLayout.Undefined);
            return true;
        }
        catch
        {
            VulkanNative.DestroyImage(_device, image, null);
            throw;
        }
    }

    private static bool TryMapFormat(PixelFormat format, out Format vkFormat)
    {
        switch (format)
        {
            case PixelFormat.BGRA32: vkFormat = Format.B8G8R8A8Unorm; return true;
            case PixelFormat.RGBA32: vkFormat = Format.R8G8B8A8Unorm; return true;
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
}

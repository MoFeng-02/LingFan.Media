using System.Diagnostics;
using LingFan.Media.GPUShare.Vulkan;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanRenderer 的 Resources 职责段（部分类，由 VulkanRenderer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Attach/Present/Clear/Detach/Dispose）在主文件；全部同步原生调用，_gate 串行化。
internal sealed unsafe partial class VulkanRenderer
{
    internal void CreateCommandPoolAndBuffer()
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _queueFamilyIndex,
        };

        Result result = VulkanNative.CreateCommandPool(_device, ref poolInfo, null, out _commandPool);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateCommandPool 失败: {result}");

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        var cmds = stackalloc CommandBuffer[1];
        // 检查 AllocateCommandBuffers 返回值
        result = VulkanNative.AllocateCommandBuffers(_device, &allocInfo, cmds);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateCommandBuffers 失败: {result}");
        _commandBuffer = cmds[0];
    }

    private void CreateSemaphores()
    {
        SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };

        Result r1 = VulkanNative.CreateSemaphore(_device, ref semInfo, null, out _imageAvailableSemaphore);
        if (r1 != Result.Success)
            throw new InvalidOperationException($"vkCreateSemaphore（imageAvailable）失败: {r1}");

        // renderFinished 按 SwapChain 图像数建独立信号量数组，索引 = imageIndex。
        int n = _swapchainImages.Length;
        _renderFinishedSemaphores = new Semaphore[n];
        for (int i = 0; i < n; i++)
        {
            Result r = VulkanNative.CreateSemaphore(_device, ref semInfo, null, out _renderFinishedSemaphores[i]);
            if (r != Result.Success)
                throw new InvalidOperationException($"vkCreateSemaphore（renderFinished[{i}]）失败: {r}");
        }
    }

    private void CreateShaderPipeline()
    {
        _shaderPipeline = new VulkanShaderPipeline(_physicalDevice, _device);
        _shaderPipeline.EnsureSwapchainResources(_swapchainFormat, _swapchainExtent, _swapchainImages);
    }

    private void EnsureStagingBuffer(ulong requiredSize)
    {
        if (_stagingBuffer.Handle != 0 && _stagingBufferSize >= requiredSize) return;

        if (_stagingBuffer.Handle != 0)
        {
            // realloc：先解持久映射再释放，避免悬空映射
            if (_stagingMapped != null)
            {
                VulkanNative.UnmapMemory(_device, _stagingMemory);
                _stagingMapped = null;
            }
            VulkanNative.DestroyBuffer(_device, _stagingBuffer, null);
            VulkanNative.FreeMemory(_device, _stagingMemory, null);
            // 重置为 default 防止双重释放——若后续 CreateBuffer/AllocateMemory 失败，
            // ReleaseSessionResources 会通过陈旧句柄再次释放已释放的内存
            _stagingBuffer = default;
            _stagingMemory = default;
        }

        BufferCreateInfo bufInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = requiredSize,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };

        Result result = VulkanNative.CreateBuffer(_device, ref bufInfo, null, out _stagingBuffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateBuffer 失败: {result}");

        MemoryRequirements memReq;
        VulkanNative.GetBufferMemoryRequirements(_device, _stagingBuffer, &memReq);

        uint memTypeIndex = FindMemoryType(
            memReq.MemoryTypeBits,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        MemoryAllocateInfo memInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memTypeIndex,
        };

        result = VulkanNative.AllocateMemory(_device, &memInfo, null, out _stagingMemory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory 失败: {result}");

        // 检查 BindBufferMemory 返回值
        result = VulkanNative.BindBufferMemory(_device, _stagingBuffer, _stagingMemory, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindBufferMemory 失败: {result}");

        // 持久映射整段 staging（HOST_COHERENT 内存，写入经一致性语义自动对 GPU 可见）。
        // 只映射一次并长期复用，UploadSoftwareFrame 直接写 _stagingMapped，免去每帧 Map/Unmap 仪式开销。
        void* mapped = null;
        Result mapResult = VulkanNative.MapMemory(_device, _stagingMemory, 0, memReq.Size, 0, &mapped);
        if (mapResult != Result.Success)
            throw new InvalidOperationException($"vkMapMemory（staging 持久映射）失败: {mapResult}");
        _stagingMapped = mapped;

        // 必须记录 buffer 创建大小（requiredSize），不能记 memReq.Size（≥ requiredSize，含对齐填充）。
        // 否则帧尺寸中途变大时，第 688 行复用判断会误判「够用」——新 requiredSize ≤ 旧 memReq.Size
        // 但 > 旧 buffer 实际 Size，CmdCopyBufferToImage 读取超出 VkBuffer 对象范围（validation error / UB）。
        _stagingBufferSize = requiredSize;
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memProps;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &memProps);
        for (int i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << i)) != 0 &&
                (memProps.MemoryTypes[i].PropertyFlags & properties) == properties)
                return (uint)i;
        }
        throw new InvalidOperationException("未找到合适的 Vulkan 内存类型。");
    }

    /// <summary>
    /// 记录图像布局转换的 Pipeline Barrier。
    /// </summary>
    /// <param name="image">目标图像。</param>
    /// <param name="oldLayout">旧布局。</param>
    /// <param name="newLayout">新布局。</param>
    /// <param name="srcAccess">源访问掩码（前一阶段的写入操作）。</param>
    /// <param name="dstAccess">目标访问掩码（后续阶段的读取/写入操作）。</param>
    /// <param name="srcStageMask">源管线阶段（前一操作发生在哪个阶段）。</param>
    /// <param name="dstStageMask">目标管线阶段（后续操作发生在哪个阶段）。</param>
    private void TransitionImageLayout(
        Image image, ImageLayout oldLayout, ImageLayout newLayout,
        AccessFlags srcAccess, AccessFlags dstAccess,
        PipelineStageFlags srcStageMask, PipelineStageFlags dstStageMask)
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = ~0u,
            DstQueueFamilyIndex = ~0u,
            Image = image,
            SubresourceRange = range,
        };

        VulkanNative.CmdPipelineBarrier(
            _commandBuffer,
            srcStageMask,
            dstStageMask,
            0, 0, null, 0, null, 1, &barrier);
    }

    private void ReleaseSessionResources()
    {
        if (_device.Handle != 0)
            VulkanNative.DeviceWaitIdle(_device);

        _shaderPipeline?.Dispose();
        _shaderPipeline = null;

        // NV12→RGBA 转换器：与 Shader 管线同生命周期（Attach 创建），此处对称释放。
        _nv12Converter?.Dispose();
        _nv12Converter = null;

        if (_imageAvailableSemaphore.Handle != 0)
        { VulkanNative.DestroySemaphore(_device, _imageAvailableSemaphore, null); _imageAvailableSemaphore = default; }
        foreach (var s in _renderFinishedSemaphores)
            if (s.Handle != 0) VulkanNative.DestroySemaphore(_device, s, null);
        _renderFinishedSemaphores = [];

        if (_stagingBuffer.Handle != 0)
        {
            if (_stagingMapped != null)
            {
                VulkanNative.UnmapMemory(_device, _stagingMemory);
                _stagingMapped = null;
            }
            VulkanNative.DestroyBuffer(_device, _stagingBuffer, null);
            _stagingBuffer = default;
        }
        if (_stagingMemory.Handle != 0)
        { VulkanNative.FreeMemory(_device, _stagingMemory, null); _stagingMemory = default; }
        _stagingBufferSize = 0;

        if (_stagingImage.Handle != 0)
        { VulkanNative.DestroyImage(_device, _stagingImage, null); _stagingImage = default; }
        if (_stagingImageMem.Handle != 0)
        { VulkanNative.FreeMemory(_device, _stagingImageMem, null); _stagingImageMem = default; }
        _stagingImageW = 0;
        _stagingImageH = 0;
        _stagingImageFormat = default;

        if (_swapchain.Handle != 0)
        { VulkanNative.DestroySwapchainKHR(_device, _swapchain, null); _swapchain = default; }
        _swapchainImages = [];

        if (_surface.Handle != 0)
        { VulkanNative.DestroySurfaceKHR(_instance, _surface, null); _surface = default; }

        if (_commandPool.Handle != 0)
        { VulkanNative.DestroyCommandPool(_device, _commandPool, null); _commandPool = default; }
    }
}

using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Commands 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
    public static unsafe Result CreateCommandPool(Device device, ref CommandPoolCreateInfo pCreateInfo, AllocationCallbacks* pAllocator, out CommandPool pCommandPool)
    {
        fixed (CommandPoolCreateInfo* p = &pCreateInfo)
        {
            CommandPool tmp;
            var r = _createCommandPool(device, p, pAllocator, &tmp);
            pCommandPool = tmp;
            return r;
        }
    }

    public static unsafe void DestroyCommandPool(Device device, CommandPool commandPool, AllocationCallbacks* pAllocator)
        => _destroyCommandPool(device, commandPool, pAllocator);

    public static unsafe Result AllocateCommandBuffers(Device device, ref CommandBufferAllocateInfo pAllocateInfo, CommandBuffer* pCommandBuffers)
    {
        fixed (CommandBufferAllocateInfo* p = &pAllocateInfo)
            return _allocateCommandBuffers(device, p, pCommandBuffers);
    }

    public static unsafe Result AllocateCommandBuffers(Device device, CommandBufferAllocateInfo* pAllocateInfo, CommandBuffer* pCommandBuffers)
        => _allocateCommandBuffers(device, pAllocateInfo, pCommandBuffers);

    public static unsafe Result BeginCommandBuffer(CommandBuffer commandBuffer, ref CommandBufferBeginInfo pBeginInfo)
    {
        fixed (CommandBufferBeginInfo* p = &pBeginInfo)
            return _beginCommandBuffer(commandBuffer, p);
    }

    public static unsafe Result EndCommandBuffer(CommandBuffer commandBuffer) => _endCommandBuffer(commandBuffer);

    public static unsafe Result ResetCommandBuffer(CommandBuffer commandBuffer, CommandBufferResetFlags flags)
        => _resetCommandBuffer(commandBuffer, flags);

    public static unsafe Result QueueSubmit(Queue queue, uint submitCount, SubmitInfo* pSubmits, nint fence)
        => _queueSubmit(queue, submitCount, pSubmits, fence);

    public static unsafe Result QueueWaitIdle(Queue queue) => _queueWaitIdle(queue);

    public static unsafe void CmdBeginRenderPass(CommandBuffer commandBuffer, RenderPassBeginInfo* pRenderPassBegin, SubpassContents contents)
        => _cmdBeginRenderPass(commandBuffer, pRenderPassBegin, contents);

    public static unsafe void CmdEndRenderPass(CommandBuffer commandBuffer) => _cmdEndRenderPass(commandBuffer);

    public static unsafe void CmdBindPipeline(CommandBuffer commandBuffer, PipelineBindPoint pipelineBindPoint, Pipeline pipeline)
        => _cmdBindPipeline(commandBuffer, pipelineBindPoint, pipeline);

    public static unsafe void CmdSetViewport(CommandBuffer commandBuffer, uint firstViewport, uint viewportCount, Viewport* pViewports)
        => _cmdSetViewport(commandBuffer, firstViewport, viewportCount, pViewports);

    public static unsafe void CmdSetScissor(CommandBuffer commandBuffer, uint firstScissor, uint scissorCount, Rect2D* pScissors)
        => _cmdSetScissor(commandBuffer, firstScissor, scissorCount, pScissors);

    public static unsafe void CmdPushConstants(CommandBuffer commandBuffer, PipelineLayout layout, ShaderStageFlags stageFlags, uint offset, uint size, void* pValues)
        => _cmdPushConstants(commandBuffer, layout, stageFlags, offset, size, pValues);

    public static unsafe void CmdBindDescriptorSets(CommandBuffer commandBuffer, PipelineBindPoint pipelineBindPoint, PipelineLayout layout, uint firstSet, uint descriptorSetCount, DescriptorSet* pDescriptorSets, uint dynamicOffsetCount, uint* pDynamicOffsets)
        => _cmdBindDescriptorSets(commandBuffer, pipelineBindPoint, layout, firstSet, descriptorSetCount, pDescriptorSets, dynamicOffsetCount, pDynamicOffsets);

    public static unsafe void CmdDraw(CommandBuffer commandBuffer, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
        => _cmdDraw(commandBuffer, vertexCount, instanceCount, firstVertex, firstInstance);

    public static unsafe void CmdPipelineBarrier(CommandBuffer commandBuffer, PipelineStageFlags srcStageMask, PipelineStageFlags dstStageMask, uint dependencyFlags, uint memoryBarrierCount, MemoryBarrier* pMemoryBarriers, uint bufferMemoryBarrierCount, BufferMemoryBarrier* pBufferMemoryBarriers, uint imageMemoryBarrierCount, ImageMemoryBarrier* pImageMemoryBarriers)
        => _cmdPipelineBarrier(commandBuffer, srcStageMask, dstStageMask, dependencyFlags, memoryBarrierCount, pMemoryBarriers, bufferMemoryBarrierCount, pBufferMemoryBarriers, imageMemoryBarrierCount, pImageMemoryBarriers);

    public static unsafe void CmdCopyBufferToImage(CommandBuffer commandBuffer, Buffer srcBuffer, Image dstImage, ImageLayout dstImageLayout, uint regionCount, BufferImageCopy* pRegions)
        => _cmdCopyBufferToImage(commandBuffer, srcBuffer, dstImage, dstImageLayout, regionCount, pRegions);

    public static unsafe void CmdCopyImageToBuffer(CommandBuffer commandBuffer, Image srcImage, ImageLayout srcImageLayout, Buffer dstBuffer, uint regionCount, BufferImageCopy* pRegions)
        => _cmdCopyImageToBuffer(commandBuffer, srcImage, srcImageLayout, dstBuffer, regionCount, pRegions);

    public static unsafe void CmdCopyImage(CommandBuffer commandBuffer, Image srcImage, ImageLayout srcImageLayout, Image dstImage, ImageLayout dstImageLayout, uint regionCount, ImageCopy* pRegions)
        => _cmdCopyImage(commandBuffer, srcImage, srcImageLayout, dstImage, dstImageLayout, regionCount, pRegions);

    public static unsafe void CmdBlitImage(CommandBuffer commandBuffer, Image srcImage, ImageLayout srcImageLayout, Image dstImage, ImageLayout dstImageLayout, uint regionCount, ImageBlit* pRegions, Filter filter)
        => _cmdBlitImage(commandBuffer, srcImage, srcImageLayout, dstImage, dstImageLayout, regionCount, pRegions, filter);

    public static unsafe void CmdClearColorImage(CommandBuffer commandBuffer, Image image, ImageLayout imageLayout, ClearColorValue* pColor, uint rangeCount, ImageSubresourceRange* pRanges)
        => _cmdClearColorImage(commandBuffer, image, imageLayout, pColor, rangeCount, pRanges);

    // KHR WSI 扩展（数组重载以对齐 Silk.NET Khr* 调用点）
}

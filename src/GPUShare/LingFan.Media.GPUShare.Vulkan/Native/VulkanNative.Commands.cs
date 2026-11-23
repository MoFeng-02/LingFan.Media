using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Commands 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
    /// <summary>创建命令池（vkCreateCommandPool；命令缓冲从其分配，绑定队列族）。</summary>
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

    /// <summary>销毁命令池（vkDestroyCommandPool；其分配的命令缓冲一并释放）。</summary>
    public static unsafe void DestroyCommandPool(Device device, CommandPool commandPool, AllocationCallbacks* pAllocator)
        => _destroyCommandPool(device, commandPool, pAllocator);

    /// <summary>分配命令缓冲（vkAllocateCommandBuffers ref 重载；数量由 pAllocateInfo 声明）。</summary>
    public static unsafe Result AllocateCommandBuffers(Device device, ref CommandBufferAllocateInfo pAllocateInfo, CommandBuffer* pCommandBuffers)
    {
        fixed (CommandBufferAllocateInfo* p = &pAllocateInfo)
            return _allocateCommandBuffers(device, p, pCommandBuffers);
    }

    /// <summary>分配命令缓冲（vkAllocateCommandBuffers 指针重载）。</summary>
    public static unsafe Result AllocateCommandBuffers(Device device, CommandBufferAllocateInfo* pAllocateInfo, CommandBuffer* pCommandBuffers)
        => _allocateCommandBuffers(device, pAllocateInfo, pCommandBuffers);

    /// <summary>开始录制命令缓冲（vkBeginCommandBuffer；OneTimeSubmit 等用法经 pBeginInfo 声明）。</summary>
    public static unsafe Result BeginCommandBuffer(CommandBuffer commandBuffer, ref CommandBufferBeginInfo pBeginInfo)
    {
        fixed (CommandBufferBeginInfo* p = &pBeginInfo)
            return _beginCommandBuffer(commandBuffer, p);
    }

    /// <summary>结束录制命令缓冲（vkEndCommandBuffer；之后方可提交）。</summary>
    public static unsafe Result EndCommandBuffer(CommandBuffer commandBuffer) => _endCommandBuffer(commandBuffer);

    /// <summary>复位命令缓冲为可重新录制状态（vkResetCommandBuffer）。</summary>
    public static unsafe Result ResetCommandBuffer(CommandBuffer commandBuffer, CommandBufferResetFlags flags)
        => _resetCommandBuffer(commandBuffer, flags);

    /// <summary>向队列提交命令缓冲批次（vkQueueSubmit；等待/信号语义经 SubmitInfo，完成可 signal fence）。</summary>
    public static unsafe Result QueueSubmit(Queue queue, uint submitCount, SubmitInfo* pSubmits, nint fence)
        => _queueSubmit(queue, submitCount, pSubmits, fence);

    /// <summary>CPU 阻塞等待队列全部已提交工作完成（vkQueueWaitIdle；重量级同步点）。</summary>
    public static unsafe Result QueueWaitIdle(Queue queue) => _queueWaitIdle(queue);

    /// <summary>录制「开始渲染通道」（vkCmdBeginRenderPass；绑定帧缓冲与清屏值）。</summary>
    public static unsafe void CmdBeginRenderPass(CommandBuffer commandBuffer, RenderPassBeginInfo* pRenderPassBegin, SubpassContents contents)
        => _cmdBeginRenderPass(commandBuffer, pRenderPassBegin, contents);

    /// <summary>录制「结束渲染通道」（vkCmdEndRenderPass；执行附件 store 操作与布局转换）。</summary>
    public static unsafe void CmdEndRenderPass(CommandBuffer commandBuffer) => _cmdEndRenderPass(commandBuffer);

    /// <summary>录制「绑定管线」（vkCmdBindPipeline；图形/计算绑定点）。</summary>
    public static unsafe void CmdBindPipeline(CommandBuffer commandBuffer, PipelineBindPoint pipelineBindPoint, Pipeline pipeline)
        => _cmdBindPipeline(commandBuffer, pipelineBindPoint, pipeline);

    /// <summary>录制「设置动态视口」（vkCmdSetViewport；管线须声明对应动态状态）。</summary>
    public static unsafe void CmdSetViewport(CommandBuffer commandBuffer, uint firstViewport, uint viewportCount, Viewport* pViewports)
        => _cmdSetViewport(commandBuffer, firstViewport, viewportCount, pViewports);

    /// <summary>录制「设置动态裁剪矩形」（vkCmdSetScissor；管线须声明对应动态状态）。</summary>
    public static unsafe void CmdSetScissor(CommandBuffer commandBuffer, uint firstScissor, uint scissorCount, Rect2D* pScissors)
        => _cmdSetScissor(commandBuffer, firstScissor, scissorCount, pScissors);

    /// <summary>录制「推送常量更新」（vkCmdPushConstants；小参数直写命令缓冲，免描述符）。</summary>
    public static unsafe void CmdPushConstants(CommandBuffer commandBuffer, PipelineLayout layout, ShaderStageFlags stageFlags, uint offset, uint size, void* pValues)
        => _cmdPushConstants(commandBuffer, layout, stageFlags, offset, size, pValues);

    /// <summary>录制「绑定描述符集」（vkCmdBindDescriptorSets）。</summary>
    public static unsafe void CmdBindDescriptorSets(CommandBuffer commandBuffer, PipelineBindPoint pipelineBindPoint, PipelineLayout layout, uint firstSet, uint descriptorSetCount, DescriptorSet* pDescriptorSets, uint dynamicOffsetCount, uint* pDynamicOffsets)
        => _cmdBindDescriptorSets(commandBuffer, pipelineBindPoint, layout, firstSet, descriptorSetCount, pDescriptorSets, dynamicOffsetCount, pDynamicOffsets);

    /// <summary>录制「非索引绘制」（vkCmdDraw；全屏三角形等无顶点缓冲绘制）。</summary>
    public static unsafe void CmdDraw(CommandBuffer commandBuffer, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
        => _cmdDraw(commandBuffer, vertexCount, instanceCount, firstVertex, firstInstance);

    /// <summary>录制「管线屏障」（vkCmdPipelineBarrier；执行/内存依赖与图像布局转换）。</summary>
    public static unsafe void CmdPipelineBarrier(CommandBuffer commandBuffer, PipelineStageFlags srcStageMask, PipelineStageFlags dstStageMask, uint dependencyFlags, uint memoryBarrierCount, MemoryBarrier* pMemoryBarriers, uint bufferMemoryBarrierCount, BufferMemoryBarrier* pBufferMemoryBarriers, uint imageMemoryBarrierCount, ImageMemoryBarrier* pImageMemoryBarriers)
        => _cmdPipelineBarrier(commandBuffer, srcStageMask, dstStageMask, dependencyFlags, memoryBarrierCount, pMemoryBarriers, bufferMemoryBarrierCount, pBufferMemoryBarriers, imageMemoryBarrierCount, pImageMemoryBarriers);

    /// <summary>录制「缓冲→图像拷贝」（vkCmdCopyBufferToImage；CPU 上传像素到纹理）。</summary>
    public static unsafe void CmdCopyBufferToImage(CommandBuffer commandBuffer, Buffer srcBuffer, Image dstImage, ImageLayout dstImageLayout, uint regionCount, BufferImageCopy* pRegions)
        => _cmdCopyBufferToImage(commandBuffer, srcBuffer, dstImage, dstImageLayout, regionCount, pRegions);

    /// <summary>录制「图像→缓冲拷贝」（vkCmdCopyImageToBuffer；GPU 回读像素）。</summary>
    public static unsafe void CmdCopyImageToBuffer(CommandBuffer commandBuffer, Image srcImage, ImageLayout srcImageLayout, Buffer dstBuffer, uint regionCount, BufferImageCopy* pRegions)
        => _cmdCopyImageToBuffer(commandBuffer, srcImage, srcImageLayout, dstBuffer, regionCount, pRegions);

    /// <summary>录制「图像→图像拷贝」（vkCmdCopyImage；格式兼容、无缩放）。</summary>
    public static unsafe void CmdCopyImage(CommandBuffer commandBuffer, Image srcImage, ImageLayout srcImageLayout, Image dstImage, ImageLayout dstImageLayout, uint regionCount, ImageCopy* pRegions)
        => _cmdCopyImage(commandBuffer, srcImage, srcImageLayout, dstImage, dstImageLayout, regionCount, pRegions);

    /// <summary>录制「图像缩放拷贝」（vkCmdBlitImage；可缩放/格式转换，filter 指定过滤）。</summary>
    public static unsafe void CmdBlitImage(CommandBuffer commandBuffer, Image srcImage, ImageLayout srcImageLayout, Image dstImage, ImageLayout dstImageLayout, uint regionCount, ImageBlit* pRegions, Filter filter)
        => _cmdBlitImage(commandBuffer, srcImage, srcImageLayout, dstImage, dstImageLayout, regionCount, pRegions, filter);

    /// <summary>录制「清屏颜色图像」（vkCmdClearColorImage；渲染通道外清屏）。</summary>
    public static unsafe void CmdClearColorImage(CommandBuffer commandBuffer, Image image, ImageLayout imageLayout, ClearColorValue* pColor, uint rangeCount, ImageSubresourceRange* pRanges)
        => _cmdClearColorImage(commandBuffer, image, imageLayout, pColor, rangeCount, pRanges);

    // KHR WSI 扩展（数组重载以对齐 Silk.NET Khr* 调用点）
}

using System.Diagnostics;
using LingFan.Media.GPUShare.Vulkan;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanRenderer 的 Recovery 职责段（部分类，由 VulkanRenderer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Attach/Present/Clear/Detach/Dispose）在主文件；全部同步原生调用，_gate 串行化。
internal sealed unsafe partial class VulkanRenderer
{
    /// <summary>
    /// 统一的设备丢失检测——结果为 <see cref="Result.ErrorDeviceLost"/> 时
    /// 记录错误并抛中立 <see cref="GpuDeviceLostException"/> 供会话层释放并重建；其余结果不处理。
    /// </summary>
    /// <param name="result">Vulkan API 返回值。</param>
    /// <param name="operation">用于日志与异常消息的操作名。</param>
    private void ThrowIfDeviceLost(Result result, string operation)
    {
        if (result != Result.ErrorDeviceLost) return;
        _logger.LogError("Vulkan 设备丢失（{Operation}）。", operation);
        throw new GpuDeviceLostException($"Vulkan 设备已丢失（{operation} 返回 VK_ERROR_DEVICE_LOST）。需释放并重建渲染会话。");
    }

    // CompositeAlpha 四级回退——Opaque > Inherit > PreMultiplied > PostMultiplied，
    // 逐位测试支持标志，全不中（违反规范的驱动）才回退 Opaque 让 vkCreateSwapchain 报出明确错误。

    /// <summary>
    /// 信号量恢复——当 AcquireNextImage 成功但后续命令记录/提交失败时，
    /// 提交一个最小命令缓冲来消费 <c>_imageAvailableSemaphore</c> 并呈现，
    /// 否则下次 AcquireNextImage 将因信号量已信号而永久超时（渲染器永久卡死）。
    /// </summary>
    /// <param name="imageIndex">已获取的 SwapChain 图像索引。</param>
    private void RecoverSemaphore(uint imageIndex)
    {
        try
        {
            // 若 RecordAndSubmitFrame 在 BeginCommandBuffer 后、EndCommandBuffer 前抛异常，
            // 命令缓冲处于 recording 状态。Vulkan 规范规定 vkResetCommandBuffer 不能用于
            // recording 状态的命令缓冲——会返回 VK_NOT_READY，后续 BeginCommandBuffer 也失败，
            // 信号量永久泄漏。先调用 EndCommandBuffer（忽略错误）将其移出 recording 状态：
            // 成功→executable 状态；失败→invalid 状态。两种状态均可被 ResetCommandBuffer 重置。
            VulkanNative.EndCommandBuffer(_commandBuffer);
            VulkanNative.ResetCommandBuffer(_commandBuffer, CommandBufferResetFlags.None);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            if (VulkanNative.BeginCommandBuffer(_commandBuffer, ref beginInfo) != Result.Success) return;

            // 将 SwapChain 图像转为 PresentSrc 布局（最小可呈现状态）
            // srcStage=TransferBit 与本提交的信号量 waitDstStageMask（TransferBit）对齐形成依赖链
            Image swapchainImage = _swapchainImages[imageIndex];
            TransitionImageLayout(swapchainImage, ImageLayout.Undefined, ImageLayout.PresentSrcKhr,
                AccessFlags.None, AccessFlags.MemoryReadBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);

            if (VulkanNative.EndCommandBuffer(_commandBuffer) != Result.Success) return;

            // 提交以消费 _imageAvailableSemaphore，信号 _renderFinishedSemaphores[imageIndex]
            CommandBuffer cmd = _commandBuffer;
            Semaphore imgAvail = _imageAvailableSemaphore;
            Semaphore renderFin = _renderFinishedSemaphores[imageIndex];
            // 同 RecordAndSubmitFrame——TransferBit
            PipelineStageFlags waitStage = PipelineStageFlags.TransferBit;

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imgAvail,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &renderFin,
            };

            VulkanNative.QueueSubmit(_queue, 1, &submitInfo, default);
            VulkanNative.QueueWaitIdle(_queue);

            // 呈现以消费 _renderFinishedSemaphores[imageIndex]
            SwapchainKHR swap = _swapchain;
            uint idx = imageIndex;
            _presentInfoArr[0] = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFin,
                SwapchainCount = 1,
                PSwapchains = &swap,
                PImageIndices = &idx,
            };

            VulkanNative.QueuePresentKHR(_queue, _presentInfoArr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "信号量恢复失败，渲染器可能需要重新 Attach");
        }
    }
}

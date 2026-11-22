using System.Diagnostics;
using LingFan.Media.GPUShare.Vulkan;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanRenderer 的清屏职责段（部分类，按职责拆分，编译期等价）。
// Clear 的 acquire→清色→present 全链与设备丢失类型化上抛；Attach/Present/Dispose 在主文件。
internal sealed unsafe partial class VulkanRenderer
{
    /// <inheritdoc/>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_disposed || !_attached || _swapchain.Handle == 0) return;

            // 有限超时
            Span<uint> imageIndexSpan = stackalloc uint[1];
            Result result = VulkanNative.AcquireNextImageKHR(
                _device, _swapchain, AcquireTimeoutNs,
                _imageAvailableSemaphore, default, imageIndexSpan);
            if (result == Result.Timeout)
            {
                _logger.LogWarning("vkAcquireNextImage 超时，跳过 Clear。");
                return;
            }
            if (result == Result.ErrorOutOfDateKhr)
            {
                // 同 Present——过期即重建，跳过本次 Clear
                RecreateSwapchain();
                return;
            }
            if (result == Result.ErrorDeviceLost)
            {
                // 同 Present——Clear 也须类型化上抛（与 D3D11 Clear 的 ThrowIfDeviceLost 对称），
                // 否则设备丢失被静默吞掉，会话层无从感知与重建。
                _logger.LogError("Vulkan 设备丢失（vkAcquireNextImage/Clear）。");
                throw new GpuDeviceLostException("Vulkan 设备已丢失（Clear 的 vkAcquireNextImage 返回 VK_ERROR_DEVICE_LOST）。需释放并重建渲染会话。");
            }
            if (result != Result.Success && result != Result.SuboptimalKhr) return;
            uint imageIndex = imageIndexSpan[0];

            try
            {
                Image swapchainImage = _swapchainImages[imageIndex];

                CommandBufferBeginInfo beginInfo = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                // 检查 BeginCommandBuffer 返回值
        result = VulkanNative.BeginCommandBuffer(_commandBuffer, ref beginInfo);
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkBeginCommandBuffer 失败: {result}");

                // Undefined→TransferDst，srcStage=TransferBit 与信号量 waitDstStageMask 对齐——同 RecordAndSubmitFrame
                TransitionImageLayout(swapchainImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    AccessFlags.None, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);

                ClearColorValue clearColor = new() { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 0 };
                ImageSubresourceRange range = new()
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                };

                VulkanNative.CmdClearColorImage(_commandBuffer, swapchainImage,
                    ImageLayout.TransferDstOptimal, &clearColor, 1, &range);

                // TransferDst→PresentSrc
                TransitionImageLayout(swapchainImage, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr,
                    AccessFlags.TransferWriteBit, AccessFlags.MemoryReadBit,
                    PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);

                // 检查 EndCommandBuffer 返回值
                result = VulkanNative.EndCommandBuffer(_commandBuffer);
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkEndCommandBuffer 失败: {result}");

                CommandBuffer cmd = _commandBuffer;
                Semaphore imgAvail = _imageAvailableSemaphore;
                Semaphore renderFin = _renderFinishedSemaphores[imageIndex];
                // TransferBit 而非 ColorAttachmentOutputBit——同 RecordAndSubmitFrame
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

                // 检查 QueueSubmit 返回值
                result = VulkanNative.QueueSubmit(_queue, 1, &submitInfo, default);
                // Clear 的 QueueSubmit 同样须类型化——否则设备丢失变泛化异常
                // 被下方 catch 过滤器捕获吞掉（对称于 Present 侧 RecordAndSubmitFrame）。
                ThrowIfDeviceLost(result, "vkQueueSubmit/Clear");
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkQueueSubmit 失败: {result}");

                // 已知性能限制——同 RecordAndSubmitFrame
                // 同 Present——GPU 执行中 TDR 从 WaitIdle 浮现
                ThrowIfDeviceLost(VulkanNative.QueueWaitIdle(_queue), "vkQueueWaitIdle/Clear");

                // 复用预分配数组
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

                // 检查 QueuePresent 返回值
                result = VulkanNative.QueuePresentKHR(_queue, _presentInfoArr);
                if (result == Result.ErrorOutOfDateKhr)
                {
                    // Clear 的 Present 阶段过期——重建（信号量一并重建，无残留）
                    RecreateSwapchain();
                }
                else if (result == Result.ErrorDeviceLost)
                {
                    // 同 Present——类型化上抛（catch 过滤器放行，见下）
                    _logger.LogError("Vulkan 设备丢失（vkQueuePresent/Clear）。");
                    throw new GpuDeviceLostException("Vulkan 设备已丢失（Clear 的 vkQueuePresent 返回 VK_ERROR_DEVICE_LOST）。需释放并重建渲染会话。");
                }
                else if (result != Result.Success && result != Result.SuboptimalKhr)
                    _logger.LogWarning("vkQueuePresent 失败: {Result}", result);
            }
            catch (Exception ex) when (ex is not GpuDeviceLostException)
            {
                // 信号量恢复——同 Present，Clear 异常后也需消费信号量。
                // GpuDeviceLostException 不在此吞掉——设备已死，信号量恢复无意义
                // 且必须让会话层感知（catch 过滤器放行类型化异常）。
                _logger.LogWarning(ex, "Clear 操作失败");
                RecoverSemaphore(imageIndex);
            }
        }
    }
}

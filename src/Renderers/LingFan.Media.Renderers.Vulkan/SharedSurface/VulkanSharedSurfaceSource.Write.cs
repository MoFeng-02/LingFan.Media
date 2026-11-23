using LingFan.Media.GPUShare.Vulkan;
using LingFan.Media.Renderers.Shared;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanSharedSurfaceSource 的 Write 职责段（部分类，由 VulkanSharedSurfaceSource.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/契约属性/Dispose）在主文件；平台专属路径见 Android/Apple partial。
internal sealed unsafe partial class VulkanSharedSurfaceSource
{
    /// <inheritdoc/>
    public bool TryWriteFrame(VideoFrame frame, out SharedGpuSurfaceDescriptor descriptor)
    {
        // 停摆看门狗：进入即置位并记录阶段，返回（含异常路径）时复位。
        EnsureStallTimer();
        Volatile.Write(ref _stageActive, 1);
        MarkStage("enter");
        try
        {
            return TryWriteFrameCore(frame, out descriptor);
        }
        catch (OperationCanceledException)
        {
            // 取消是管线优雅退出的信号，语义上不是「本帧写不进去」——继续上抛，由调用方结束循环。
            throw;
        }
        catch (Exception ex)
        {
            // 契约（ISharedGpuSurfaceSource.TryWriteFrame）：失败返回 false 属正常降级路径，
            // 不应抛异常打断管线。原生/托管异常一律在此收拢为丢帧。
            _logger.LogWarning(ex, "[VULKAN-SHARED] 写入共享表面失败，丢弃本帧。");
            descriptor = default;
            return false;
        }
        finally
        {
            MarkStage("done");
            Volatile.Write(ref _stageActive, 0);
        }
    }

    private bool TryWriteFrameCore(VideoFrame frame, out SharedGpuSurfaceDescriptor descriptor)
    {
        descriptor = default;
        if (_disposed)
            return false;
        if (frame.Resource is null)
            return false;
        Interlocked.Increment(ref _writeSeq);

        // 每帧重置 AHB 自提交标志（仅 Android AHB 零拷贝路径在 TryRecordAhbConversion 内部分步自提交后置位）。
        _ahbSelfSubmitted = false;
        // 双槽形态（Android VkImage 直采）：本帧翻转到另一槽写入并交付（绕开 Skia Ganesh 对同 VkImage 的内容缓存）。
        // 判据与 _slotCount 同源——单槽形态若翻转会越界到不存在的槽 1（_sharedMemorySizes[1]/_sharedImages[1] 均为空）。
        if (_slotCount > 1)
            _sharedActive ^= 1;

        int w, h;
        bool recorded;
        string? path = null;
        if (frame.Resource is GPUShare.Android.AndroidHardwareBufferFrameResource ahb)
        {
            // 真零拷贝路径（Layer 3）：AHB 经 Vulkan 导入后在 GPU 内直采/转换，零 CPU 像素拷贝。
            if (!_announcedZeroCopy)
            {
                _announcedZeroCopy = true;
                _logger.LogInformation("[VULKAN-SHARED] 路径锁定=ZERO-COPY(AHB→GPU) — GPU 出帧，无 CPU 像素拷贝。");
            }
            path = "ZERO-COPY(AHB→GPU)";
            w = ahb.Width;
            h = ahb.Height;

            // Android RGBA 直采样捷径（0 队列提交）：导入图像直接交 Skia 采样，不再由本源做转换+拷贝
            // 双提交。防回归：本源提交与 Skia 帧提交共用同一 VkQueue 时，逐帧 AHB 导入+双提交在 Adreno 上
            // 会以极低频率随机触发 vkQueueSubmit ErrorInitializationFailed（规范外错误码），并殃及同队列的
            // Skia 提交 → Avalonia 渲染循环停摆 → 画面永久定格。
            // 导入是纯 CPU 调用（无队列命令），不产生该错误触发源。失败或 YCbCr 外部格式 → 落回旧转换路径。
            if (_isAndroid && TryBuildAhbDirectDescriptor(ahb, w, h, frame.RotationDegrees, out descriptor))
                return true;

            MarkStage("EnsureSharedSurface");
            EnsureSharedSurface(w, h);
            _pipeline!.EnsureOffscreenResources(_surfaceVkFormat, new Extent2D((uint)w, (uint)h), _isAndroid ? _convertView : _sharedImageViews[0]);
            MarkStage("AhbConvert.Record");
            recorded = TryRecordAhbConversion(ahb, w, h);
        }
        else if (frame.Resource is SoftwareFrameResource sw)
        {
            // 软帧路径：YUV 平面经 CPU 上传 + YUV→RGB shader 写入共享离屏表面（仍 GPU 上屏，但非真零拷贝——
            // 解码侧已做 CPU 像素提取，此处仅为「软帧上屏」）。
            if (!_announcedSoft)
            {
                _announcedSoft = true;
                _logger.LogInformation("[VULKAN-SHARED] 路径锁定=SOFT-UPLOAD(CPU YUV→GPU 上传) — 软帧上屏，非零拷贝。");
            }
            path = "SOFT-UPLOAD(CPU→GPU)";
            w = sw.Width;
            h = sw.Height;
            MarkStage("EnsureSharedSurface");
            EnsureSharedSurface(w, h);
            _pipeline!.EnsureOffscreenResources(_surfaceVkFormat, new Extent2D((uint)w, (uint)h), _isAndroid ? _convertView : _sharedImageViews[0]);
            MarkStage("SoftwareUpload.Record");
            recorded = TryRecordSoftwareUpload(sw, w, h);
        }
        else
        {
            return false; // 不支持的帧类型 → 交回回退
        }

        if (!recorded)
            return false;

        // Android AHB 零拷贝路径已在 TryRecordAhbConversion 内部分步自提交（隔离 Mali DEVICE_LOST 发生步），
        // 命令缓冲不归公共提交段管理，跳过 EndCommandBuffer/QueueSubmit/WaitForFences，直接导出描述符。
        if (!_ahbSelfSubmitted)
        {
            // 公共提交段：EndCommandBuffer → QueueSubmit（可选信号量握手）→ 有限超时 WaitForFences。
            MarkStage("Submit.EndCommandBuffer");
            Result result = VulkanNative.EndCommandBuffer(_commandBuffer);
            if (result != Result.Success)
            {
                _logger.LogWarning("Vulkan 共享表面 EndCommandBuffer 失败：{Result}", result);
                return false;
            }

            // 生产者等待消费方归还（ConsumerSignal）→ 写完后 signal 消费方等待（ConsumerWait）。
            // None 模型（Android 默认 VulkanNativeImage 形态）：生产与消费共用同一 VkDevice、同一图形队列，
            // 同队列提交天然按提交序串行，无需跨端信号量握手；仅以 Fence 保证自身 GPU 写完成，
            // 末屏障使写入对后续采样可见（见 CopyToSharedImage）。
            Fence fence = _frameFence;
            CommandBuffer cmdBuf = _commandBuffer;
            VulkanNative.ResetFences(_device, 1, &fence);
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmdBuf,
            };
            if (_syncMode != SharedGpuSyncMode.None)
            {
                Semaphore waitSem = _consumerSignalSem;
                Semaphore signalSem = _consumerWaitSem;
                PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
                submitInfo.WaitSemaphoreCount = 1;
                submitInfo.PWaitSemaphores = &waitSem;
                submitInfo.PWaitDstStageMask = &waitStage;
                submitInfo.SignalSemaphoreCount = 1;
                submitInfo.PSignalSemaphores = &signalSem;
            }

            MarkStage("Submit.QueueSubmit");
            result = VulkanNative.QueueSubmit(_queue, 1, &submitInfo, (nint)fence.Handle);
            if (result != Result.Success)
            {
                _logger.LogWarning("Vulkan 共享表面 QueueSubmit 失败：{Result}", result);
                return false;
            }

            // 有限超时等待 GPU 完成（与 D3D11 16ms keyed mutex 超时对称）——超时=消费方未归还 → 丢帧。
            MarkStage("Submit.WaitForFences");
            Result waitR = VulkanNative.WaitForFences(_device, 1, &fence, 1u, WriteWaitTimeoutNs);
            if (waitR == Result.Timeout)
            {
                _logger.LogTrace("Vulkan 共享表面等待消费方归还超时，跳过本帧。");
                return false;
            }
            if (waitR != Result.Success)
            {
                _logger.LogWarning("Vulkan 共享表面 WaitForFences 失败：{Result}", waitR);
                return false;
            }
        }

        // 0拷贝帧出参 / 转移口：将本帧共享离屏表面的外部内存句柄 + 版本 + 同步模型打包交合成器
        // 此处即「帧转移不拷贝」的交付点：调用方（合成器）仅持 SharedGpuSurfaceDescriptor，不感知源像素布局。
        // 零拷贝 = 本描述符携带外部内存句柄（fd / HANDLE / IOSurface），而非像素副本；软帧同样经此口交付。
        // 【兜底】绝不允许带着 MemorySize=0 交付 —— Avalonia 导入 OPAQUE_FD 时以此做严格相等校验，
        // 0 必然抛 "Invalid memory size"、导入连续失败 → 整链回退 Skia 不出画。
        // 正常路径已在分配点记录；此处现查只是防御，代价一次 vkGetImageMemoryRequirements（纳秒级）。
        if (_sharedMemorySize == 0)
        {
            MemoryRequirements fallbackReq;
            VulkanNative.GetImageMemoryRequirements(_device, _sharedImages[_sharedActive], &fallbackReq);
            _sharedMemorySizes[_sharedActive] = fallbackReq.Size;
            _logger.LogWarning(
                "[VULKAN-SHARED] MemorySize 在分配点未记录，交付前现查兜底={Size}（应检查分配点为何漏记）。",
                fallbackReq.Size);
        }
        // Trace 级：呈现线程为实时线程，周期性 Information 写 logcat（双 provider）可达数十 ms，
        // 会周期性阻塞 Present（卡顿诱因之一），调高日志级别即可查看。
        if ((_writtenFrames % FrameLogInterval) == 0)
            _logger.LogTrace(
                "[VULKAN-SHARED] 转移口 帧#{N} 路径={Path} 出参Kind={Kind} version={Ver} sync={Sync} {W}x{H} mem={Mem} usage=0x{Usage:X} flags=0x{Flags:X}",
                _writtenFrames, path, _handleKind, _version, _syncMode, w, h, _sharedMemorySize,
                (uint)_sharedUsage, (uint)_sharedFlags);
        _writtenFrames++;

        if (_handleKind == SharedGpuHandleKind.VulkanNativeImage)
        {
            // 同 device 直采样交付：句柄即原生 VkImage，配套 Native* 字段描述内存绑定与采样前置状态
            //（布局=ShaderReadOnlyOptimal、格式/用法/队列族如实上报）。MemorySize/MemoryOffset 兼作
            // 消费方包装所需的分配尺寸/偏移。图像生命周期归本源；尺寸变化时 _version 递增，
            // 消费方据此重建包装。
            descriptor = new SharedGpuSurfaceDescriptor(
                (nint)_sharedImages[_sharedActive].Handle,
                _handleKind,
                w, h,
                _surfaceFormatEnum,
                _version,
                _syncMode,
                _sharedMemorySize,
                0,
                NativeImage: (nint)_sharedImages[_sharedActive].Handle,
                NativeDeviceMemory: (nint)_sharedMemories[_sharedActive].Handle,
                NativeImageLayout: (uint)ImageLayout.ShaderReadOnlyOptimal,
                NativeVkFormat: (uint)_surfaceVkFormat,
                NativeQueueFamilyIndex: _queueFamilyIndex,
                NativeImageUsage: (uint)_sharedUsage,
                NativeImageTiling: (uint)ImageTiling.Optimal,
                RotationDegrees: frame.RotationDegrees);
            return true;
        }

        descriptor = new SharedGpuSurfaceDescriptor(
            _exportedMemoryHandle,
            _handleKind,
            w, h,
            _surfaceFormatEnum,
            _version,
            _syncMode,
            _sharedMemorySize,
            0,
            RotationDegrees: frame.RotationDegrees);
        return true;
    }

}

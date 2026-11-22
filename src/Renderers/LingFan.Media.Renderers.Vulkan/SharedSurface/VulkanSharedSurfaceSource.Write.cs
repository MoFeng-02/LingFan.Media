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
            // 双提交。防回归：我方提交与 Skia 帧提交共用同一 VkQueue 时，逐帧 AHB 导入+双提交在 Adreno 上
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
            // None 模型（Android）：移动端驱动不支持二进制外部信号量导出，提交不带信号量，
            // 由 Avalonia 合成器（UpdateAsync）自管跨端同步；仅以 Fence 保证自身 GPU 写完成。
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

    /// <summary>记录软帧上传（CPU YUV 平面 → GPU 纹理 → YUV→RGB shader 写入共享离屏表面）。返回是否成功记录。</summary>
    private bool TryRecordSoftwareUpload(SoftwareFrameResource sw, int w, int h)
    {
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        if (VulkanNative.BeginCommandBuffer(_commandBuffer, ref beginInfo) != Result.Success)
        {
            _logger.LogWarning("Vulkan 共享表面 BeginCommandBuffer（软帧）失败。");
            return false;
        }
        try
        {
            _pipeline!.PresentOffscreen(sw, _commandBuffer, (0, 0, w, h), (0f, 0f, 1f, 1f));

            // Android：软帧已渲进 plain 内部 _convertImage（离屏 RenderPass FinalLayout=ColorAttachmentOptimal），
            // 拷进 AHB 离屏 _sharedImage（仅 TRANSFER_DST，导出交合成器）。
            if (_isAndroid)
                CopyToSharedImage(_commandBuffer, ImageLayout.ColorAttachmentOptimal, w, h);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vulkan 共享表面软帧上传记录失败。");
            VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
            return false;
        }
    }

    /// <summary>
    /// Android 稳健层：把已渲染进 plain 内部 <see cref="_convertImage"/> 的帧经 vkCmdCopyImage 拷进 AHB 离屏
    /// <see cref="_sharedImage"/>（仅 TRANSFER_DST，导出交合成器）。<see cref="_sharedImage"/> 为 AHB 支持内存，
    /// 直接作 color attachment 在 Mali 上触发 GROUP_ERROR_FATAL / DEVICE_LOST，故一律经内部图中转。
    /// 软帧路径 srcLayout=ColorAttachmentOptimal（离屏 RenderPass FinalLayout）；AHB 路径 srcLayout=TransferSrcOptimal
    /// （转换器 Convert 末态）。<see cref="_sharedImage"/> 首帧由 Undefined 转入 TransferDstOptimal，后续帧保持
    /// TransferDstOptimal（由 <see cref="_sharedCopyReady"/> 追踪，跨命令缓冲持久）。
    /// </summary>
    private void CopyToSharedImage(CommandBuffer cmd, ImageLayout srcLayout, int w, int h)
    {
        // 1) _convertImage → TransferSrcOptimal（软帧路径需转；AHB 路径已是，跳过）。
        if (srcLayout != ImageLayout.TransferSrcOptimal)
        {
            ImageMemoryBarrier srcBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = srcLayout,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = ~0u,
                DstQueueFamilyIndex = ~0u,
                Image = _convertImage,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            VulkanNative.CmdPipelineBarrier(cmd, PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &srcBarrier);
        }

        // 2) _sharedImage → TransferDstOptimal（首帧 Undefined，后续帧已是 TransferDstOptimal）。
        //    首帧无前序写入可等（TopOfPipe + 0 访问）；后续帧须等上一帧 TRANSFER 写入完成再覆盖。
        //    Android（VulkanNativeImage→Skia 直采样）：上一帧图像已按 ShaderReadOnlyOptimal 交付给
        //    消费方采样（采样不改布局），本帧覆盖屏障的旧布局统一用 Undefined——规范允许的
        //    「不保留旧内容」语义（与图像实际状态无关恒合法），整幅重写故无内容损失；
        //    执行序由同队列（生产/消费共用同一 VkQueue）隐式按提交序串行保证。
        bool firstCopy = !_sharedCopyReady[_sharedActive];
        bool dstFromUndefined = firstCopy || _isAndroid;
        ImageLayout dstOld = dstFromUndefined ? ImageLayout.Undefined : ImageLayout.TransferDstOptimal;
        PipelineStageFlags dstSrcStage = dstFromUndefined ? PipelineStageFlags.TopOfPipeBit : PipelineStageFlags.TransferBit;
        AccessFlags dstSrcAccess = dstFromUndefined ? 0 : AccessFlags.TransferWriteBit;
        ImageMemoryBarrier dstBarrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = dstSrcAccess,
            DstAccessMask = AccessFlags.TransferWriteBit,
            OldLayout = dstOld,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = ~0u,
            DstQueueFamilyIndex = ~0u,
            Image = _sharedImages[_sharedActive],
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        VulkanNative.CmdPipelineBarrier(cmd, dstSrcStage,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &dstBarrier);

        // 3) vkCmdCopyImage：_convertImage(TransferSrcOptimal) → _sharedImage(TransferDstOptimal)。
        ImageCopy region = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            SrcOffset = new Offset3D(0, 0, 0),
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            DstOffset = new Offset3D(0, 0, 0),
            Extent = new Extent3D((uint)w, (uint)h, 1),
        };
        VulkanNative.CmdCopyImage(cmd, _convertImage, ImageLayout.TransferSrcOptimal,
            _sharedImages[_sharedActive], ImageLayout.TransferDstOptimal, 1, &region);

        // 4) Android（VulkanNativeImage→Skia 直采样）：交付前把 _sharedImage 转入
        //    ShaderReadOnlyOptimal 并使拷贝写入对后续采样可见（dstStage 覆盖片元/计算着色器读取）。
        //    消费方按 GRVkImageInfo.ImageLayout 认定布局——采样 ShaderReadOnlyOptimal 无需再转换；
        //    同一 VkQueue 上晚于此提交的采样命令（Skia 帧绘制）自动落入本屏障的可视性作用域。
        if (_isAndroid)
        {
            ImageMemoryBarrier readyBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = ~0u,
                DstQueueFamilyIndex = ~0u,
                Image = _sharedImages[_sharedActive],
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            VulkanNative.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                0, 0, null, 0, null, 1, &readyBarrier);
        }

        // 标记当前槽位已进入交付布局（Android=ShaderReadOnlyOptimal / 其余=TransferDstOptimal，
        // 供后续帧复用，避免重复 Undefined 重排）。
        _sharedCopyReady[_sharedActive] = true;
    }
}

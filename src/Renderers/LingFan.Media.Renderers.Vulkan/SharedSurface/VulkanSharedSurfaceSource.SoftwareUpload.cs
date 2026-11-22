using LingFan.Media.GPUShare.Vulkan;
using LingFan.Media.Renderers.Shared;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanSharedSurfaceSource 的软帧上传职责段（部分类，按职责拆分，编译期等价）。
// 软帧 YUV/RGBA 平面上传与向共享离屏图像的拷贝；帧写入编排在 Write 主段。
internal sealed unsafe partial class VulkanSharedSurfaceSource
{
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

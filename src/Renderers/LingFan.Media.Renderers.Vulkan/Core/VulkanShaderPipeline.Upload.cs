using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanShaderPipeline 的 Upload 职责段（部分类，由 VulkanShaderPipeline.cs 按职责拆分，编译期等价）。
// 字段与核心编排（RenderFrameTo/Dispose）在主文件；全部同步原生调用，由 VulkanRenderer._gate 串行化。
internal sealed unsafe partial class VulkanShaderPipeline
{
    /// <summary>计算软帧上传到离屏 staging 缓冲所需的字节数（与 <see cref="UploadPlanes"/> 的平面字节总和一致）。</summary>
    private static int CalculateStagingSize(SoftwareFrameResource sw)
    {
        int w = sw.Width, h = sw.Height;
        return sw.Format switch
        {
            PixelFormat.BGRA32 or PixelFormat.RGBA32 => w * h * 4,
            PixelFormat.NV12 or PixelFormat.NV21 =>
                w * h + (((w + 1) >> 1) * ((h + 1) >> 1) * 2),
            PixelFormat.YUV420P =>
                w * h + 2 * (((w + 1) >> 1) * ((h + 1) >> 1)),
            PixelFormat.YUV422P =>
                w * h + 2 * (((w + 1) >> 1) * h),
            PixelFormat.YUV444P => w * h * 3,
            _ => w * h * 4,
        };
    }

    /// <summary>确保离屏路径自有 staging 缓冲就绪（创建 + 持久映射），与 SwapChain 路径的 staging 解耦。</summary>
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
            throw new InvalidOperationException($"vkCreateBuffer（离屏 staging）失败: {result}");

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

        result = VulkanNative.AllocateMemory(_device, ref memInfo, null, out _stagingMemory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory（离屏 staging）失败: {result}");

        result = VulkanNative.BindBufferMemory(_device, _stagingBuffer, _stagingMemory, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindBufferMemory（离屏 staging）失败: {result}");

        // 持久映射整段 staging（HOST_COHERENT，写入经一致性语义自动对 GPU 可见），长期复用。
        void* mapped = null;
        Result mapResult = VulkanNative.MapMemory(_device, _stagingMemory, 0, memReq.Size, 0, &mapped);
        if (mapResult != Result.Success)
            throw new InvalidOperationException($"vkMapMemory（离屏 staging 持久映射）失败: {mapResult}");
        _stagingMapped = mapped;

        // 记录 buffer 创建大小（requiredSize），不能记 memReq.Size（≥ requiredSize，含对齐填充）。
        _stagingBufferSize = requiredSize;
    }

    private void UploadPlanes(SoftwareFrameResource sw, Buffer stagingBuffer, void* stagingMapped, ulong stagingBufferSize, CommandBuffer cmd)
    {
        int w = sw.Width, h = sw.Height;
        var data = sw.Data.Span;
        Span<byte> staging = new(stagingMapped, (int)stagingBufferSize);

        switch (sw.Format)
        {
            case PixelFormat.BGRA32:
            case PixelFormat.RGBA32:
            {
                int rowBytes = w * 4;
                int srcRowPitch = sw.Stride > 0 ? sw.Stride : rowBytes;
                int dataSize = w * h * 4;
                if ((ulong)dataSize > stagingBufferSize)
                    throw new InvalidOperationException($"staging 缓冲不足：需要 {dataSize}，仅有 {stagingBufferSize}。");

                if (srcRowPitch == rowBytes)
                {
                    data.Slice(0, dataSize).CopyTo(staging.Slice(0, dataSize));
                }
                else
                {
                    for (int y = 0; y < h; y++)
                        data.Slice(y * srcRowPitch, rowBytes).CopyTo(staging.Slice(y * rowBytes, rowBytes));
                }

                TransitionImageLayout(cmd, _planeImages[0], ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                CopyBufferToImage(cmd, stagingBuffer, 0, _planeImages[0], (uint)w, (uint)h);
                break;
            }

            case PixelFormat.YUV420P:
            case PixelFormat.YUV422P:
            case PixelFormat.YUV444P:
            {
                (int cw, int ch) = sw.Format switch
                {
                    PixelFormat.YUV444P => (w, h),
                    PixelFormat.YUV422P => ((w + 1) >> 1, h),
                    _ => ((w + 1) >> 1, (h + 1) >> 1),
                };
                int ySize = w * h;
                int cSize = cw * ch;
                int total = ySize + 2 * cSize;
                if ((ulong)total > stagingBufferSize)
                    throw new InvalidOperationException($"staging 缓冲不足：需要 {total}，仅有 {stagingBufferSize}。");

                data.Slice(0, ySize).CopyTo(staging.Slice(0, ySize));
                data.Slice(ySize, cSize).CopyTo(staging.Slice(ySize, cSize));
                data.Slice(ySize + cSize, cSize).CopyTo(staging.Slice(ySize + cSize, cSize));

                TransitionImageLayout(cmd, _planeImages[0], ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                CopyBufferToImage(cmd, stagingBuffer, 0, _planeImages[0], (uint)w, (uint)h);

                TransitionImageLayout(cmd, _planeImages[1], ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                CopyBufferToImage(cmd, stagingBuffer, (ulong)ySize, _planeImages[1], (uint)cw, (uint)ch);

                TransitionImageLayout(cmd, _planeImages[2], ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                CopyBufferToImage(cmd, stagingBuffer, (ulong)(ySize + cSize), _planeImages[2], (uint)cw, (uint)ch);
                break;
            }

            case PixelFormat.NV12:
            case PixelFormat.NV21:
            {
                int ySize = w * h;
                int uvW = (w + 1) >> 1;
                int uvH = (h + 1) >> 1;
                int uvSize = uvW * uvH * 2;
                int total = ySize + uvSize;
                if ((ulong)total > stagingBufferSize)
                    throw new InvalidOperationException($"staging 缓冲不足：需要 {total}，仅有 {stagingBufferSize}。");

                data.Slice(0, ySize).CopyTo(staging.Slice(0, ySize));
                data.Slice(ySize, uvSize).CopyTo(staging.Slice(ySize, uvSize));

                TransitionImageLayout(cmd, _planeImages[0], ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                CopyBufferToImage(cmd, stagingBuffer, 0, _planeImages[0], (uint)w, (uint)h);

                TransitionImageLayout(cmd, _planeImages[1], ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                CopyBufferToImage(cmd, stagingBuffer, (ulong)ySize, _planeImages[1], (uint)uvW, (uint)uvH);
                break;
            }

            default:
                throw new NotSupportedException($"Vulkan Shader 管线不支持像素格式 {sw.Format}。");
        }
    }

    private void CopyBufferToImage(CommandBuffer cmd, Buffer srcBuffer, ulong offset, Image dstImage, uint width, uint height)
    {
        BufferImageCopy region = new()
        {
            BufferOffset = offset,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(width, height, 1),
        };
        VulkanNative.CmdCopyBufferToImage(cmd, srcBuffer, dstImage, ImageLayout.TransferDstOptimal, 1, &region);
    }

    private void TransitionImageLayout(
        CommandBuffer cmd, Image image,
        ImageLayout oldLayout, ImageLayout newLayout,
        AccessFlags srcAccess, AccessFlags dstAccess,
        PipelineStageFlags srcStage, PipelineStageFlags dstStage)
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

        VulkanNative.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
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
}

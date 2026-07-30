using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan.Tests;

/// <summary>
/// VK-ZERO 端到端：<see cref="VulkanRenderer.BlitVulkanImageResource"/> 把 GPU 纹理（VkImage）
/// 零拷贝 copy 到目标图像，像素经 readback 验证与源图案一致——真正验证零拷贝正确，
/// 而非仅"不抛异常"。本机有 Vulkan（核显/独显），headless 真实驱动。
/// </summary>
public unsafe class BlitVulkanImageResourceTests
{
    [Fact]
    [Trait("Category", "RequiresGPU")]
    public void Blit_SameSizeBgra_CopiesPixelsVerifiedByReadback()
    {
        using var ctx = new VulkanTestContext();
        const int w = 64, h = 64;

        VulkanImageResourceTests.CreateTestImage(ctx, w, h, out var srcImage, out var srcMem);
        VulkanImageResourceTests.CreateTestImage(ctx, w, h, out var dstImage, out var dstMem);
        try
        {
            // 渲染器拥有自建命令池，Dispose 负责释放（先于 ctx.Dispose 销毁 Device，顺序正确）
            using var renderer = VulkanImageResourceTests.CreateHeadlessRenderer(ctx);
            renderer.CreateCommandPoolAndBuffer();
            var cmd = VulkanImageResourceTests.GetRendererCommandBuffer(renderer);

            // 上传全红图案到源图像（BGRA: B=0,G=0,R=255,A=255）
            UploadPattern(ctx, cmd, srcImage, w, h);

            // 封装为 VulkanImageResource（默认 CurrentLayout=TransferSrcOptimal，与上传后布局一致）
            // VulkanImageResource 拥有 srcImage/srcMem，Dispose 负责释放
            using var srcRes = new VulkanImageResource(ctx.Vk, ctx.Device, srcImage, srcMem, w, h, PixelFormat.BGRA32);

            // 设置 Present 目标尺寸/格式（Blit 从 _swapchainExtent / _swapchainFormat 读取）
            SetSwapchainFields(renderer, (uint)w, (uint)h, Format.B8G8R8A8Unorm);

            // 录制并提交 Blit（同尺寸同格式 → CmdCopyImage 零缩放）
            RecordAndBlit(ctx, cmd, renderer, srcRes, dstImage);

            // readback 目标图像，验证像素与源图案一致
            var readback = ReadbackImage(ctx, cmd, dstImage, w, h);
            VerifyAllRed(readback);
        }
        finally
        {
            // dstImage/dstMem 为裸句柄（未包 VulkanImageResource），此处手动释放
            ctx.Vk.DestroyImage(ctx.Device, dstImage, null);
            ctx.Vk.FreeMemory(ctx.Device, dstMem, null);
        }
    }

    private static void UploadPattern(VulkanTestContext ctx, CommandBuffer cmd, Image image, int w, int h)
    {
        int size = w * h * 4;
        var staging = new byte[size];
        for (int i = 0; i < size; i += 4)
        {
            staging[i] = 0;       // B
            staging[i + 1] = 0;   // G
            staging[i + 2] = 255; // R
            staging[i + 3] = 255; // A
        }

        var bufInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)size,
            Usage = BufferUsageFlags.TransferSrcBit,
        };
        ctx.Vk.CreateBuffer(ctx.Device, &bufInfo, null, out var buffer);
        ctx.Vk.GetBufferMemoryRequirements(ctx.Device, buffer, out var memReq);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = VulkanImageResourceTests.FindMemoryType(ctx, memReq.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };
        ctx.Vk.AllocateMemory(ctx.Device, &alloc, null, out var mem);
        ctx.Vk.BindBufferMemory(ctx.Device, buffer, mem, 0);

        void* mapped = null;
        ctx.Vk.MapMemory(ctx.Device, mem, 0, (ulong)size, 0, &mapped);
        Marshal.Copy(staging, 0, (nint)mapped, size);
        ctx.Vk.UnmapMemory(ctx.Device, mem);

        BeginCommand(ctx, cmd);
        Transition(ctx, cmd, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            AccessFlags.None, AccessFlags.TransferWriteBit);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageExtent = new Extent3D((uint)w, (uint)h, 1),
        };
        ctx.Vk.CmdCopyBufferToImage(cmd, buffer, image, ImageLayout.TransferDstOptimal, 1, &region);
        // 转回 TransferSrcOptimal（VulkanImageResource 默认交付布局）
        Transition(ctx, cmd, image, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
            AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        EndSubmitWait(ctx, cmd);

        ctx.Vk.DestroyBuffer(ctx.Device, buffer, null);
        ctx.Vk.FreeMemory(ctx.Device, mem, null);
    }

    private static void RecordAndBlit(VulkanTestContext ctx, CommandBuffer cmd, VulkanRenderer renderer,
        VulkanImageResource srcRes, Image dstImage)
    {
        BeginCommand(ctx, cmd);
        // Blit 的 CmdCopyImage 要求目标为 TransferDstOptimal
        Transition(ctx, cmd, dstImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            AccessFlags.None, AccessFlags.TransferWriteBit);
        renderer.BlitVulkanImageResource(srcRes, dstImage);
        EndSubmitWait(ctx, cmd);
    }

    private static byte[] ReadbackImage(VulkanTestContext ctx, CommandBuffer cmd, Image image, int w, int h)
    {
        int size = w * h * 4;
        var bufInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)size,
            Usage = BufferUsageFlags.TransferDstBit,
        };
        ctx.Vk.CreateBuffer(ctx.Device, &bufInfo, null, out var buffer);
        ctx.Vk.GetBufferMemoryRequirements(ctx.Device, buffer, out var memReq);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = VulkanImageResourceTests.FindMemoryType(ctx, memReq.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };
        ctx.Vk.AllocateMemory(ctx.Device, &alloc, null, out var mem);
        ctx.Vk.BindBufferMemory(ctx.Device, buffer, mem, 0);

        BeginCommand(ctx, cmd);
        // Blit 后目标为 TransferDstOptimal，转 TransferSrcOptimal 以便拷贝到 buffer
        Transition(ctx, cmd, image, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
            AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageExtent = new Extent3D((uint)w, (uint)h, 1),
        };
        ctx.Vk.CmdCopyImageToBuffer(cmd, image, ImageLayout.TransferSrcOptimal, buffer, 1, &region);
        EndSubmitWait(ctx, cmd);

        void* mapped = null;
        ctx.Vk.MapMemory(ctx.Device, mem, 0, (ulong)size, 0, &mapped);
        var result = new byte[size];
        Marshal.Copy((nint)mapped, result, 0, size);
        ctx.Vk.UnmapMemory(ctx.Device, mem);

        ctx.Vk.DestroyBuffer(ctx.Device, buffer, null);
        ctx.Vk.FreeMemory(ctx.Device, mem, null);
        return result;
    }

    private static void VerifyAllRed(byte[] data)
    {
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i].Should().Be(0, "B 通道应为 0");
            data[i + 1].Should().Be(0, "G 通道应为 0");
            data[i + 2].Should().Be(255, "R 通道应为 255（全红图案）");
            data[i + 3].Should().Be(255, "A 通道应为 255");
        }
    }

    // ── 命令辅助 ──

    private static void BeginCommand(VulkanTestContext ctx, CommandBuffer cmd)
    {
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        // 调用前已由 EndSubmitWait 重置命令缓冲
        ctx.Vk.BeginCommandBuffer(cmd, &begin);
    }

    private static void EndSubmitWait(VulkanTestContext ctx, CommandBuffer cmd)
    {
        ctx.Vk.EndCommandBuffer(cmd);
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        ctx.Vk.QueueSubmit(ctx.Queue, 1, &submit, default);
        ctx.Vk.QueueWaitIdle(ctx.Queue);
        ctx.Vk.ResetCommandBuffer(cmd, 0);
    }

    private static void Transition(VulkanTestContext ctx, CommandBuffer cmd, Image image,
        ImageLayout from, ImageLayout to, AccessFlags srcAccess, AccessFlags dstAccess)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
        };
        ctx.Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &barrier);
    }

    private static void SetSwapchainFields(VulkanRenderer renderer, uint w, uint h, Format format)
    {
        typeof(VulkanRenderer).GetField("_swapchainExtent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(renderer, new Extent2D(w, h));
        typeof(VulkanRenderer).GetField("_swapchainFormat",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(renderer, format);
    }
}

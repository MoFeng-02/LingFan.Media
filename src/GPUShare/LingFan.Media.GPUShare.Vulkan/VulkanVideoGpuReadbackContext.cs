using System;
using Silk.NET.Vulkan;

namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// Vulkan 视频解码 DPB 帧 GPU→CPU 回读助手（<c>ReadbackToCpu</c> 真实现）。
/// </summary>
/// <remarks>
/// <para><b>用途</b>：把硬解 NV12 DPB 图像从 GPU 读回为紧凑 BGRA32 CPU 字节，仅作诊断/取证路径，
/// 不影响零拷贝上屏主流。Vulkan 视频 Decode 命令的输出（DPB 图像）原生支持 <c>vkCmdCopyImageToBuffer</c>
/// 转 TransferSrcOptimal 后拷到 host-visible buffer，再 map 出来供 CPU 读。</para>
/// <para><b>布局不变性</b>：内部两条屏障 —— 解码布局 → TransferSrcOptimal（拷贝）→ VideoDecodeDpbKhr（还原）。
/// 不破坏渲染器后续经 <see cref="VulkanNv12ToRgbaConverter.Convert"/> 自 VideoDecodeDpbKhr 起的 transition 链。</para>
/// <para><b>异步策略</b>：<see cref="ReadbackNv12AsBgra32"/> 同步（native 分类）—— 同帧 fence 自等，CPU 阻塞拿结果，与诊断路径的同步调用场景一致。</para>
/// <para><b>AOT</b>：零反射、零 Silk.NET 运行期依赖（走 <see cref="VulkanNative"/> 零反射绑定）；dispose 释放所有 Vk 句柄。</para>
/// </remarks>
public sealed unsafe class VulkanVideoGpuReadbackContext : IDisposable
{
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Queue _readbackQueue;
    private readonly uint _readbackQueueFamilyIndex;

    // 复用命令池 / 命令缓冲 / 栅栏：readback 是单线程串行调用（FPS 极低，诊断场景）。
    private readonly CommandPool _commandPool;
    private readonly CommandBuffer _commandBuffer;
    private readonly Fence _fence;

    // 复用 host-visible staging buffer（最大覆盖 1 帧 NV12，按需扩容）。
    private Buffer _stagingBuffer;
    private DeviceMemory _stagingMemory;
    private void* _stagingMapped;
    private ulong _stagingSize;
    private uint _stagingW;
    private uint _stagingH;

    private bool _disposed;

    public VulkanVideoGpuReadbackContext(Device device, PhysicalDevice physicalDevice, Queue readbackQueue, uint readbackQueueFamilyIndex)
    {
        _device = device;
        _physicalDevice = physicalDevice;
        _readbackQueue = readbackQueue;
        _readbackQueueFamilyIndex = readbackQueueFamilyIndex;

        var poolCi = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = readbackQueueFamilyIndex,
            // VUID-00046/00050：回读命令缓冲每帧 Begin（reset 模式）须此位，否则 vkBeginCommandBuffer 隐式 reset 报错。
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        if (VulkanNative.CreateCommandPool(_device, ref poolCi, null, out _commandPool) != Result.Success)
            throw new InvalidOperationException("readback: 创建命令池失败");

        var alloc = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        fixed (CommandBuffer* pCb = &_commandBuffer)
        {
            if (VulkanNative.AllocateCommandBuffers(_device, ref alloc, pCb) != Result.Success)
                throw new InvalidOperationException("readback: 分配命令缓冲失败");
        }

        var fenceCi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        if (VulkanNative.CreateFence(_device, &fenceCi, null, out _fence) != Result.Success)
            throw new InvalidOperationException("readback: 创建栅栏失败");
    }

    /// <summary>
    /// 把 NV12 多平面 VkImage 同步读回为紧凑 BGRA32 字节数组。
    /// </summary>
    /// <param name="src">NV12 <c>VkImage</c>（硬解 DPB，CONCURRENT 共享）。</param>
    /// <param name="w">图像宽度（像素）。</param>
    /// <param name="h">图像高度（像素）。</param>
    /// <param name="srcLayout">交付时的图像布局（Vulkan 硬解 DPB = <see cref="ImageLayout.VideoDecodeDpbKhr"/>）。</param>
    /// <returns>紧凑 BGRA32，长度 = <c>w * h * 4</c>。</returns>
    public byte[] ReadbackNv12AsBgra32(Image src, uint w, uint h, ImageLayout srcLayout, uint layer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (w == 0 || h == 0) return Array.Empty<byte>();

        EnsureStaging(w, h);

        // ── 录命令 ──
        CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
        if (VulkanNative.BeginCommandBuffer(_commandBuffer, ref beginInfo) != Result.Success)
            throw new InvalidOperationException("readback: BeginCommandBuffer 失败");

        // Barrier A: srcLayout → TransferSrcOptimal
        // CONCURRENT 共享下 SrcQueueFamilyIndex = DstQueueFamilyIndex = ~0u（合法，无所有权转移）。
        // srcAccess=MemoryWrite + srcStage=AllCommands 是 Silk.NET 2.23.0 在 synchronization2 缺失下覆盖 video-decode 阶段/访问位的对称做法
        // （与解码器 EnsureSlotDecodeLayout / 转换器 RenderInto 一致）。
        ImageSubresourceRange subresourceRange = new()
        {
            AspectMask = ImageAspectFlags.Plane0BitKhr | ImageAspectFlags.Plane1BitKhr,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = layer,
            LayerCount = 1,
        };
        ImageMemoryBarrier toTransferBarrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            OldLayout = srcLayout,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = ~0u,
            DstQueueFamilyIndex = ~0u,
            Image = src,
            SubresourceRange = subresourceRange,
        };
        VulkanNative.CmdPipelineBarrier(_commandBuffer,
            PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &toTransferBarrier);

        // Copy plane0 (Y, R8) → offset 0; BufferRowLength = w（每行 stride = w 字节）
        ulong yPlaneSize = (ulong)w * h;
        BufferImageCopy yCopy = new()
        {
            BufferOffset = 0,
            BufferRowLength = w,
            BufferImageHeight = h,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.Plane0BitKhr,
                MipLevel = 0,
                BaseArrayLayer = layer,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(w, h, 1),
        };
        VulkanNative.CmdCopyImageToBuffer(_commandBuffer, src, ImageLayout.TransferSrcOptimal, _stagingBuffer, 1, &yCopy);

        // Copy plane1 (UV, R8G8) → offset yPlaneSize; UV plane 尺寸 W/2 x H/2，buffer 行 stride = W/2（紧密，2 字节/texel → W 字节/行）。
        // ⚠️ 此前误用 BufferRowLength=w（texels）→ 每行写 2w 字节，h/2 行共越界 ~0.5·W·H 字节（staging 仅 1.5·W·H），
        //    GPU 越界写 → 下一 submit（渲染）VK_ERROR_DEVICE_LOST。修正为 w/2 与下方 ui 读取的紧密行距一致。
        BufferImageCopy uvCopy = new()
        {
            BufferOffset = yPlaneSize,
            BufferRowLength = w / 2,
            BufferImageHeight = h / 2,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.Plane1BitKhr,
                MipLevel = 0,
                BaseArrayLayer = layer,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(w / 2, h / 2, 1),
        };
        VulkanNative.CmdCopyImageToBuffer(_commandBuffer, src, ImageLayout.TransferSrcOptimal, _stagingBuffer, 1, &uvCopy);

        // Barrier B: TransferSrcOptimal → VideoDecodeDpbKhr（还原为解码器/渲染器约定的交付布局）
        ImageMemoryBarrier backBarrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferReadBit,
            DstAccessMask = AccessFlags.MemoryWriteBit,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.VideoDecodeDpbKhr,
            SrcQueueFamilyIndex = ~0u,
            DstQueueFamilyIndex = ~0u,
            Image = src,
            SubresourceRange = subresourceRange,
        };
        VulkanNative.CmdPipelineBarrier(_commandBuffer,
            PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &backBarrier);

        if (VulkanNative.EndCommandBuffer(_commandBuffer) != Result.Success)
            throw new InvalidOperationException("readback: EndCommandBuffer 失败");

        // ── 提交 + fence 等 ──
        var cb = _commandBuffer;
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
        };
        fixed (Fence* pFence = &_fence)
        {
            // ⚠️ 第 4 参是 VkFence *句柄值*（64 位不透明句柄），不是 Fence 结构指针。
            // 误传 &_fence（栈地址）作句柄会被驱动解引用 → 0xC0000005。须与解码器一致传 _fence.Handle。
            if (VulkanNative.QueueSubmit(_readbackQueue, 1, &submitInfo, (nint)_fence.Handle) != Result.Success)
                throw new InvalidOperationException("readback: QueueSubmit 失败");
            if (VulkanNative.WaitForFences(_device, 1, pFence, 1, 5_000_000_000UL) != Result.Success)
                throw new InvalidOperationException("readback: WaitForFences 失败");
            if (VulkanNative.ResetFences(_device, 1, pFence) != Result.Success)
                throw new InvalidOperationException("readback: ResetFences 失败");
        }

        // ── map + NV12→BGRA32（CPU 软转；诊断用，几帧够看）──
        int wInt = (int)w;
        int hInt = (int)h;
        byte[] bgra = new byte[wInt * hInt * 4];
        byte* pStaging = (byte*)_stagingMapped;
        for (int y = 0; y < hInt; y++)
        {
            for (int x = 0; x < wInt; x++)
            {
                int yi = y * wInt + x;
                int ui = (int)yPlaneSize + (y / 2) * wInt + (x / 2) * 2;
                int Y = pStaging[yi], U = pStaging[ui], V = pStaging[ui + 1];
                int c = Y - 16, d = U - 128, e = V - 128;
                int o = yi * 4;
                // BT.601 full range → BGRA 字节顺序
                bgra[o]     = (byte)Clamp8((298 * c + 516 * d + 128) >> 8);
                bgra[o + 1] = (byte)Clamp8((298 * c - 100 * d - 208 * e + 128) >> 8);
                bgra[o + 2] = (byte)Clamp8((298 * c + 409 * e + 128) >> 8);
                bgra[o + 3] = 255;
            }
        }
        return bgra;
    }

    private void EnsureStaging(uint w, uint h)
    {
        ulong required = (ulong)w * h * 3 / 2; // NV12 = Y(W*H) + UV(W/2*H/2*2) = 1.5*W*H
        if (required < 4096) required = 4096;
        if (_stagingBuffer.Handle != 0 && _stagingW == w && _stagingH == h && _stagingSize >= required) return;

        ReleaseStaging();
        _stagingSize = required;

        var bufCi = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = _stagingSize,
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };
        if (VulkanNative.CreateBuffer(_device, ref bufCi, null, out _stagingBuffer) != Result.Success)
            throw new InvalidOperationException("readback: 创建 staging 缓冲失败");

        MemoryRequirements memReq;
        VulkanNative.GetBufferMemoryRequirements(_device, _stagingBuffer, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
        };
        if (VulkanNative.AllocateMemory(_device, ref alloc, null, out _stagingMemory) != Result.Success)
            throw new InvalidOperationException("readback: 分配 staging 内存失败");
        if (VulkanNative.BindBufferMemory(_device, _stagingBuffer, _stagingMemory, 0) != Result.Success)
            throw new InvalidOperationException("readback: 绑定 staging 内存失败");

        void* mapped = null;
        if (VulkanNative.MapMemory(_device, _stagingMemory, 0, _stagingSize, 0, &mapped) != Result.Success)
            throw new InvalidOperationException("readback: 映射 staging 内存失败");
        _stagingMapped = mapped;
        _stagingW = w;
        _stagingH = h;
    }

    private void ReleaseStaging()
    {
        if (_device.Handle == 0) return;
        if (_stagingMapped != null) { VulkanNative.UnmapMemory(_device, _stagingMemory); _stagingMapped = null; }
        if (_stagingBuffer.Handle != 0) { VulkanNative.DestroyBuffer(_device, _stagingBuffer, null); _stagingBuffer = default; }
        if (_stagingMemory.Handle != 0) { VulkanNative.FreeMemory(_device, _stagingMemory, null); _stagingMemory = default; }
        _stagingSize = 0; _stagingW = 0; _stagingH = 0;
    }

    private uint FindMemoryType(uint memoryTypeBits, MemoryPropertyFlags required)
    {
        PhysicalDeviceMemoryProperties props;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &props);
        for (uint i = 0; i < props.MemoryTypeCount; i++)
            if ((memoryTypeBits & (1u << (int)i)) != 0 && (props.MemoryTypes[(int)i].PropertyFlags & required) == required)
                return i;
        for (uint i = 0; i < props.MemoryTypeCount; i++)
            if ((memoryTypeBits & (1u << (int)i)) != 0) return i;
        throw new InvalidOperationException("readback: 未找到 host-visible coherent 内存类型。");
    }

    private static int Clamp8(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_device.Handle != 0)
        {
            ReleaseStaging();
            if (_fence.Handle != 0) VulkanNative.DestroyFence(_device, _fence, null);
            if (_commandPool.Handle != 0) VulkanNative.DestroyCommandPool(_device, _commandPool, null);
        }
    }
}

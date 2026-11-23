using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanGpuFrameProducer 的 Readback 职责段（部分类，由 VulkanGpuFrameProducer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/TryImport 分派/Dispose）在主文件；导入/转换/回读各路径零反射原生调用。
public sealed unsafe partial class VulkanGpuFrameProducer
{
    /// <summary>
    /// 把 <see cref="VulkanImageResource"/>（RGBA，TransferSrcOptimal）同步回读为紧凑 BGRA32。
    /// Android 显示桥：Skia 合成路径（<c>SkiaVideoPresenter</c>）经 <see cref="IGpuTextureResource.ReadbackToCpu"/>
    /// 消费 GPU 帧 —— 解码与 YCbCr→RGBA 转换仍在 GPU（零 CPU 像素转换），仅最终一帧一次 GPU→CPU 拷贝。
    /// </summary>
    /// <remarks>
    /// <para>独立命令池/命令缓冲/栅栏/staging buffer（<see cref="EnsureReadbackResources"/> 惰性创建），
    /// 与解码线程的转换命令资源隔离；提交段持 <c>_queueGate</c>（与转换路径共用图形队列，VkQueue 须宿主同步）。
    /// staging buffer 持久映射、grow-only 复用；输出数组经 <see cref="System.Buffers.ArrayPool{T}"/> 租借
    /// （<see cref="GpuTextureReadback"/> 池化构造，Dispose 自动归还，消除 1080p 每帧 ~8MB 的 LOH 分配）。</para>
    /// <para>图像内存屏障（布局保持 TransferSrcOptimal 的 no-op 转移）保证上一提交（转换 draw，已 QueueWaitIdle）
    /// 的写入对本提交的拷贝可见；拷贝后缓冲屏障（TRANSFER_WRITE→HOST/MemoryRead）保证 fence 等待后 host 读有效。</para>
    /// </remarks>
    private unsafe GpuTextureReadback ReadbackRgbaToCpu(VulkanImageResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        int w = resource.Width, h = resource.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"[VK-READBACK] 尺寸无效 {w}x{h}");
        ulong size = (ulong)w * (uint)h * 4;

        lock (_queueGate)
        {
            if (_readbackCommandPool.Handle == 0 && !EnsureReadbackResources())
                throw new InvalidOperationException("[VK-READBACK] 回读命令资源创建失败");
            EnsureReadbackStaging(size);

            VulkanNative.ResetCommandBuffer(_readbackCommandBuffer, CommandBufferResetFlags.None);
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            if (VulkanNative.BeginCommandBuffer(_readbackCommandBuffer, ref beginInfo) != Result.Success)
                throw new InvalidOperationException("[VK-READBACK] BeginCommandBuffer 失败");

            try
            {
                // 图像内存屏障：上一提交的转换写入 → 本次拷贝读（布局 no-op，同为 TransferSrcOptimal）。
                var subresource = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                };
                var imgBarrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.MemoryWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.TransferSrcOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = ~0u,
                    DstQueueFamilyIndex = ~0u,
                    Image = resource.Image,
                    SubresourceRange = subresource,
                };
                VulkanNative.CmdPipelineBarrier(_readbackCommandBuffer,
                    PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &imgBarrier);

                var copy = new BufferImageCopy
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,   // 0 = 紧密打包（stride = w * 4）
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    ImageOffset = new Offset3D(0, 0, 0),
                    ImageExtent = new Extent3D((uint)w, (uint)h, 1),
                };
                VulkanNative.CmdCopyImageToBuffer(_readbackCommandBuffer,
                    resource.Image, ImageLayout.TransferSrcOptimal, _readbackStagingBuffer, 1, &copy);

                // 缓冲内存屏障：TRANSFER_WRITE → HOST 读（HOST_COHERENT，fence 等待后映射数据有效）。
                var bufBarrier = new BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.MemoryReadBit,
                    SrcQueueFamilyIndex = ~0u,
                    DstQueueFamilyIndex = ~0u,
                    Buffer = _readbackStagingBuffer,
                    Offset = 0,
                    Size = size,
                };
                VulkanNative.CmdPipelineBarrier(_readbackCommandBuffer,
                    PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit,
                    0, 0, null, 1, &bufBarrier, 0, null);
            }
            catch
            {
                // 把命令缓冲移出 recording 状态，避免 ResetCommandBuffer 返回 VK_NOT_READY。
                VulkanNative.EndCommandBuffer(_readbackCommandBuffer);
                throw;
            }

            if (VulkanNative.EndCommandBuffer(_readbackCommandBuffer) != Result.Success)
                throw new InvalidOperationException("[VK-READBACK] EndCommandBuffer 失败");

            var cb = _readbackCommandBuffer;
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cb,
            };
            fixed (Fence* pFence = &_readbackFence)
            {
                // 第 4 参是 VkFence 句柄值（64 位不透明句柄），非结构指针（与 VulkanVideoGpuReadbackContext 同手法）。
                if (VulkanNative.QueueSubmit(_graphicsQueue, 1, &submitInfo, (nint)_readbackFence.Handle) != Result.Success)
                    throw new InvalidOperationException("[VK-READBACK] QueueSubmit 失败");
                if (VulkanNative.WaitForFences(_device, 1, pFence, 1, 5_000_000_000UL) != Result.Success)
                    throw new InvalidOperationException("[VK-READBACK] WaitForFences 失败");
                if (VulkanNative.ResetFences(_device, 1, pFence) != Result.Success)
                    throw new InvalidOperationException("[VK-READBACK] ResetFences 失败");
            }

            // staging 持久映射 → 池化托管数组（GpuTextureReadback 池化构造，Dispose 归还池）。
            byte[] data = System.Buffers.ArrayPool<byte>.Shared.Rent((int)size);
            new Span<byte>(_readbackStagingMapped, (int)size).CopyTo(data.AsSpan(0, (int)size));
            return new GpuTextureReadback(w, h, PixelFormat.BGRA32, data, w * 4, (int)size);
        }
    }

    /// <summary>惰性创建回读命令资源：独立命令池 + 命令缓冲 + 栅栏（复用 <see cref="EnsureConvertResources"/> 的图形队列）。</summary>
    private unsafe bool EnsureReadbackResources()
    {
        if (!EnsureConvertResources()) return false; // 图形队列 + 转换命令资源（含 _graphicsQueue/_graphicsQueueFamily）

        var poolCi = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _graphicsQueueFamily,
            // 回读命令缓冲每次 Begin 前 Reset，须此位（VUID-00046/00050）。
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        if (VulkanNative.CreateCommandPool(_device, ref poolCi, null, out _readbackCommandPool) != Result.Success)
            return false;

        var alloc = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _readbackCommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer cb;
        if (VulkanNative.AllocateCommandBuffers(_device, ref alloc, &cb) != Result.Success)
        {
            VulkanNative.DestroyCommandPool(_device, _readbackCommandPool, null);
            _readbackCommandPool = default;
            return false;
        }
        _readbackCommandBuffer = cb;

        var fenceCi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        return VulkanNative.CreateFence(_device, &fenceCi, null, out _readbackFence) == Result.Success;
    }

    /// <summary>确保回读 staging buffer 容纳 <paramref name="size"/> 字节（HOST_VISIBLE|HOST_COHERENT，持久映射，grow-only）。</summary>
    private unsafe void EnsureReadbackStaging(ulong size)
    {
        if (_readbackStagingBuffer.Handle != 0 && _readbackStagingSize >= size) return;

        if (_readbackStagingBuffer.Handle != 0)
        {
            VulkanNative.UnmapMemory(_device, _readbackStagingMemory);
            VulkanNative.DestroyBuffer(_device, _readbackStagingBuffer, null);
            VulkanNative.FreeMemory(_device, _readbackStagingMemory, null);
            _readbackStagingBuffer = default;
            _readbackStagingMemory = default;
            _readbackStagingMapped = null;
            _readbackStagingSize = 0;
        }
        _readbackStagingSize = Math.Max(size, 4096);

        var bufCi = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = _readbackStagingSize,
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };
        if (VulkanNative.CreateBuffer(_device, ref bufCi, null, out _readbackStagingBuffer) != Result.Success)
            throw new InvalidOperationException("[VK-READBACK] 创建 staging 缓冲失败");

        MemoryRequirements memReq;
        VulkanNative.GetBufferMemoryRequirements(_device, _readbackStagingBuffer, &memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindReadbackMemoryType(memReq.MemoryTypeBits),
        };
        if (VulkanNative.AllocateMemory(_device, ref allocInfo, null, out _readbackStagingMemory) != Result.Success)
            throw new InvalidOperationException("[VK-READBACK] 分配 staging 内存失败");
        if (VulkanNative.BindBufferMemory(_device, _readbackStagingBuffer, _readbackStagingMemory, 0) != Result.Success)
            throw new InvalidOperationException("[VK-READBACK] 绑定 staging 内存失败");

        void* mapped = null;
        if (VulkanNative.MapMemory(_device, _readbackStagingMemory, 0, _readbackStagingSize, 0, &mapped) != Result.Success)
            throw new InvalidOperationException("[VK-READBACK] 映射 staging 内存失败");
        _readbackStagingMapped = mapped;
    }

    /// <summary>回读 staging 内存类型：优先 HOST_VISIBLE|HOST_COHERENT，兜底任意满足 memoryTypeBits 的类型。</summary>
    private unsafe uint FindReadbackMemoryType(uint memoryTypeBits)
    {
        PhysicalDeviceMemoryProperties props;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &props);
        const MemoryPropertyFlags required =
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        for (uint i = 0; i < props.MemoryTypeCount; i++)
            if ((memoryTypeBits & (1u << (int)i)) != 0 &&
                (props.MemoryTypes[(int)i].PropertyFlags & required) == required)
                return i;
        for (uint i = 0; i < props.MemoryTypeCount; i++)
            if ((memoryTypeBits & (1u << (int)i)) != 0)
                return i;
        throw new InvalidOperationException("[VK-READBACK] 未找到 host-visible coherent 内存类型。");
    }

    /// <summary>关闭 DXGI 共享 NT 句柄（导入完成/失败后由生产者负责关闭，防内核句柄泄漏）。</summary>
    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);
}

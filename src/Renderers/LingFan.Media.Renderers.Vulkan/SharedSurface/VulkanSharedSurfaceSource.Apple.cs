using LingFan.Media.GPUShare.Vulkan;
using LingFan.Media.Renderers.Shared;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan 共享表面源（no-airspace 纯无空域生产者）：把软帧渲染进一块<b>可外部导出</b>的
/// Vulkan 离屏 <see cref="Image"/>，经外部内存句柄交给宿主合成器直接导入采样，从而实现
/// 「无空域、纯控件级」的 GPU 上屏——<b>Vulkan 渲染 Vulkan 自己的</b>，不跨界喂 D3D11 组合器。
/// </summary>
/// <remarks>
/// <para><b>这是渲染器层唯一碰 Vulkan 具体 API 的地方</b>。其余层（Avalonia <c>CompositionVideoRenderer</c>）
/// 只看到 <see cref="SharedGpuSurfaceDescriptor"/>（外部内存句柄 + 信号量对），<b>不引用任何 GPU 库</b>，
/// 从而达成「不绑定具体 GPU、低耦合」的架构诉求，严守「各 Renderer 管好自身（无头/有头/无空域）」架构原则。</para>
/// <para><b>零拷贝路径</b>：软帧（NV12/NV21/YUV420P/YUV422P/YUV444P/BGRA/RGBA）的 Y/U/V 平面上传到 GPU 纹理后
/// 由 <see cref="VulkanShaderPipeline"/> 完成 YUV→RGB + 缩放，写入可导出离屏图像；该图像由宿主合成器
/// 经 <c>VkImportMemoryWin32HandleInfoKHR</c> / <c>VkImportMemoryFdInfoKHR</c> 直接导入采样，无 CPU 回读、无独占 HWND。</para>
/// <para><b>同步模型（Semaphores，Vulkan 原生机制）</b>：</para>
/// <list type="bullet">
/// <item>生产者（本源）写完后 signal <c>ConsumerWait</c> 信号量（消费方据此等待「内容就绪」）；</item>
/// <item>消费方采样完成后 signal <c>ConsumerSignal</c> 信号量（生产者据此等待「表面已归还，可覆写」）；</item>
/// <item>生产者每帧以<b>有限超时（16ms，与 D3D11 keyed mutex 超时对称）</b>等待 <c>ConsumerSignal</c> 后再写，
/// 超时即丢帧——绝不无限阻塞管线线程。两信号量均经外部句柄导出，消费方导入一次长期使用。</item>
/// </list>
/// <para><b>握手初始化</b>：二进制信号量默认未信号。生产者首帧须等待 <c>ConsumerSignal</c>，故创建时以一次
/// signal-only 提交将其初始化为信号态，避免首帧永久阻塞。</para>
/// <para><b>异步策略</b>：<see cref="TryWriteFrame"/> 同步（native 分类）——GPU 命令提交无真实 I/O await，
/// 补 async 即伪异步；且用<b>有限超时</b> Fence 等待，超时即丢帧，绝不阻塞管线线程。</para>
/// <para><b>线程</b>：由管线线程调用；共享 Vulkan 设备已开启多线程保护（见 <see cref="VulkanRendererFactory"/>），
/// 信号量握手负责与 Avalonia 合成线程的跨线程同步。</para>
/// <para><b>AOT 兼容</b>：sealed 类，裸 vtable 互操作，无反射。</para>
/// </remarks>
internal sealed unsafe partial class VulkanSharedSurfaceSource : ISharedGpuSurfaceSource
{
    // 信号量握手键（Semaphores 模型不使用 keyed mutex，恒为 0）
    // Apple / MoltenVK 平台实现（MTLSharedEvent 信号量导出、IOSurface 离屏导出）


    // Apple / MoltenVK：经 VK_EXT_metal_objects 把 Vulkan 信号量导出为 MTLSharedEvent
    private void CreateSemaphoresApple()
    {
        // 创建信号量时 pNext 链 ExportMetalObjectCreateInfoEXT（SharedEvent 位），
        // 告知 MoltenVK 该信号量应作为 Metal 共享事件创建。
        ExportMetalObjectCreateInfoEXT exportInfo = new()
        {
            SType = StructureType.ExportMetalObjectCreateInfoExt,
            ExportObjectType = ExportMetalObjectTypeFlagsEXT.SharedEventBitExt,
        };
        SemaphoreCreateInfo semInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = (void*)&exportInfo,
        };
        Result r1 = VulkanNative.CreateSemaphore(_device, ref semInfo, null, out _consumerWaitSem);
        Result r2 = VulkanNative.CreateSemaphore(_device, ref semInfo, null, out _consumerSignalSem);
        if (r1 != Result.Success || r2 != Result.Success)
            throw new InvalidOperationException($"vkCreateSemaphore（Apple 共享表面信号量）失败: {r1}/{r2}");

        _consumerWaitHandle = ExportMtlSharedEvent(_consumerWaitSem);
        _consumerSignalHandle = ExportMtlSharedEvent(_consumerSignalSem);
        if (_consumerWaitHandle == IntPtr.Zero || _consumerSignalHandle == IntPtr.Zero)
            throw new InvalidOperationException("vkExportMetalObjectsEXT 未能导出 MTLSharedEvent（VK_EXT_metal_objects 可能未启用）。");

        // 握手初始化：以一次 signal-only 提交把 ConsumerSignal 置为信号态，否则生产者首帧永久阻塞。
        Semaphore bootstrapSem = _consumerSignalSem;
        SubmitInfo bootstrap = new()
        {
            SType = StructureType.SubmitInfo,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &bootstrapSem,
        };
        Result bootR = VulkanNative.QueueSubmit(_queue, 1, &bootstrap, default);
        if (bootR != Result.Success)
            throw new InvalidOperationException($"vkQueueSubmit（Apple 信号量握手初始化）失败: {bootR}");
        VulkanNative.QueueWaitIdle(_queue);
    }

    private nint ExportMtlSharedEvent(Semaphore sem)
    {
        // 每次调用单独导出：pNext 链中同一结构体类型不应出现两次（规避 Vulkan valid usage），
        // 故两个信号量各走一次 vkExportMetalObjectsEXT。
        ExportMetalSharedEventInfoEXT evt = new()
        {
            SType = StructureType.ExportMetalSharedEventInfoExt,
            Semaphore = sem,
            Event = default,
        };
        ExportMetalObjectsInfoEXT metalsInfo = new()
        {
            SType = StructureType.ExportMetalObjectsInfoExt,
            PNext = (void*)&evt,
        };
        VulkanNative.ExportMetalObjectsEXT(_device, &metalsInfo);
        return evt.MtlSharedEvent;
    }

        // Apple / MoltenVK：经 VK_EXT_metal_objects 把 Vulkan 离屏图像导出为 IOSurface
    private void EnsureSharedSurfaceApple(int w, int h)
    {
        // 图像创建：pNext 链 ExportMetalObjectCreateInfoEXT（IOSurface 位），告知 MoltenVK
        // 此图像可经 vkExportMetalObjectsEXT 导出为 IOSurface。当前 VK_EXT_metal_objects
        // 不再要求 VK_IMAGE_CREATE_METAL_COMPATIBLE_BIT_EXT 创建标志（已从 VkImageCreateFlagBits 移除）。
        ExportMetalObjectCreateInfoEXT metalImageInfo = new()
        {
            SType = StructureType.ExportMetalObjectCreateInfoExt,
            ExportObjectType = ExportMetalObjectTypeFlagsEXT.IosurfaceBitExt,
        };
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.B8G8R8A8Unorm,
            Extent = new Extent3D((uint)w, (uint)h, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            PNext = (void*)&metalImageInfo,
        };
        Result result = VulkanNative.CreateImage(_device, &imageInfo, null, out _sharedImages[0]);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage（Apple 共享表面离屏）失败: {result}");

        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, _sharedImages[0], &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);

        // MoltenVK 自行管理 IOSurface 底层内存，普通设备本地分配即可（无需 ExportMemoryAllocateInfo）。
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
        };
        result = VulkanNative.AllocateMemory(_device, &allocInfo, null, out _sharedMemories[0]);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory（Apple 共享表面离屏）失败: {result}");
        result = VulkanNative.BindImageMemory(_device, _sharedImages[0], _sharedMemories[0], 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindImageMemory（Apple 共享表面离屏）失败: {result}");
        // 与 OPAQUE_FD 路径同理：记录真实分配字节数（IOSurface 路径合成器侧同样做严格相等校验）。
        _sharedMemorySizes[0] = memReq.Size;

        // 导出 IOSurface（持久，随图像生命周期；消费方 Avalonia 经 IOSurfaceRef 直接导入采样）。
        _exportedMemoryHandle = ExportIOSurface(_sharedImages[0]);
        if (_exportedMemoryHandle == IntPtr.Zero)
            throw new InvalidOperationException("vkExportMetalObjectsEXT 未能导出 IOSurface（VK_EXT_metal_objects 可能未启用）。");

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _sharedImages[0],
            ViewType = ImageViewType.Type2D,
            Format = Format.B8G8R8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        result = VulkanNative.CreateImageView(_device, &viewInfo, null, out _sharedImageViews[0]);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView（Apple 共享表面离屏）失败: {result}");

        _texW = w;
        _texH = h;
        _version++;
    }

    private nint ExportIOSurface(Image image)
    {
        ExportMetalIOSurfaceInfoEXT iosurf = new()
        {
            SType = StructureType.ExportMetalIOSurfaceInfoExt,
            Image = image,
        };
        ExportMetalObjectsInfoEXT metalsInfo = new()
        {
            SType = StructureType.ExportMetalObjectsInfoExt,
            PNext = (void*)&iosurf,
        };
        VulkanNative.ExportMetalObjectsEXT(_device, &metalsInfo);
        return iosurf.IoSurface;
    }

}
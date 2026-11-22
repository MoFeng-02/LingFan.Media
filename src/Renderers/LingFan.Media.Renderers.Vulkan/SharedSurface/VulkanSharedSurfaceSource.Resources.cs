using LingFan.Media.GPUShare.Vulkan;
using LingFan.Media.Renderers.Shared;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanSharedSurfaceSource 的 Resources 职责段（部分类，由 VulkanSharedSurfaceSource.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/契约属性/Dispose）在主文件；平台专属路径见 Android/Apple partial。
internal sealed unsafe partial class VulkanSharedSurfaceSource
{
    private void CreateCommandResources()
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _queueFamilyIndex,
        };
        Result result = VulkanNative.CreateCommandPool(_device, ref poolInfo, null, out _commandPool);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateCommandPool（共享表面源）失败: {result}");

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        var cmds = stackalloc CommandBuffer[1];
        result = VulkanNative.AllocateCommandBuffers(_device, &allocInfo, cmds);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateCommandBuffers（共享表面源）失败: {result}");
        _commandBuffer = cmds[0];

        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        result = VulkanNative.CreateFence(_device, &fenceInfo, null, out _frameFence);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateFence（共享表面源）失败: {result}");
    }

    private void CreateSemaphores()
    {
        if (_isApple)
        {
            CreateSemaphoresApple();
            return;
        }

        ExportSemaphoreCreateInfo extSemInfo = new()
        {
            SType = StructureType.ExportSemaphoreCreateInfo,
            HandleTypes = _semHandleType,
        };
        SemaphoreCreateInfo semInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = (void*)&extSemInfo,
        };

        Result r1 = VulkanNative.CreateSemaphore(_device, ref semInfo, null, out _consumerWaitSem);
        Result r2 = VulkanNative.CreateSemaphore(_device, ref semInfo, null, out _consumerSignalSem);
        if (r1 != Result.Success || r2 != Result.Success)
            throw new InvalidOperationException($"vkCreateSemaphore（共享表面信号量）失败: {r1}/{r2}");

        // 导出两个信号量的外部句柄，供消费方导入。
        if (_isWindows)
        {
            SemaphoreGetWin32HandleInfoKHR getWait = new()
            {
                SType = StructureType.SemaphoreGetWin32HandleInfoKhr,
                Semaphore = _consumerWaitSem,
                HandleType = _semHandleType,
            };
            SemaphoreGetWin32HandleInfoKHR getSignal = new()
            {
                SType = StructureType.SemaphoreGetWin32HandleInfoKhr,
                Semaphore = _consumerSignalSem,
                HandleType = _semHandleType,
            };
            Result h1 = VulkanNative.GetSemaphoreWin32HandleKHR(_device, &getWait, out nint hWait);
            Result h2 = VulkanNative.GetSemaphoreWin32HandleKHR(_device, &getSignal, out nint hSignal);
            if (h1 != Result.Success || h2 != Result.Success)
                throw new InvalidOperationException($"vkGetSemaphoreWin32HandleKHR 失败: {h1}/{h2}");
            _consumerWaitHandle = hWait;
            _consumerSignalHandle = hSignal;
        }
        else
        {
            SemaphoreGetFdInfoKHR getWait = new()
            {
                SType = StructureType.SemaphoreGetFDInfoKhr,
                Semaphore = _consumerWaitSem,
                HandleType = _semHandleType,
            };
            SemaphoreGetFdInfoKHR getSignal = new()
            {
                SType = StructureType.SemaphoreGetFDInfoKhr,
                Semaphore = _consumerSignalSem,
                HandleType = _semHandleType,
            };
            Result h1 = VulkanNative.GetSemaphoreFdKHR(_device, &getWait, out int fdWait);
            Result h2 = VulkanNative.GetSemaphoreFdKHR(_device, &getSignal, out int fdSignal);
            if (h1 != Result.Success || h2 != Result.Success)
                throw new InvalidOperationException($"vkGetSemaphoreFdKHR 失败: {h1}/{h2}");
            _consumerWaitHandle = (nint)fdWait;
            _consumerSignalHandle = (nint)fdSignal;
        }

        // 握手初始化：以一次 signal-only 提交把 ConsumerSignal 置为信号态，
        // 否则生产者首帧等待 ConsumerSignal 将永久阻塞。
        Semaphore bootstrapSem = _consumerSignalSem;
        SubmitInfo bootstrap = new()
        {
            SType = StructureType.SubmitInfo,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &bootstrapSem,
        };
        Result bootR = VulkanNative.QueueSubmit(_queue, 1, &bootstrap, default);
        if (bootR != Result.Success)
            throw new InvalidOperationException($"vkQueueSubmit（信号量握手初始化）失败: {bootR}");
        VulkanNative.QueueWaitIdle(_queue);
    }

    /// <summary>
    /// 确保可外部导出离屏图像就绪，尺寸变化时重建底层图像并重新导出句柄（_version 递增）。
    /// </summary>
    private void EnsureSharedSurface(int w, int h)
    {
        if (_sharedImages[0].Handle != 0 && _texW == w && _texH == h)
            return;

        // 拆除旧图像（保留 _version 语义：重建才 +1）
        ReleaseSharedSurface();

        if (_isApple)
        {
            EnsureSharedSurfaceApple(w, h);
            return;
        }

        // 图像创建：启用外部内存导出（pNext=ExternalMemoryImageCreateInfo）。
        // Android（VulkanNativeImage 同 device 直采样）为<b>平面图像</b>：无外部导出、无 dedicated、
        // 无 fd——彻底绕开 Adreno external-memory 分配的厂商坑；Usage 含 Sampled（Skia 采样必需）。
        // Android 建 2 槽（双缓冲轮换交付），其余平台 1 槽。
        for (int slot = 0; slot < _slotCount; slot++)
        {
        ExternalMemoryImageCreateInfo extImageInfo = new()
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = _memHandleType,
        };
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = _surfaceVkFormat,
            Extent = new Extent3D((uint)w, (uint)h, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            // _sharedImage 现为普通 Vulkan 外部图像（Android 同 Linux）：生产者把转换后的 RGBA 经
            // vkCmdCopyImage 拷入（TRANSFER_DST_BIT），合成器经 OPAQUE_FD 导入后作为采样纹理上屏。
            // Usage 与 Flags 必须与宿主合成器（Avalonia VulkanImageBase）逐位一致：
            // vkGetImageMemoryRequirements 是 image 创建参数的函数，Usage/Flags 不同会让
            // 驱动（部分 Adreno 型号实测）给出不同 size，而 Avalonia 导入时按其自身 requirements
            // 对 MemorySize 做严格相等校验，不符即抛"Invalid memory size"→ 每帧导入失败。
            // 对齐值取自 Avalonia 12.1.1 反汇编：UsageFlags=0x17(TransferSrc|TransferDst|Sampled|
            // ColorAttachment)、Flags=MUTABLE_FORMAT。
            Usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit
                    | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit,
            Flags = ImageCreateFlags.CreateMutableFormatBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            PNext = _handleKind == SharedGpuHandleKind.VulkanNativeImage ? (void*)null : (void*)&extImageInfo,
        };
        _sharedUsage = imageInfo.Usage;
        _sharedFlags = imageInfo.Flags;

        Result result = VulkanNative.CreateImage(_device, &imageInfo, null, out Image img);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage（共享表面离屏）失败: {result}");

        // 内存分配：ExportMemoryAllocateInfo(OpaqueWin32/OpaqueFd) 导出 + dedicated 分配。
        // dedicated **必须挂**：不挂时普通 Vulkan 图像 + OPAQUE_FD 导出内存连 vkBindImageMemory 都过不去，
        // 报 vkBindImageMemory 失败: ErrorInvalidExternalHandle（Attach 自检阶段即整链回退 Skia）。
        // 宿主合成器（Avalonia ImportedImage.CreateMemory）也以 dedicated 语义导入，规范上两侧必须匹配。
        // Android（VulkanNativeImage）：平面内存即可，无导出/dedicated 约束。
        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, img, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);

        MemoryDedicatedAllocateInfo dedicated = new MemoryDedicatedAllocateInfo
        {
            SType = StructureType.MemoryDedicatedAllocateInfo,
            Image = img,
        };

        ExternalMemoryHandleTypeFlags memHandle = _memHandleType;
        ExportMemoryAllocateInfo extMemInfo = new()
        {
            SType = StructureType.ExportMemoryAllocateInfo,
            HandleTypes = memHandle,
            PNext = &dedicated,
        };
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
            PNext = _handleKind == SharedGpuHandleKind.VulkanNativeImage ? (void*)null : (void*)&extMemInfo,
        };
        result = VulkanNative.AllocateMemory(_device, &allocInfo, null, out DeviceMemory mem);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory（共享表面离屏）失败: {result}");
        result = VulkanNative.BindImageMemory(_device, img, mem, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindImageMemory（共享表面离屏）失败: {result}");
        // 记录本次分配的真实字节数（= vkGetImageMemoryRequirements().size），随描述符交给合成器。
        // 【为什么必须填】Avalonia 的 VulkanExternalObjectsFeature.ImportedImage.CreateMemory 会拿
        // properties.MemorySize 与它自己 vkGetImageMemoryRequirements(导入图像).size 做**严格相等**校验，
        // 不等即抛 "Invalid memory size"（留 0 → 每帧导入失败 → 不出画）。OPAQUE_FD 不携带
        // 内存元数据，此值只能由生产者如实上报。注意：不是 w*h*4 —— 驱动会按 tile/对齐扩到更大。
        _sharedMemorySizes[slot] = memReq.Size;

        // 导出内存句柄（交合成器）：
        //  - Windows：HANDLE（OpaqueWin32）。
        //  - Android/Linux：vkGetMemoryFdKHR 导出 opaque fd（dma_buf）——两条路径完全同代码。
        //    （防回归：不要改回 AHB 承载（vkGetMemoryAndroidHardwareBufferANDROID 取回 AHardwareBuffer
        //    再抽 dma_buf fd）——AHB 兼容约束的 requirements 与宿主按普通 OPAQUE_FD 建图算出的
        //    不一致，严格相等校验必失败，导入全部被拒、不出画。）
        if (_isWindows)
        {
            MemoryGetWin32HandleInfoKHR getInfo = new()
            {
                SType = StructureType.MemoryGetWin32HandleInfoKhr,
                Memory = mem,
                HandleType = memHandle,
            };
            Result hR = VulkanNative.GetMemoryWin32HandleKHR(_device, &getInfo, out nint hMem);
            if (hR != Result.Success)
                throw new InvalidOperationException($"vkGetMemoryWin32HandleKHR 失败: {hR}");
            _exportedMemoryHandle = hMem;
        }
        else if (_handleKind == SharedGpuHandleKind.VulkanNativeImage)
        {
            // VulkanNativeImage（同 device 直采样）：无需导出任何外部句柄——
            // 消费方（UI 层 Skia）直接按描述符的 Native* 字段包装本图像采样绘制。
            _exportedMemoryHandle = IntPtr.Zero;
            if (slot == 0)
                _logger.LogInformation(
                    "[VULKAN-SHARED] VulkanNativeImage：平面图像+平面内存（无 fd/无 dedicated），双缓冲轮换直接采样。");
        }
        else
        {
            // Linux / Android fd 形态：经 vkGetMemoryFdKHR 导出 opaque fd（dma_buf）——两条路径完全同代码。
            MemoryGetFdInfoKHR getInfo = new()
            {
                SType = StructureType.MemoryGetFDInfoKhr,
                Memory = mem,
                HandleType = memHandle,
            };
            Result hR = VulkanNative.GetMemoryFdKHR(_device, &getInfo, out int fd);
            if (hR != Result.Success)
                throw new InvalidOperationException($"vkGetMemoryFdKHR 失败: {hR}");
            _exportedMemoryHandle = (nint)fd;
        }

        // ImageView（由本源持有并随图像释放；传给管线作离屏渲染目标）。
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = img,
            ViewType = ImageViewType.Type2D,
            Format = _surfaceVkFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        result = VulkanNative.CreateImageView(_device, &viewInfo, null, out ImageView view);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView（共享表面离屏）失败: {result}");

        _sharedImages[slot] = img;
        _sharedMemories[slot] = mem;
        _sharedImageViews[slot] = view;
        } // end slot loop

        // Android：额外创建 plain 内部 RGBA 转换目标（color attachment + transfer src），
        // 所有转换/上传先渲进它，再 vkCmdCopyImage 拷进 AHB 离屏（规避 AHB 作 color attachment
        // 在 Mali 上触发的 GROUP_ERROR_FATAL / DEVICE_LOST）。仅 Android 启用。
        if (_isAndroid)
        {
            ImageCreateInfo convertInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = _surfaceVkFormat,
                Extent = new Extent3D((uint)w, (uint)h, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            if (VulkanNative.CreateImage(_device, &convertInfo, null, out _convertImage) != Result.Success)
                throw new InvalidOperationException("vkCreateImage（Android 内部转换目标）失败。");
            MemoryRequirements convertMemReq;
            VulkanNative.GetImageMemoryRequirements(_device, _convertImage, &convertMemReq);
            uint convertMemType = FindMemoryType(convertMemReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
            var convertAlloc = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = convertMemReq.Size,
                MemoryTypeIndex = convertMemType,
            };
            if (VulkanNative.AllocateMemory(_device, &convertAlloc, null, out _convertMemory) != Result.Success)
                throw new InvalidOperationException("vkAllocateMemory（Android 内部转换目标）失败。");
            if (VulkanNative.BindImageMemory(_device, _convertImage, _convertMemory, 0) != Result.Success)
                throw new InvalidOperationException("vkBindImageMemory（Android 内部转换目标）失败。");
            ImageViewCreateInfo convertViewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _convertImage,
                ViewType = ImageViewType.Type2D,
                Format = _surfaceVkFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            if (VulkanNative.CreateImageView(_device, &convertViewInfo, null, out _convertView) != Result.Success)
                throw new InvalidOperationException("vkCreateImageView（Android 内部转换目标）失败。");
        }

        _texW = w;
        _texH = h;
        _version++;
    }

}

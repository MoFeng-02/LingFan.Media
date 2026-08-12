using LingFan.Media.Renderers.Shared;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan 共享表面源（no-airspace 纯无空域生产者）：把软帧渲染进一块<b>可外部导出</b>的
/// Vulkan 离屏 <see cref="Image"/>，经外部内存句柄交给宿主合成器直接导入采样，从而实现
/// 「无空域、纯控件级」的 GPU 上屏——<b>Vulkan 渲染 Vulkan 自己的</b>，不跨界喂 D3D11 组合器。
/// </summary>
/// <remarks>
/// <para><b>这是渲染器层唯一碰 Vulkan 具体 API 的地方</b>。其余层（Avalonia <c>CompositionVideoRenderer</c>）
/// 只看到 <see cref="SharedGpuSurfaceDescriptor"/>（外部内存句柄 + 信号量对），<b>不引用任何 GPU 库</b>，
/// 从而达成「不绑定具体 GPU、低耦合」的架构诉求，严守「各 Renderer 管好自身（无头/有头/无空域）」宪法。</para>
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
internal sealed unsafe class VulkanSharedSurfaceSource : ISharedGpuSurfaceSource
{
    // ── 信号量握手键（Semaphores 模型不使用 keyed mutex，恒为 0）──
    private const ulong UnusedKey = 0;
    // 生产者等待消费方归还表面的有限超时（纳秒，与 D3D11 的 16ms AcquireSync 超时对称）。
    private const ulong WriteWaitTimeoutNs = 16_000_000;

    private readonly VulkanRendererFactory _factory;
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Queue _queue;
    private readonly uint _queueFamilyIndex;
    private readonly ILogger<VulkanSharedSurfaceSource> _logger;
    private readonly bool _isWindows;
    private readonly SharedGpuHandleKind _handleKind;
    private readonly SharedGpuSemaphoreKind _semaphoreKind;
    private readonly ExternalMemoryHandleTypeFlags _memHandleType;
    private readonly ExternalSemaphoreHandleTypeFlags _semHandleType;

    // 离屏渲染管线（与 SwapChain 路径共用着色器/描述符/管线布局，仅 RenderPass/Pipeline/Framebuffer 独立）。
    private VulkanShaderPipeline? _pipeline;

    // 命令提交（每帧复用）
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private Fence _frameFence;

    // 可外部导出离屏图像（尺寸变化时重建；_version 随之递增）。
    private Image _sharedImage;
    private DeviceMemory _sharedMemory;
    private ImageView _sharedImageView;
    private int _texW, _texH;
    private nint _exportedMemoryHandle;   // 导出的外部内存句柄：Windows=HANDLE，Linux/Android=fd（int 经 nint 传递）
    private ulong _version;

    // 信号量对（长期对象，随源创建/释放；消费方导入一次长期使用）。
    private Semaphore _consumerWaitSem;
    private Semaphore _consumerSignalSem;
    private nint _consumerWaitHandle;
    private nint _consumerSignalHandle;

    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="VulkanSharedSurfaceSource"/> 的新实例。
    /// </summary>
    /// <param name="factory">Vulkan 渲染器工厂（持有共享 Vulkan 设备与设备身份）。</param>
    /// <param name="logger">日志。</param>
    internal VulkanSharedSurfaceSource(VulkanRendererFactory factory, ILogger<VulkanSharedSurfaceSource> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _device = factory.SharedDevice;
        _physicalDevice = factory.SharedPhysicalDevice;
        _queue = factory.SharedQueue;
        _queueFamilyIndex = factory.SharedQueueFamilyIndex;
        _isWindows = OperatingSystem.IsWindows();

        if (_isWindows)
        {
            _handleKind = SharedGpuHandleKind.VulkanOpaqueNtHandle;
            _semaphoreKind = SharedGpuSemaphoreKind.VulkanOpaqueNtHandle;
            _memHandleType = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit;
            _semHandleType = ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit;
        }
        else
        {
            _handleKind = SharedGpuHandleKind.VulkanOpaquePosixFileDescriptor;
            _semaphoreKind = SharedGpuSemaphoreKind.VulkanOpaquePosixFileDescriptor;
            _memHandleType = ExternalMemoryHandleTypeFlags.OpaqueFDBit;
            _semHandleType = ExternalSemaphoreHandleTypeFlags.OpaqueFDBit;
        }

        CreateCommandResources();
        CreateSemaphores();
    }

    /// <inheritdoc/>
    public SharedGpuHandleKind HandleKind => _handleKind;

    /// <inheritdoc/>
    public SharedGpuSyncMode SyncMode => SharedGpuSyncMode.Semaphores;

    /// <inheritdoc/>
    public ulong ConsumerAcquireKey => UnusedKey;

    /// <inheritdoc/>
    public ulong ConsumerReleaseKey => UnusedKey;

    /// <inheritdoc/>
    public SharedGpuSemaphorePair? Semaphores =>
        _consumerWaitHandle != IntPtr.Zero && _consumerSignalHandle != IntPtr.Zero
            ? new SharedGpuSemaphorePair(_consumerWaitHandle, _consumerSignalHandle, _semaphoreKind)
            : null;

    /// <inheritdoc/>
    public bool TryWriteFrame(VideoFrame frame, out SharedGpuSurfaceDescriptor descriptor)
    {
        descriptor = default;
        if (_disposed)
            return false;
        if (frame.Resource is not SoftwareFrameResource sw)
            return false; // 仅支持软帧无空域路径；GPU 纹理零拷贝交由后续扩展，不支持即交回回退

        int w = sw.Width, h = sw.Height;
        EnsureSharedSurface(w, h);

        // 绑定离屏 RenderPass/Pipeline/Framebuffer 到可导出图像视图（尺寸/格式变化才重建）。
        _pipeline!.EnsureOffscreenResources(Format.B8G8R8A8Unorm, new Extent2D((uint)w, (uint)h), _sharedImageView);

        // 记录并提交通知命令缓冲：等待消费方归还信号量 → 写帧 → signal 消费方等待信号量。
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Result result = VulkanNative.BeginCommandBuffer(_commandBuffer, ref beginInfo);
        if (result != Result.Success)
        {
            _logger.LogWarning("Vulkan 共享表面 BeginCommandBuffer 失败：{Result}", result);
            return false;
        }

        _pipeline.PresentOffscreen(sw, _commandBuffer, (0, 0, w, h), (0f, 0f, 1f, 1f));

        result = VulkanNative.EndCommandBuffer(_commandBuffer);
        if (result != Result.Success)
        {
            _logger.LogWarning("Vulkan 共享表面 EndCommandBuffer 失败：{Result}", result);
            return false;
        }

        // 生产者等待消费方归还（ConsumerSignal）→ 写完后 signal 消费方等待（ConsumerWait）。
        Semaphore waitSem = _consumerSignalSem;
        Semaphore signalSem = _consumerWaitSem;
        PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        // struct 字段不可直接取址（CS0212）；复制为局部变量再取址（句柄值等价，不影响握手）。
        Fence fence = _frameFence;
        CommandBuffer cmdBuf = _commandBuffer;
        VulkanNative.ResetFences(_device, 1, &fence);
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSem,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmdBuf,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSem,
        };

        result = VulkanNative.QueueSubmit(_queue, 1, &submitInfo, (nint)fence.Handle);
        if (result != Result.Success)
        {
            _logger.LogWarning("Vulkan 共享表面 QueueSubmit 失败：{Result}", result);
            return false;
        }

        // 有限超时等待 GPU 完成（与 D3D11 16ms keyed mutex 超时对称）——超时=消费方未归还 → 丢帧。
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

        descriptor = new SharedGpuSurfaceDescriptor(
            _exportedMemoryHandle,
            _handleKind,
            w, h,
            SharedGpuSurfaceFormat.B8G8R8A8UNorm,
            _version,
            SharedGpuSyncMode.Semaphores);
        return true;
    }

    // ── 命令资源（命令池 + 命令缓冲 + 每帧 Fence）──
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

    // ── 信号量对（导出 + 握手初始化）──
    private void CreateSemaphores()
    {
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
        if (_sharedImage.Handle != 0 && _texW == w && _texH == h)
            return;

        // 拆除旧图像（保留 _version 语义：重建才 +1）
        ReleaseSharedSurface();

        // 图像创建：启用外部内存导出（pNext=ExternalMemoryImageCreateInfo）。
        ExternalMemoryImageCreateInfo extImageInfo = new()
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = _memHandleType,
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
            PNext = (void*)&extImageInfo,
        };

        Result result = VulkanNative.CreateImage(_device, &imageInfo, null, out _sharedImage);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage（共享表面离屏）失败: {result}");

        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, _sharedImage, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);

        // 内存分配：启用外部内存导出（pNext=ExportMemoryAllocateInfo）。
        ExternalMemoryHandleTypeFlags memHandle = _memHandleType;
        ExportMemoryAllocateInfo extMemInfo = new()
        {
            SType = StructureType.ExportMemoryAllocateInfo,
            HandleTypes = memHandle,
        };
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
            PNext = (void*)&extMemInfo,
        };
        result = VulkanNative.AllocateMemory(_device, &allocInfo, null, out _sharedMemory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory（共享表面离屏）失败: {result}");
        result = VulkanNative.BindImageMemory(_device, _sharedImage, _sharedMemory, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindImageMemory（共享表面离屏）失败: {result}");

        // 导出内存句柄（Windows=HANDLE，Linux/Android=fd）。
        if (_isWindows)
        {
            MemoryGetWin32HandleInfoKHR getInfo = new()
            {
                SType = StructureType.MemoryGetWin32HandleInfoKhr,
                Memory = _sharedMemory,
                HandleType = memHandle,
            };
            Result hR = VulkanNative.GetMemoryWin32HandleKHR(_device, &getInfo, out nint hMem);
            if (hR != Result.Success)
                throw new InvalidOperationException($"vkGetMemoryWin32HandleKHR 失败: {hR}");
            _exportedMemoryHandle = hMem;
        }
        else
        {
            MemoryGetFdInfoKHR getInfo = new()
            {
                SType = StructureType.MemoryGetFDInfoKhr,
                Memory = _sharedMemory,
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
            Image = _sharedImage,
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
        result = VulkanNative.CreateImageView(_device, &viewInfo, null, out _sharedImageView);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView（共享表面离屏）失败: {result}");

        _texW = w;
        _texH = h;
        _version++;
    }

    private void ReleaseSharedSurface()
    {
        if (_device.Handle == 0) return;
        if (_sharedImageView.Handle != 0)
        {
            VulkanNative.DestroyImageView(_device, _sharedImageView, null);
            _sharedImageView = default;
        }
        if (_sharedImage.Handle != 0)
        {
            VulkanNative.DestroyImage(_device, _sharedImage, null);
            _sharedImage = default;
        }
        if (_sharedMemory.Handle != 0)
        {
            VulkanNative.FreeMemory(_device, _sharedMemory, null);
            _sharedMemory = default;
        }
        // 导出的外部句柄随内存释放自动失效，无需 CloseHandle/dup；_version 保留递增语义。
        _exportedMemoryHandle = IntPtr.Zero;
        _texW = 0;
        _texH = 0;
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
        throw new InvalidOperationException("未找到合适的 Vulkan 内存类型（共享表面离屏）。");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_device.Handle != 0)
            VulkanNative.DeviceWaitIdle(_device);

        _pipeline?.Dispose();
        _pipeline = null;

        ReleaseSharedSurface();

        if (_frameFence.Handle != 0)
        {
            VulkanNative.DestroyFence(_device, _frameFence, null);
            _frameFence = default;
        }
        if (_commandPool.Handle != 0)
        {
            VulkanNative.DestroyCommandPool(_device, _commandPool, null);
            _commandPool = default;
        }
        if (_consumerWaitSem.Handle != 0)
        {
            VulkanNative.DestroySemaphore(_device, _consumerWaitSem, null);
            _consumerWaitSem = default;
        }
        if (_consumerSignalSem.Handle != 0)
        {
            VulkanNative.DestroySemaphore(_device, _consumerSignalSem, null);
            _consumerSignalSem = default;
        }
        _consumerWaitHandle = IntPtr.Zero;
        _consumerSignalHandle = IntPtr.Zero;
    }
}

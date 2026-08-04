namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan 视频渲染器。将 <see cref="VideoFrame"/> 呈现到 Vulkan SwapChain。
/// </summary>
/// <remarks>
/// <para>跨平台 GPU 渲染器（Windows / Linux / Android；macOS/MoltenVK <b>待开发</b>——
/// 缺 VK_EXT_metal_surface 分支与 portability enumeration 标志，见审计 M-6/D-3）。
/// Surface 创建用 Vulkan 自己的 WSI 扩展（VK_KHR_*_surface），不需要平台互操作文件。</para>
/// <para>WSI 扩展方法（Surface/Swapchain/Present）在 KhrSurface/KhrSwapchain 等扩展对象上，
/// 由工厂通过 Vk.TryGetInstanceExtension/TryGetDeviceExtension 加载后注入。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：contract，返回 <see cref="Task.CompletedTask"/></item>
/// <item><see cref="Attach"/>/<see cref="Detach"/>：native sync，UI 线程</item>
/// <item><see cref="Present"/>/<see cref="Clear"/>：native sync，渲染线程</item>
/// <item><see cref="DisposeAsync"/>：contract，委托 Dispose + CompletedTask，非伪异步</item>
/// </list>
/// <para><b>线程安全</b>：<c>_gate</c> 锁串行化所有公开方法。
/// A-L8：<see cref="Present"/> 的 vkAcquireNextImage 以 <c>AcquireTimeoutNs</c>（2 秒）超时
/// 在 <c>_gate</c> 锁内阻塞——并发调用 <see cref="Dispose"/>/<see cref="Detach"/> 最坏被卡约 2 秒后才能获得锁。
/// 这是「有限超时替代无限等待」权衡（审计 M1）的已知副作用，属预期行为而非死锁。</para>
/// <para><b>已知性能限制（V1）</b>：<see cref="RecordAndSubmitFrame"/> 中使用 <c>vkQueueWaitIdle</c>
/// 每帧同步 GPU——确保 Command Buffer 可安全复用但消除 GPU 并行。V3 将改用 Fence 或环形 Command Buffer。</para>
/// <para><b>已知功能限制（V1）</b>：不支持帧尺寸与 SwapChain 尺寸不匹配的缩放（需 Shader/Blit，V3 实现）。
/// Linux X11/Wayland Surface 创建缺少 Display 指针——明确抛 <see cref="PlatformNotSupportedException"/>（B-M10，V3 扩展契约后支持）。
/// B-M2/B-M9：ErrorOutOfDateKhr 已由 <c>RecreateSwapchain</c> 就地重建（含信号量重建，消除 signaled 残留）；
/// 其余 QueuePresent 硬失败仍抛异常，由会话层重新 Attach 恢复（信号量在重 Attach 时重建，无 double-signal 风险）。</para>
/// <para>AOT 兼容：sealed unsafe 类，无反射，pattern matching。</para>
/// </remarks>
internal sealed unsafe partial class VulkanRenderer : IVideoRenderer
{
    // ── 共享资源（工厂注入，不由本类释放）──
    private readonly Vk _vk;
    private readonly Instance _instance;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly uint _queueFamilyIndex;
    private readonly KhrSurface _khrSurface;
    private readonly KhrSwapchain _khrSwapchain;
    private readonly KhrWin32Surface? _khrWin32Surface;
    private readonly KhrXlibSurface? _khrXlibSurface;
    private readonly KhrWaylandSurface? _khrWaylandSurface;
    private readonly KhrAndroidSurface? _khrAndroidSurface;
    private readonly ILogger<VulkanRenderer> _logger;

    // ── Session 级资源 ──
    private SurfaceKHR _surface;
    private SwapchainKHR _swapchain;
    private Image[] _swapchainImages = [];
    // R3-9: _swapchainImageViews 已移除——V1 仅用 CmdCopyBufferToImage（直接操作 VkImage），
    // 不需要 ImageView。V3 实现 Shader 渲染时需重新添加。
    private Format _swapchainFormat;
    private Extent2D _swapchainExtent;
    // B-M2: Attach 时记录目标尺寸，供 OutOfDate 重建 SwapChain 使用
    //（CreateSwapchain 优先取 Surface CurrentExtent，此值仅作 CurrentExtent 不可用时的回退）。
    private uint _targetWidth;
    private uint _targetHeight;
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private Semaphore _imageAvailableSemaphore;
    private Semaphore _renderFinishedSemaphore;

    // M3: 预分配 PresentInfo 数组，避免每帧 GC 分配
    private readonly PresentInfoKHR[] _presentInfoArr = [new PresentInfoKHR()];

    // ── 暂存缓冲 ──
    private Buffer _stagingBuffer;
    private DeviceMemory _stagingMemory;
    private ulong _stagingBufferSize;

    private bool _disposed;
    private bool _attached;
    private readonly object _gate = new();

    // M1: AcquireNextImage 超时——2 秒（纳秒），避免窗口最小化时永久阻塞
    private const ulong AcquireTimeoutNs = 2_000_000_000;

    internal VulkanRenderer(
        Vk vk, Instance instance, PhysicalDevice physicalDevice,
        Device device, Queue queue, uint queueFamilyIndex,
        KhrSurface khrSurface, KhrSwapchain khrSwapchain,
        KhrWin32Surface? khrWin32Surface, KhrXlibSurface? khrXlibSurface,
        KhrWaylandSurface? khrWaylandSurface, KhrAndroidSurface? khrAndroidSurface,
        ILogger<VulkanRenderer> logger)
    {
        _vk = vk;
        _instance = instance;
        _physicalDevice = physicalDevice;
        _device = device;
        _queue = queue;
        _queueFamilyIndex = queueFamilyIndex;
        _khrSurface = khrSurface;
        _khrSwapchain = khrSwapchain;
        _khrWin32Surface = khrWin32Surface;
        _khrXlibSurface = khrXlibSurface;
        _khrWaylandSurface = khrWaylandSurface;
        _khrAndroidSurface = khrAndroidSurface;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal bool IsDisposed => _disposed;

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    // ═══════════════ Attach ═══════════════

    /// <inheritdoc/>
    public void Attach(IRenderTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            if (_disposed) return;
            if (_attached) { _logger.LogWarning("Vulkan 渲染器已附加，先 Detach。"); Detach(); }

            if (target.HandleType != RenderHandleType.Pointer)
                throw new NotSupportedException($"Vulkan 渲染器仅支持 {nameof(RenderHandleType.Pointer)}。");
            if (target.NativeHandle is not IntPtr handle || handle == IntPtr.Zero)
                throw new ArgumentException("渲染目标句柄无效。", nameof(target));
            if (target.Width <= 0 || target.Height <= 0)
                throw new ArgumentException($"尺寸无效：{target.Width}x{target.Height}。", nameof(target));

            try
            {
                _targetWidth = (uint)target.Width;
                _targetHeight = (uint)target.Height;
                CreateSurface(handle);
                CreateSwapchain((uint)target.Width, (uint)target.Height);
                CreateCommandPoolAndBuffer();
                CreateSemaphores();
                _attached = true;
                _logger.LogDebug("Vulkan 渲染器已附加：{W}x{H}", target.Width, target.Height);
            }
            catch { ReleaseSessionResources(); throw; }
        }
    }

    // ═══════════════ Present ═══════════════

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            if (_disposed) return;
            if (!_attached || _swapchain.Handle == 0)
                throw new InvalidOperationException("渲染器未附加渲染目标。");

            // 1. 获取下一张 SwapChain 图像
            // A-L8: 2 秒超时在 _gate 锁内阻塞，并发 Dispose/Detach 最坏等约 2 秒（预期行为，见类级注释）
            Span<uint> imageIndexSpan = stackalloc uint[1];
            Result result = _khrSwapchain.AcquireNextImage(
                _device, _swapchain, AcquireTimeoutNs,
                _imageAvailableSemaphore, default, imageIndexSpan);
            if (result == Result.Timeout)
            {
                _logger.LogWarning("vkAcquireNextImage 超时，跳过本帧。");
                return;
            }
            if (result == Result.ErrorOutOfDateKhr)
            {
                // B-M2: SwapChain 过期（窗口尺寸/显示模式变化）——就地重建，跳过本帧，
                // 下一帧用新 SwapChain 正常渲染。规范保证 OutOfDate 时 Acquire 不信号 semaphore。
                RecreateSwapchain();
                return;
            }
            if (result == Result.ErrorDeviceLost)
            {
                // B-DEVLOST: VK_ERROR_DEVICE_LOST——设备及全部资源永久失效，
                // 抛中立 GpuDeviceLostException 供会话层释放并重建。
                _logger.LogError("Vulkan 设备丢失（vkAcquireNextImage）。");
                throw new GpuDeviceLostException("Vulkan 设备已丢失（vkAcquireNextImage 返回 VK_ERROR_DEVICE_LOST）。需释放并重建渲染会话。");
            }
            if (result != Result.Success && result != Result.SuboptimalKhr)
                throw new InvalidOperationException($"vkAcquireNextImage 失败: {result}");
            uint imageIndex = imageIndexSpan[0];

            // 2. 记录+提交命令缓冲
            try
            {
                RecordAndSubmitFrame(frame, imageIndex);
            }
            catch (Exception ex) when (ex is not GpuDeviceLostException)
            {
                // R2-1: 信号量恢复——AcquireNextImage 已信号 _imageAvailableSemaphore，
                // 但 QueueSubmit 前异常导致信号量未消费。必须提交最小命令缓冲消费信号量，
                // 否则下次 AcquireNextImage 因信号量已信号而永久超时（渲染器永久卡死）。
                // B-DEVLOST: 设备丢失时跳过恢复（设备已死，提交必然失败且徒增日志噪音），直接上抛。
                RecoverSemaphore(imageIndex);
                throw;
            }

            // 3. 呈现（M3: 复用预分配数组）
            SwapchainKHR swap = _swapchain;
            Semaphore renderFin = _renderFinishedSemaphore;
            uint idx = imageIndex;

            _presentInfoArr[0] = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFin,
                SwapchainCount = 1,
                PSwapchains = &swap,
                PImageIndices = &idx,
            };

            result = _khrSwapchain.QueuePresent(_queue, _presentInfoArr);
            if (result == Result.ErrorOutOfDateKhr)
            {
                // B-M2/B-M9: Present 阶段过期——丢弃本帧并重建 SwapChain。
                // RecreateSwapchain 内部同时重建两个信号量，消除 QueuePresent
                // 失败后 _renderFinishedSemaphore 可能的 signaled 残留（B-M9）。
                RecreateSwapchain();
                return;
            }
            if (result == Result.ErrorDeviceLost)
            {
                // B-DEVLOST: 同 AcquireNextImage——类型化异常供会话层重建
                _logger.LogError("Vulkan 设备丢失（vkQueuePresent）。");
                throw new GpuDeviceLostException("Vulkan 设备已丢失（vkQueuePresent 返回 VK_ERROR_DEVICE_LOST）。需释放并重建渲染会话。");
            }
            if (result != Result.Success && result != Result.SuboptimalKhr)
                throw new InvalidOperationException($"vkQueuePresent 失败: {result}");
        }
    }

    private void RecordAndSubmitFrame(VideoFrame frame, uint imageIndex)
    {
        Image swapchainImage = _swapchainImages[imageIndex];

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        // H3: 检查 BeginCommandBuffer 返回值
        Result result = _vk.BeginCommandBuffer(_commandBuffer, ref beginInfo);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBeginCommandBuffer 失败: {result}");

        // A-H4: Undefined→TransferDst 首屏障 srcStage 必须等于信号量 waitDstStageMask（TransferBit），
        // 才能与 _imageAvailableSemaphore 的等待形成依赖链（Khronos Synchronization Examples
        // 「Swapchain image acquire」范式）。若用 TopOfPipe（srcScope 为空），布局转换（对图像的写）
        // 与 presentation engine 的读取之间没有执行依赖 → 规范级数据竞争。srcAccess 保持 None。
        TransitionImageLayout(swapchainImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            AccessFlags.None, AccessFlags.TransferWriteBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);

        switch (frame.Resource)
        {
            case SoftwareFrameResource sw:
                UploadSoftwareFrame(sw, swapchainImage);
                break;
            case VulkanImageResource vk:
                BlitVulkanImageResource(vk, swapchainImage);
                break;
            default:
                throw new NotSupportedException(
                    $"不支持的帧资源类型：{frame.Resource?.GetType().Name ?? "null"}。");
        }

        // C3: TransferDst→PresentSrc，srcStage=Transfer（前一阶段写入），dstStage=BottomOfPipe（presentation engine 读取）
        TransitionImageLayout(swapchainImage, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr,
            AccessFlags.TransferWriteBit, AccessFlags.MemoryReadBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);

        // H3: 检查 EndCommandBuffer 返回值
        result = _vk.EndCommandBuffer(_commandBuffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkEndCommandBuffer 失败: {result}");

        // 提交
        CommandBuffer cmd = _commandBuffer;
        Semaphore imgAvail = _imageAvailableSemaphore;
        Semaphore renderFin = _renderFinishedSemaphore;
        // R3-1: WaitDstStageMask 必须为 TransferBit——命令缓冲仅含 Transfer 操作（CopyBufferToImage + LayoutTransition），
        // Transfer 阶段在管线中早于 ColorAttachmentOutput。若用 ColorAttachmentOutputBit，Transfer 操作
        // 不在等待范围内，可能在信号量信号前执行，导致写入尚未被 Acquire 的 SwapChain 图像（数据竞争）。
        PipelineStageFlags waitStage = PipelineStageFlags.TransferBit;

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &imgAvail,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &renderFin,
        };

        result = _vk.QueueSubmit(_queue, 1, &submitInfo, default);
        ThrowIfDeviceLost(result, "vkQueueSubmit"); // B-DEVLOST（审计补漏）：QueueSubmit 是 TDR 设备丢失最常见的浮现点
        if (result != Result.Success)
            throw new InvalidOperationException($"vkQueueSubmit 失败: {result}");

        // H2: 已知性能限制——QueueWaitIdle 每帧同步 GPU 以确保 Command Buffer 可安全复用。
        // V3 将改用 vkCreateFence + vkWaitForFences 或环形 Command Buffer 消除此阻塞。
        // B-DEVLOST（复审补漏）：「提交成功、GPU 执行中 TDR」的设备丢失恰从 WaitIdle 浮现——必须检测。
        // 其余失败码（OOM 等）保持既有忽略语义不变。
        ThrowIfDeviceLost(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle");
    }

    private void UploadSoftwareFrame(SoftwareFrameResource sw, Image dstImage)
    {
        int width = sw.Width;
        int height = sw.Height;

        // H1: 帧尺寸超过 SwapChain 尺寸时拒绝拷贝——vkCmdCopyBufferToImage 越界会触发 validation error
        if ((uint)width > _swapchainExtent.Width || (uint)height > _swapchainExtent.Height)
            throw new NotSupportedException(
                $"帧尺寸 {width}x{height} 超过 SwapChain 尺寸 {_swapchainExtent.Width}x{_swapchainExtent.Height}。" +
                "Vulkan 渲染器 V1 不支持帧缩放（V3 将通过 Shader/Blit 实现）。");

        int dataSize = width * height * 4;
        int rowBytes = width * 4;
        int rowPitch = sw.Stride > 0 ? sw.Stride : rowBytes;

        // A-M12: Stride 合法性——stride 不得小于一行像素字节数，否则行内数据不完整
        if (rowPitch < rowBytes)
            throw new InvalidOperationException(
                $"帧 Stride {rowPitch} 小于行字节数 {rowBytes}（{width}x{height} BGRA32）。");

        // R4-4 + A-M12: 按 stride 公式校验源数据长度——Stride > width*4 时实际需要
        // (height-1)*Stride + width*4（最后一行只需 rowBytes，不含尾部填充）。
        // 旧校验只查 width*height*4，strided 帧会「校验通过却在拷贝中途 Span 越界」——失败点后移。
        long requiredSrcLen = (long)(height - 1) * rowPitch + rowBytes;
        if (sw.Data.Length < requiredSrcLen)
            throw new InvalidOperationException(
                $"帧数据长度 {sw.Data.Length} 不足以填充 {width}x{height} BGRA32 帧" +
                $"（Stride={rowPitch}，需要 {requiredSrcLen} 字节）。");

        EnsureStagingBuffer((ulong)dataSize);

        void* pData = null;
        // H3: 检查 MapMemory 返回值
        Result mapResult = _vk.MapMemory(_device, _stagingMemory, 0, (ulong)dataSize, 0, &pData);
        if (mapResult != Result.Success)
            throw new InvalidOperationException($"vkMapMemory 失败: {mapResult}");
        try
        {
            Span<byte> dst = new(pData, dataSize);
            var src = sw.Data.Span;

            // B-M4: SwapChain 可能是 BGRA8（首选）或 RGBA8（回退）。
            // 源格式与 SwapChain 格式 R/B 顺序一致 → 直拷；不一致 → R/B 交换拷贝。
            bool swapchainIsBgra = _swapchainFormat == Format.B8G8R8A8Unorm;
            bool sameChannelOrder = sw.Format switch
            {
                PixelFormat.BGRA32 => swapchainIsBgra,
                PixelFormat.RGBA32 => !swapchainIsBgra,
                _ => throw new NotSupportedException($"Vulkan 渲染器不支持像素格式 {sw.Format}。"),
            };

            if (sameChannelOrder)
            {
                // R3-5: sw.Data.Span 可能比 dataSize 长（ArrayPool 租借的数组更长），
                // 用 Slice 确保只拷贝实际帧数据量
                if (rowPitch == rowBytes)
                    src.Slice(0, dataSize).CopyTo(dst);
                else
                    CopyStrided(src, dst, width, height, rowPitch);
            }
            else
            {
                SwapRbAndCopy(src, dst, width, height, rowPitch);
            }
        }
        finally { _vk.UnmapMemory(_device, _stagingMemory); }

        BufferImageCopy copyRegion = new()
        {
            BufferOffset = 0,
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
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };

        _vk.CmdCopyBufferToImage(_commandBuffer, _stagingBuffer, dstImage,
            ImageLayout.TransferDstOptimal, 1, &copyRegion);
    }

    /// <summary>
    /// VK-ZERO：Vulkan GPU 纹理零拷贝 Present 路径。
    /// 将 <see cref="VulkanImageResource"/> 的 <c>VkImage</c> blit/copy 到 SwapChain 图像。
    /// </summary>
    /// <remarks>
    /// <para>同尺寸且格式一致 → <c>vkCmdCopyImage</c>（零缩放，与软帧 CopyBufferToImage 语义一致）；
    /// 尺寸不同（缩放）或格式不同（R/B 顺序 / UNORM↔sRGB 转换）→ <c>vkCmdBlitImage</c>（Linear 过滤，
    /// 与 D3D11 双线性缩放语义一致）。多平面 / 24 位格式（NV12/NV21/YUV*/RGB24）Vulkan blit 不支持，归 V3。</para>
    /// <para>异步策略：同步原生调用（无 I/O await），符合 Present 的 sync-only 铁律。</para>
    /// <para>AOT 兼容：无反射、无新增 P/Invoke（复用 Vortice 源生成 <c>LibraryImport</c> 绑定）。</para>
    /// </remarks>
    internal void BlitVulkanImageResource(VulkanImageResource src, Image dstImage)
    {
        int srcW = src.Width;
        int srcH = src.Height;
        uint dstW = _swapchainExtent.Width;
        uint dstH = _swapchainExtent.Height;

        // 多平面 / 24 位等 Vulkan blit 不支持或需转码 → 归 V3
        Format srcVkFormat = src.Format switch
        {
            PixelFormat.BGRA32 => Format.B8G8R8A8Unorm,
            PixelFormat.RGBA32 => Format.R8G8B8A8Unorm,
            PixelFormat.NV12 or PixelFormat.NV21 or PixelFormat.YUV420P
                or PixelFormat.YUV422P or PixelFormat.YUV444P or PixelFormat.RGB24
                => throw new NotSupportedException(
                    $"Vulkan GPU 纹理零拷贝暂不支持格式 {src.Format}（多平面/24 位需 Shader 转码，V3 实现）。"),
            _ => throw new NotSupportedException($"Vulkan 渲染器不支持的像素格式 {src.Format}。"),
        };

        // 源图像：交付布局 → TransferSrcOptimal（生产者可能以其他布局交付）
        TransitionImageLayout(src.Image, src.CurrentLayout, ImageLayout.TransferSrcOptimal,
            AccessFlags.None, AccessFlags.TransferReadBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);

        bool sameSize = (uint)srcW == dstW && (uint)srcH == dstH;
        if (sameSize && srcVkFormat == _swapchainFormat)
        {
            // 单平面、同尺寸、同格式 → 直接 Image Copy（零缩放）
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
                Extent = new Extent3D((uint)srcW, (uint)srcH, 1),
            };
            _vk.CmdCopyImage(_commandBuffer, src.Image, ImageLayout.TransferSrcOptimal,
                dstImage, ImageLayout.TransferDstOptimal, 1, &region);
        }
        else
        {
            // 尺寸不同（缩放）或格式不同（R/B 顺序 / UNORM↔sRGB 转换）→ Blit（Linear 过滤）
            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
            blit.SrcOffsets[1] = new Offset3D(srcW, srcH, 1);
            blit.DstOffsets[0] = new Offset3D(0, 0, 0);
            blit.DstOffsets[1] = new Offset3D((int)dstW, (int)dstH, 1);
            _vk.CmdBlitImage(_commandBuffer, src.Image, ImageLayout.TransferSrcOptimal,
                dstImage, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
        }
    }

    // ═══════════════ Clear ═══════════════

    /// <inheritdoc />
    public TimeSpan PresentationLatency => TimeSpan.Zero;

    /// <inheritdoc/>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_disposed || !_attached || _swapchain.Handle == 0) return;

            // M1: 有限超时
            Span<uint> imageIndexSpan = stackalloc uint[1];
            Result result = _khrSwapchain.AcquireNextImage(
                _device, _swapchain, AcquireTimeoutNs,
                _imageAvailableSemaphore, default, imageIndexSpan);
            if (result == Result.Timeout)
            {
                _logger.LogWarning("vkAcquireNextImage 超时，跳过 Clear。");
                return;
            }
            if (result == Result.ErrorOutOfDateKhr)
            {
                // B-M2: 同 Present——过期即重建，跳过本次 Clear
                RecreateSwapchain();
                return;
            }
            if (result == Result.ErrorDeviceLost)
            {
                // B-DEVLOST: 同 Present——Clear 也须类型化上抛（与 D3D11 Clear 的 ThrowIfDeviceLost 对称），
                // 否则设备丢失被静默吞掉，会话层无从感知与重建。
                _logger.LogError("Vulkan 设备丢失（vkAcquireNextImage/Clear）。");
                throw new GpuDeviceLostException("Vulkan 设备已丢失（Clear 的 vkAcquireNextImage 返回 VK_ERROR_DEVICE_LOST）。需释放并重建渲染会话。");
            }
            if (result != Result.Success && result != Result.SuboptimalKhr) return;
            uint imageIndex = imageIndexSpan[0];

            try
            {
                Image swapchainImage = _swapchainImages[imageIndex];

                CommandBufferBeginInfo beginInfo = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                // H3: 检查 BeginCommandBuffer 返回值
                result = _vk.BeginCommandBuffer(_commandBuffer, ref beginInfo);
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkBeginCommandBuffer 失败: {result}");

                // A-H4: Undefined→TransferDst，srcStage=TransferBit 与信号量 waitDstStageMask 对齐——同 RecordAndSubmitFrame
                TransitionImageLayout(swapchainImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    AccessFlags.None, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);

                ClearColorValue clearColor = new() { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 0 };
                ImageSubresourceRange range = new()
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                };

                _vk.CmdClearColorImage(_commandBuffer, swapchainImage,
                    ImageLayout.TransferDstOptimal, &clearColor, 1, &range);

                // C3: TransferDst→PresentSrc
                TransitionImageLayout(swapchainImage, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr,
                    AccessFlags.TransferWriteBit, AccessFlags.MemoryReadBit,
                    PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);

                // H3: 检查 EndCommandBuffer 返回值
                result = _vk.EndCommandBuffer(_commandBuffer);
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkEndCommandBuffer 失败: {result}");

                CommandBuffer cmd = _commandBuffer;
                Semaphore imgAvail = _imageAvailableSemaphore;
                Semaphore renderFin = _renderFinishedSemaphore;
                // R3-1: TransferBit 而非 ColorAttachmentOutputBit——同 RecordAndSubmitFrame
                PipelineStageFlags waitStage = PipelineStageFlags.TransferBit;

                SubmitInfo submitInfo = new()
                {
                    SType = StructureType.SubmitInfo,
                    WaitSemaphoreCount = 1,
                    PWaitSemaphores = &imgAvail,
                    PWaitDstStageMask = &waitStage,
                    CommandBufferCount = 1,
                    PCommandBuffers = &cmd,
                    SignalSemaphoreCount = 1,
                    PSignalSemaphores = &renderFin,
                };

                // H3: 检查 QueueSubmit 返回值
                result = _vk.QueueSubmit(_queue, 1, &submitInfo, default);
                // B-DEVLOST（复审补漏）：Clear 的 QueueSubmit 同样须类型化——否则设备丢失变泛化异常
                // 被下方 catch 过滤器捕获吞掉（对称于 Present 侧 RecordAndSubmitFrame）。
                ThrowIfDeviceLost(result, "vkQueueSubmit/Clear");
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkQueueSubmit 失败: {result}");

                // H2: 已知性能限制——同 RecordAndSubmitFrame
                // B-DEVLOST（复审补漏）：同 Present——GPU 执行中 TDR 从 WaitIdle 浮现
                ThrowIfDeviceLost(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle/Clear");

                // M3: 复用预分配数组
                SwapchainKHR swap = _swapchain;
                uint idx = imageIndex;
                _presentInfoArr[0] = new PresentInfoKHR
                {
                    SType = StructureType.PresentInfoKhr,
                    WaitSemaphoreCount = 1,
                    PWaitSemaphores = &renderFin,
                    SwapchainCount = 1,
                    PSwapchains = &swap,
                    PImageIndices = &idx,
                };

                // H3: 检查 QueuePresent 返回值
                result = _khrSwapchain.QueuePresent(_queue, _presentInfoArr);
                if (result == Result.ErrorOutOfDateKhr)
                {
                    // B-M2: Clear 的 Present 阶段过期——重建（信号量一并重建，无残留）
                    RecreateSwapchain();
                }
                else if (result == Result.ErrorDeviceLost)
                {
                    // B-DEVLOST: 同 Present——类型化上抛（catch 过滤器放行，见下）
                    _logger.LogError("Vulkan 设备丢失（vkQueuePresent/Clear）。");
                    throw new GpuDeviceLostException("Vulkan 设备已丢失（Clear 的 vkQueuePresent 返回 VK_ERROR_DEVICE_LOST）。需释放并重建渲染会话。");
                }
                else if (result != Result.Success && result != Result.SuboptimalKhr)
                    _logger.LogWarning("vkQueuePresent 失败: {Result}", result);
            }
            catch (Exception ex) when (ex is not GpuDeviceLostException)
            {
                // R2-1: 信号量恢复——同 Present，Clear 异常后也需消费信号量。
                // B-DEVLOST: GpuDeviceLostException 不在此吞掉——设备已死，信号量恢复无意义
                // 且必须让会话层感知（catch 过滤器放行类型化异常）。
                _logger.LogWarning(ex, "Clear 操作失败");
                RecoverSemaphore(imageIndex);
            }
        }
    }

    // ═══════════════ Detach / Dispose ═══════════════

    /// <inheritdoc/>
    public void Detach()
    {
        lock (_gate)
        {
            if (_disposed || !_attached) return;
            ReleaseSessionResources();
            _attached = false;
            _logger.LogDebug("Vulkan 渲染器已解绑");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseSessionResources();
            _attached = false;
            _logger.LogDebug("Vulkan 渲染器已释放");
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    // ═══════════════ 内部辅助方法 ═══════════════

    // A-M1: 获取进程模块句柄供 Win32SurfaceCreateInfoKHR.Hinstance 使用。
    // LibraryImport P/Invoke，NativeAOT 兼容（源生成 marshaller，直接 P/Invoke，无运行时反射式封送）。
    [System.Runtime.InteropServices.LibraryImport("kernel32")]
    private static partial nint GetModuleHandleW(nint lpModuleName);

    private void CreateSurface(IntPtr handle)
    {
        SurfaceKHR[] surfArr = new SurfaceKHR[1];
        Result result;

        if (OperatingSystem.IsWindows() && _khrWin32Surface is not null)
        {
            // A-M1: VUID-VkWin32SurfaceCreateInfoKHR-hinstance-01307 要求有效 HINSTANCE，
            // 不能默认 0 靠驱动宽容（validation layer 必报错）。GetModuleHandleW(null) = 进程模块句柄。
            var info = new Win32SurfaceCreateInfoKHR
            {
                SType = StructureType.Win32SurfaceCreateInfoKhr,
                Hinstance = GetModuleHandleW(0),
                Hwnd = handle,
            };
            result = _khrWin32Surface.CreateWin32Surface(_instance, &info, (AllocationCallbacks*)null, surfArr);
        }
        else if (OperatingSystem.IsAndroid() && _khrAndroidSurface is not null)
        {
            // A-H3: handle 本身就是 ANativeWindow*（IRenderTarget 传来的原生窗口指针）。
            // 绝不能写 &handle——那是「指向栈局部变量的指针」，驱动会把栈地址当 ANativeWindow* 解引用（UB）。
            // 对照 Win32 路径 Hwnd = handle 的直接赋值语义：字段里装的必须是窗口指针值本身。
            var info = new AndroidSurfaceCreateInfoKHR
            {
                SType = StructureType.AndroidSurfaceCreateInfoKhr,
                Window = (nint*)handle,
            };
            result = _khrAndroidSurface.CreateAndroidSurface(_instance, &info, (AllocationCallbacks*)null, surfArr);
        }
        else if (OperatingSystem.IsLinux())
        {
            // B-M10：X11/Wayland Surface 创建需要 Display* 指针（Xlib 的 Dpy / Wayland 的 wl_display*），
            // 当前 IRenderTarget.NativeHandle 仅传递单个 IntPtr（窗口句柄），无法携带 Display 指针。
            // 旧「预留骨架」以缺失 Dpy/Display 的方式调用驱动，属于未定义行为（驱动解引用空 Display）。
            // 按平台范围决策（V2-16）Linux 原生 Surface 不在范围——明确抛 PNS，快速失败优于 UB。
            // V3 若排期：需扩展 IRenderTarget 契约（复合句柄/ExtraFields）携带 Display* 后再实现。
            _logger.LogWarning(
                "Linux Vulkan Surface 创建被拒绝（Xlib 扩展可用: {HasXlib}, Wayland 扩展可用: {HasWayland}）——缺少 Display* 传递通道。",
                _khrXlibSurface is not null, _khrWaylandSurface is not null);
            throw new PlatformNotSupportedException(
                "Linux 原生 Vulkan Surface 需要 Display* 指针（X11 Dpy / wl_display*），" +
                "当前 IRenderTarget.NativeHandle 仅单一窗口句柄无法携带，V3 扩展契约后才支持。");
        }
        else
        {
            throw new PlatformNotSupportedException("Vulkan Surface 创建不支持当前平台。");
        }

        if (result != Result.Success)
            throw new InvalidOperationException($"Vulkan Surface 创建失败: {result}");
        _surface = surfArr[0];
    }

    private void CreateSwapchain(uint width, uint height)
    {
        SurfaceCapabilitiesKHR[] capsArr = new SurfaceCapabilitiesKHR[1];
        // R3-4: 检查 GetPhysicalDeviceSurfaceCapabilities 返回值
        Result capsResult = _khrSurface.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, capsArr);
        if (capsResult != Result.Success)
            throw new InvalidOperationException($"vkGetPhysicalDeviceSurfaceCapabilitiesKHR 失败: {capsResult}");
        ref SurfaceCapabilitiesKHR caps = ref capsArr[0];

        uint formatCount = 0;
        // R3-4: 检查 GetPhysicalDeviceSurfaceFormats 返回值
        Result fmtResult = _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null);
        if (fmtResult != Result.Success)
            throw new InvalidOperationException($"vkGetPhysicalDeviceSurfaceFormatsKHR 失败: {fmtResult}");
        if (formatCount == 0)
            throw new InvalidOperationException("Surface 无可用格式。");

        var formats = new SurfaceFormatKHR[formatCount];
        // R4-2: 检查第二次 GetPhysicalDeviceSurfaceFormats 返回值
        Result fmtResult2 = _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formats);
        if (fmtResult2 != Result.Success)
            throw new InvalidOperationException($"vkGetPhysicalDeviceSurfaceFormatsKHR (第二次) 失败: {fmtResult2}");

        // B-M4: 优选 B8G8R8A8Unorm，回退 R8G8B8A8Unorm（部分 Android/移动驱动仅报 RGBA8）。
        // UploadSoftwareFrame 按「源像素格式与 SwapChain 格式 R/B 顺序是否一致」动态决定直拷/交换，
        // 两种格式均能正确渲染。仅当两者都不支持时抛异常，避免颜色错乱。
        SurfaceFormatKHR selectedFormat = default;
        bool formatFound = false;
        foreach (var f in formats)
        {
            if (f.Format == Format.B8G8R8A8Unorm) { selectedFormat = f; formatFound = true; break; }
        }
        if (!formatFound)
        {
            foreach (var f in formats)
            {
                if (f.Format == Format.R8G8B8A8Unorm) { selectedFormat = f; formatFound = true; break; }
            }
        }
        if (!formatFound)
            throw new NotSupportedException(
                $"Surface 既不支持 B8G8R8A8Unorm 也不支持 R8G8B8A8Unorm（可用: {string.Join(", ", formats.Select(f => f.Format))}）。" +
                "Vulkan 渲染器 V1 仅支持 8bit BGRA/RGBA SwapChain。");

        _swapchainFormat = selectedFormat.Format;
        // R3-8: 钳制 Extent 到 Surface 能力范围——CurrentExtent.Width==uint.MaxValue 表示由 SwapChain 决定尺寸
        if (caps.CurrentExtent.Width != uint.MaxValue)
        {
            _swapchainExtent = caps.CurrentExtent;
        }
        else
        {
            _swapchainExtent = new Extent2D(
                System.Math.Clamp(width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                System.Math.Clamp(height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
        }

        // M4: MinImageCount 下限保护——至少双缓冲（某些驱动返回 0 或 1）
        // R2-2: 上限保护——不超过 MaxImageCount（MaxImageCount=0 表示无限制）
        uint minImageCount = System.Math.Max(2u, caps.MinImageCount);
        if (caps.MaxImageCount > 0 && minImageCount > caps.MaxImageCount)
            minImageCount = caps.MaxImageCount;

        SwapchainCreateInfoKHR swapInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = minImageCount,
            ImageFormat = selectedFormat.Format,
            ImageColorSpace = selectedFormat.ColorSpace,
            ImageExtent = _swapchainExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            // M3: 按 Surface 支持的 CompositeAlpha 位选择——Opaque 首选，否则 Inherit/PreMultiplied/PostMultiplied
            // （Wayland/Android 常见仅支持后者，硬写 Opaque 会导致 vkCreateSwapchain 失败）。
            // 审计补漏：末位不再硬选 PreMultiplied——仅 PostMultiplied 可用的驱动也能命中支持位
            //（规范保证 SupportedCompositeAlpha 至少置位一个，四选一必中）。
            CompositeAlpha = SelectCompositeAlpha(caps.SupportedCompositeAlpha),
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true,
            OldSwapchain = default,
        };

        SwapchainKHR[] swapArr = new SwapchainKHR[1];
        Result result = _khrSwapchain.CreateSwapchain(_device, &swapInfo, (AllocationCallbacks*)null, swapArr);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateSwapchain 失败: {result}");
        _swapchain = swapArr[0];

        uint imageCount = 0;
        // R3-4: 检查 GetSwapchainImages 返回值
        Result imgResult = _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, null);
        if (imgResult != Result.Success)
            throw new InvalidOperationException($"vkGetSwapchainImagesKHR 失败: {imgResult}");
        _swapchainImages = new Image[imageCount];
        // R4-2: 检查第二次 GetSwapchainImages 返回值
        Result imgResult2 = _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, _swapchainImages);
        if (imgResult2 != Result.Success)
            throw new InvalidOperationException($"vkGetSwapchainImagesKHR (第二次) 失败: {imgResult2}");
    }

    /// <summary>
    /// B-DEVLOST: 统一的设备丢失检测——结果为 <see cref="Result.ErrorDeviceLost"/> 时
    /// 记录错误并抛中立 <see cref="GpuDeviceLostException"/> 供会话层释放并重建；其余结果不处理。
    /// </summary>
    /// <param name="result">Vulkan API 返回值。</param>
    /// <param name="operation">用于日志与异常消息的操作名。</param>
    private void ThrowIfDeviceLost(Result result, string operation)
    {
        if (result != Result.ErrorDeviceLost) return;
        _logger.LogError("Vulkan 设备丢失（{Operation}）。", operation);
        throw new GpuDeviceLostException($"Vulkan 设备已丢失（{operation} 返回 VK_ERROR_DEVICE_LOST）。需释放并重建渲染会话。");
    }

    // M3（审计补漏）：CompositeAlpha 四级回退——Opaque > Inherit > PreMultiplied > PostMultiplied，
    // 逐位测试支持标志，全不中（违反规范的驱动）才回退 Opaque 让 vkCreateSwapchain 报出明确错误。
    private static CompositeAlphaFlagsKHR SelectCompositeAlpha(CompositeAlphaFlagsKHR supported)
    {
        if ((supported & CompositeAlphaFlagsKHR.OpaqueBitKhr) != 0) return CompositeAlphaFlagsKHR.OpaqueBitKhr;
        if ((supported & CompositeAlphaFlagsKHR.InheritBitKhr) != 0) return CompositeAlphaFlagsKHR.InheritBitKhr;
        if ((supported & CompositeAlphaFlagsKHR.PreMultipliedBitKhr) != 0) return CompositeAlphaFlagsKHR.PreMultipliedBitKhr;
        if ((supported & CompositeAlphaFlagsKHR.PostMultipliedBitKhr) != 0) return CompositeAlphaFlagsKHR.PostMultipliedBitKhr;
        return CompositeAlphaFlagsKHR.OpaqueBitKhr;
    }

    internal void CreateCommandPoolAndBuffer()
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _queueFamilyIndex,
        };

        Result result = _vk.CreateCommandPool(_device, ref poolInfo, null, out _commandPool);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateCommandPool 失败: {result}");

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        var cmds = stackalloc CommandBuffer[1];
        // H3: 检查 AllocateCommandBuffers 返回值
        result = _vk.AllocateCommandBuffers(_device, &allocInfo, cmds);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateCommandBuffers 失败: {result}");
        _commandBuffer = cmds[0];
    }

    private void CreateSemaphores()
    {
        SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };

        Result r1 = _vk.CreateSemaphore(_device, ref semInfo, null, out _imageAvailableSemaphore);
        Result r2 = _vk.CreateSemaphore(_device, ref semInfo, null, out _renderFinishedSemaphore);
        if (r1 != Result.Success || r2 != Result.Success)
            throw new InvalidOperationException($"vkCreateSemaphore 失败: {r1}/{r2}");
    }

    private void EnsureStagingBuffer(ulong requiredSize)
    {
        if (_stagingBuffer.Handle != 0 && _stagingBufferSize >= requiredSize) return;

        if (_stagingBuffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, _stagingBuffer, null);
            _vk.FreeMemory(_device, _stagingMemory, null);
            // R3-2: 重置为 default 防止双重释放——若后续 CreateBuffer/AllocateMemory 失败，
            // ReleaseSessionResources 会通过陈旧句柄再次释放已释放的内存
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

        Result result = _vk.CreateBuffer(_device, ref bufInfo, null, out _stagingBuffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateBuffer 失败: {result}");

        MemoryRequirements memReq;
        _vk.GetBufferMemoryRequirements(_device, _stagingBuffer, &memReq);

        uint memTypeIndex = FindMemoryType(
            memReq.MemoryTypeBits,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        MemoryAllocateInfo memInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memTypeIndex,
        };

        result = _vk.AllocateMemory(_device, &memInfo, null, out _stagingMemory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory 失败: {result}");

        // H3: 检查 BindBufferMemory 返回值
        result = _vk.BindBufferMemory(_device, _stagingBuffer, _stagingMemory, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindBufferMemory 失败: {result}");
        // A-H1: 必须记录 buffer 创建大小（requiredSize），不能记 memReq.Size（≥ requiredSize，含对齐填充）。
        // 否则帧尺寸中途变大时，第 688 行复用判断会误判「够用」——新 requiredSize ≤ 旧 memReq.Size
        // 但 > 旧 buffer 实际 Size，CmdCopyBufferToImage 读取超出 VkBuffer 对象范围（validation error / UB）。
        _stagingBufferSize = requiredSize;
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memProps;
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, &memProps);
        for (int i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << i)) != 0 &&
                (memProps.MemoryTypes[i].PropertyFlags & properties) == properties)
                return (uint)i;
        }
        throw new InvalidOperationException("未找到合适的 Vulkan 内存类型。");
    }

    /// <summary>
    /// 记录图像布局转换的 Pipeline Barrier。
    /// </summary>
    /// <param name="image">目标图像。</param>
    /// <param name="oldLayout">旧布局。</param>
    /// <param name="newLayout">新布局。</param>
    /// <param name="srcAccess">源访问掩码（前一阶段的写入操作）。</param>
    /// <param name="dstAccess">目标访问掩码（后续阶段的读取/写入操作）。</param>
    /// <param name="srcStageMask">源管线阶段（前一操作发生在哪个阶段）。</param>
    /// <param name="dstStageMask">目标管线阶段（后续操作发生在哪个阶段）。</param>
    private void TransitionImageLayout(
        Image image, ImageLayout oldLayout, ImageLayout newLayout,
        AccessFlags srcAccess, AccessFlags dstAccess,
        PipelineStageFlags srcStageMask, PipelineStageFlags dstStageMask)
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

        _vk.CmdPipelineBarrier(
            _commandBuffer,
            srcStageMask,
            dstStageMask,
            0, 0, null, 0, null, 1, &barrier);
    }

    private static void CopyStrided(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, int rowPitch)
    {
        int dstRowLen = width * 4;
        for (int y = 0; y < height; y++)
            src.Slice(y * rowPitch, dstRowLen).CopyTo(dst.Slice(y * dstRowLen, dstRowLen));
    }

    private static void SwapRbAndCopy(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, int rowPitch)
    {
        int dstRowLen = width * 4;
        // M11: 逐行用 uint 位运算整体交换 R/B 通道，消除逐字节 8 次边界检查（每行宽度必为 4 的倍数）。
        // 小端内存布局 [B,G,R,A] = B | (G<<8) | (R<<16) | (A<<24)；交换 R 与 B，保留 G/A。
        for (int y = 0; y < height; y++)
        {
            var srcRow = src.Slice(y * rowPitch, dstRowLen);
            var dstRow = dst.Slice(y * dstRowLen, dstRowLen);
            var srcU = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(srcRow);
            var dstU = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dstRow);
            for (int i = 0; i < srcU.Length; i++)
            {
                uint v = srcU[i];
                dstU[i] = (v & 0xFF00FF00u) | ((v & 0x00FF0000u) >> 16) | ((v & 0x000000FFu) << 16);
            }
        }
    }

    /// <summary>
    /// R2-1: 信号量恢复——当 AcquireNextImage 成功但后续命令记录/提交失败时，
    /// 提交一个最小命令缓冲来消费 <c>_imageAvailableSemaphore</c> 并呈现，
    /// 否则下次 AcquireNextImage 将因信号量已信号而永久超时（渲染器永久卡死）。
    /// </summary>
    /// <param name="imageIndex">已获取的 SwapChain 图像索引。</param>
    private void RecoverSemaphore(uint imageIndex)
    {
        try
        {
            // R4-1: 若 RecordAndSubmitFrame 在 BeginCommandBuffer 后、EndCommandBuffer 前抛异常，
            // 命令缓冲处于 recording 状态。Vulkan 规范规定 vkResetCommandBuffer 不能用于
            // recording 状态的命令缓冲——会返回 VK_NOT_READY，后续 BeginCommandBuffer 也失败，
            // 信号量永久泄漏。先调用 EndCommandBuffer（忽略错误）将其移出 recording 状态：
            // 成功→executable 状态；失败→invalid 状态。两种状态均可被 ResetCommandBuffer 重置。
            _vk.EndCommandBuffer(_commandBuffer);
            _vk.ResetCommandBuffer(_commandBuffer, CommandBufferResetFlags.None);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            if (_vk.BeginCommandBuffer(_commandBuffer, ref beginInfo) != Result.Success) return;

            // 将 SwapChain 图像转为 PresentSrc 布局（最小可呈现状态）
            // A-H4: srcStage=TransferBit 与本提交的信号量 waitDstStageMask（TransferBit）对齐形成依赖链
            Image swapchainImage = _swapchainImages[imageIndex];
            TransitionImageLayout(swapchainImage, ImageLayout.Undefined, ImageLayout.PresentSrcKhr,
                AccessFlags.None, AccessFlags.MemoryReadBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);

            if (_vk.EndCommandBuffer(_commandBuffer) != Result.Success) return;

            // 提交以消费 _imageAvailableSemaphore，信号 _renderFinishedSemaphore
            CommandBuffer cmd = _commandBuffer;
            Semaphore imgAvail = _imageAvailableSemaphore;
            Semaphore renderFin = _renderFinishedSemaphore;
            // R3-1: 同 RecordAndSubmitFrame——TransferBit
            PipelineStageFlags waitStage = PipelineStageFlags.TransferBit;

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imgAvail,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &renderFin,
            };

            _vk.QueueSubmit(_queue, 1, &submitInfo, default);
            _vk.QueueWaitIdle(_queue);

            // 呈现以消费 _renderFinishedSemaphore
            SwapchainKHR swap = _swapchain;
            uint idx = imageIndex;
            _presentInfoArr[0] = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFin,
                SwapchainCount = 1,
                PSwapchains = &swap,
                PImageIndices = &idx,
            };

            _khrSwapchain.QueuePresent(_queue, _presentInfoArr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "信号量恢复失败，渲染器可能需要重新 Attach");
        }
    }

    /// <summary>
    /// B-M2/B-M9: SwapChain 过期（ErrorOutOfDateKhr）时就地重建。
    /// 调用方须持有 _gate 锁。DeviceWaitIdle 后销毁旧 SwapChain 并按当前 Surface 尺寸重建；
    /// 两个信号量一并重建——同时消除 QueuePresent 失败后信号量可能的 signaled 残留（B-M9），
    /// 避免下次 AcquireNextImage 因信号量已信号而校验失败/永久超时。
    /// </summary>
    private void RecreateSwapchain()
    {
        // B-DEVLOST（复审补漏）：OutOfDate 重建路径的 DeviceWaitIdle 也可能返回 DeviceLost
        //（如休眠唤醒同时掉驱动）——类型化上抛，避免后续 CreateSwapchain 报泛化错误误导会话层。
        ThrowIfDeviceLost(_vk.DeviceWaitIdle(_device), "vkDeviceWaitIdle/RecreateSwapchain");

        if (_swapchain.Handle != 0)
        { _khrSwapchain.DestroySwapchain(_device, _swapchain, null); _swapchain = default; }
        _swapchainImages = [];

        if (_imageAvailableSemaphore.Handle != 0)
        { _vk.DestroySemaphore(_device, _imageAvailableSemaphore, null); _imageAvailableSemaphore = default; }
        if (_renderFinishedSemaphore.Handle != 0)
        { _vk.DestroySemaphore(_device, _renderFinishedSemaphore, null); _renderFinishedSemaphore = default; }

        try
        {
            CreateSwapchain(_targetWidth, _targetHeight);
            CreateSemaphores();
        }
        catch
        {
            // 维度4（多方位审计补漏）：半途失败（如 CreateSwapchain 成功但 CreateSemaphores OOM）
            // 会留下「_attached=true 但信号量为 VK_NULL_HANDLE」的不一致状态——下次 Present 以空信号量
            // 调用 vkAcquireNextImage 属规范违规（semaphore 与 fence 不得同时为 NULL，UB）。
            // 统一释放全部会话资源并标记未附加：后续 Present 抛清晰的「未附加」异常，会话层重新 Attach。
            ReleaseSessionResources();
            _attached = false;
            throw;
        }
        _logger.LogDebug("SwapChain 已因过期重建：{W}x{H}", _swapchainExtent.Width, _swapchainExtent.Height);
    }

    private void ReleaseSessionResources()
    {
        if (_device.Handle != 0)
            _vk.DeviceWaitIdle(_device);

        if (_imageAvailableSemaphore.Handle != 0)
        { _vk.DestroySemaphore(_device, _imageAvailableSemaphore, null); _imageAvailableSemaphore = default; }
        if (_renderFinishedSemaphore.Handle != 0)
        { _vk.DestroySemaphore(_device, _renderFinishedSemaphore, null); _renderFinishedSemaphore = default; }

        if (_stagingBuffer.Handle != 0)
        { _vk.DestroyBuffer(_device, _stagingBuffer, null); _stagingBuffer = default; }
        if (_stagingMemory.Handle != 0)
        { _vk.FreeMemory(_device, _stagingMemory, null); _stagingMemory = default; }
        _stagingBufferSize = 0;

        if (_swapchain.Handle != 0)
        { _khrSwapchain.DestroySwapchain(_device, _swapchain, null); _swapchain = default; }
        _swapchainImages = [];

        if (_surface.Handle != 0)
        { _khrSurface.DestroySurface(_instance, _surface, null); _surface = default; }

        if (_commandPool.Handle != 0)
        { _vk.DestroyCommandPool(_device, _commandPool, null); _commandPool = default; }
    }
}

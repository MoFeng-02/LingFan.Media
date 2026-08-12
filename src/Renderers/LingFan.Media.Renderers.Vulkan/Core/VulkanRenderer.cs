using System.Diagnostics;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// 渲染器诊断接口（本程序集 public，供探针跨程序集读取分相计时，不污染契约层 IVideoRenderer）。
/// </summary>
public interface IRendererProfiler
{
    /// <summary>返回 CPU 转换耗时与 GPU 同步耗时的分相统计摘要。</summary>
    string GetProfile();
}

/// <summary>
/// Vulkan 视频渲染器。将 <see cref="VideoFrame"/> 呈现到 Vulkan SwapChain。
/// </summary>
/// <remarks>
/// <para>跨平台 GPU 渲染器（Windows / Linux / Android；macOS/iOS 经 MoltenVK 覆盖——
/// 仅引入 MoltenVK 让 Vulkan 后端在 Apple 平台初始化/跑 SwapChain，无空域零拷贝上屏属第二类，待 Apple 合成栈落地）。
/// Surface 创建用 Vulkan 自己的 WSI 扩展（VK_KHR_*_surface / VK_EXT_metal_surface），不需要平台互操作文件。</para>
/// <para>WSI 扩展（Surface/Swapchain/Present）由 <c>VulkanNative</c> 零反射绑定在运行时经三阶段解析
/// （实例句柄解析实例级 / WSI 实例扩展，设备句柄解析设备级 / WSI 设备扩展），
/// 不依赖 Silk.NET 的 <c>Khr*</c> 扩展对象与加载层。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：contract，返回 <see cref="Task.CompletedTask"/></item>
/// <item><see cref="Attach"/>/<see cref="Detach"/>：native sync，UI 线程</item>
/// <item><see cref="Present"/>/<see cref="Clear"/>：native sync，渲染线程</item>
/// <item><see cref="DisposeAsync"/>：contract，委托 Dispose + CompletedTask，非伪异步</item>
/// </list>
/// <para><b>线程安全</b>：<c>_gate</c> 锁串行化所有公开方法。
/// <see cref="Present"/> 的 vkAcquireNextImage 以 <c>AcquireTimeoutNs</c>（2 秒）超时
/// 在 <c>_gate</c> 锁内阻塞——并发调用 <see cref="Dispose"/>/<see cref="Detach"/> 最坏被卡约 2 秒后才能获得锁。
/// 这是「有限超时替代无限等待」权衡的已知副作用，属预期行为而非死锁。</para>
/// <para><b>已知性能限制</b>：<see cref="RecordAndSubmitFrame"/> 中使用 <c>vkQueueWaitIdle</c>
/// 每帧同步 GPU——确保 Command Buffer 可安全复用但消除 GPU 并行。将改用 Fence 或环形 Command Buffer。</para>
/// <para><b>已知功能限制</b>：Linux X11/Wayland Surface 创建缺少 Display 指针——明确抛
/// <see cref="PlatformNotSupportedException"/>（扩展契约后支持）。
/// 软帧 YUV 平面格式（NV12/NV21/YUV420P/YUV422P/YUV444P）走 GPU Shader 路径：由 Fragment Shader
/// 采样 Y/U/V 平面并完成 YUV→RGB（与 D3D11 Shader 路径共用 BT.601 全范围矩阵），CPU 仅做原始平面搬运；
/// 缩放支持三种 <see cref="AspectRatioMode"/>（见 <see cref="ScaleMode"/>）。</para>
/// ErrorOutOfDateKhr 已由 <c>RecreateSwapchain</c> 就地重建（含信号量重建，消除 signaled 残留）；
/// 其余 QueuePresent 硬失败仍抛异常，由会话层重新 Attach 恢复（信号量在重 Attach 时重建，无 double-signal 风险）。</para>
/// <para>AOT 兼容：sealed unsafe 类，无反射，pattern matching。</para>
/// </remarks>
internal sealed unsafe partial class VulkanRenderer : IVideoRenderer, IRendererProfiler
{
    // ── 共享资源（工厂注入，不由本类释放）──
    private readonly Instance _instance;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly uint _queueFamilyIndex;
    private readonly ILogger<VulkanRenderer> _logger;

    // GPU Shader 管线（软帧 YUV 路径）
    private VulkanShaderPipeline? _shaderPipeline;

    // ── Session 级资源 ──
    private SurfaceKHR _surface;
    private SwapchainKHR _swapchain;
    private Image[] _swapchainImages = [];
    // _swapchainImageViews 已移除——仅用 CmdCopyBufferToImage（直接操作 VkImage），
    // 不需要 ImageView。实现 Shader 渲染时需重新添加。
    private Format _swapchainFormat;
    private Extent2D _swapchainExtent;
    // Attach 时记录目标尺寸，供 OutOfDate 重建 SwapChain 使用
    //（CreateSwapchain 优先取 Surface CurrentExtent，此值仅作 CurrentExtent 不可用时的回退）。
    private uint _targetWidth;
    private uint _targetHeight;
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private Semaphore _imageAvailableSemaphore;
    private Semaphore _renderFinishedSemaphore;

    // 预分配 PresentInfo 数组，避免每帧 GC 分配
    private readonly PresentInfoKHR[] _presentInfoArr = [new PresentInfoKHR()];

    // ── 暂存缓冲 ──
    private Buffer _stagingBuffer;
    private DeviceMemory _stagingMemory;
    private ulong _stagingBufferSize;
    // staging 为 HOST_COHERENT 内存（见 EnsureStagingBuffer），整段映射一次后长期复用，
    // 免去每帧 MapMemory/UnmapMemory 的仪式开销；写入经一致性语义自动对 GPU 可见。
    private void* _stagingMapped;

    // 缩放模式（契约层 AspectRatioMode）：软帧尺寸与 SwapChain 不一致时据此适配。
    // 默认 Uniform（信箱）——无畸变、留黑边，与 VideoView/Pipeline 默认一致。
    public AspectRatioMode ScaleMode { get; set; } = AspectRatioMode.Uniform;

    // ── 缩放 blit 用暂存图像（软帧 1:1 不匹配时经此中转）──
    private Image _stagingImage;
    private DeviceMemory _stagingImageMem;
    private uint _stagingImageW;
    private uint _stagingImageH;
    private Format _stagingImageFormat;

    // ── 诊断计时（A1 验证用：定位每帧 ~92ms 开销归属）──
    // 累加 CPU 转换耗时（UploadSoftwareFrame 全程）与 GPU 同步耗时（QueueWaitIdle 全程），
    // 收尾由探针读取打印，指导进一步优化方向（不改热路径逻辑）。
    private long _profConvertTicks;
    private long _profGpuTicks;
    private int _profFrames;
    public string GetProfile()
    {
        double convMs = _profFrames == 0 ? 0
            : (double)_profConvertTicks / _profFrames / TimeSpan.TicksPerMillisecond;
        double gpuMs = _profFrames == 0 ? 0
            : (double)_profGpuTicks / _profFrames / TimeSpan.TicksPerMillisecond;
        return $"帧数={_profFrames} 平均CPU转换={convMs:F2}ms 平均GPU同步(QueueWaitIdle)={gpuMs:F2}ms";
    }

    private bool _disposed;
    private bool _attached;
    private readonly object _gate = new();

    // AcquireNextImage 超时——2 秒（纳秒），避免窗口最小化时永久阻塞
    private const ulong AcquireTimeoutNs = 2_000_000_000;

    internal VulkanRenderer(
        Instance instance, PhysicalDevice physicalDevice,
        Device device, Queue queue, uint queueFamilyIndex,
        ILogger<VulkanRenderer> logger)
    {
        _instance = instance;
        _physicalDevice = physicalDevice;
        _device = device;
        _queue = queue;
        _queueFamilyIndex = queueFamilyIndex;
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
                CreateShaderPipeline();
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
            // 2 秒超时在 _gate 锁内阻塞，并发 Dispose/Detach 最坏等约 2 秒（预期行为，见类级注释）
            Span<uint> imageIndexSpan = stackalloc uint[1];
            Result result = VulkanNative.AcquireNextImageKHR(
                _device, _swapchain, AcquireTimeoutNs,
                _imageAvailableSemaphore, default, imageIndexSpan);
            if (result == Result.Timeout)
            {
                _logger.LogWarning("vkAcquireNextImage 超时，跳过本帧。");
                return;
            }
            if (result == Result.ErrorOutOfDateKhr)
            {
                // SwapChain 过期（窗口尺寸/显示模式变化）——就地重建，跳过本帧，
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
                // 信号量恢复——AcquireNextImage 已信号 _imageAvailableSemaphore，
                // 但 QueueSubmit 前异常导致信号量未消费。必须提交最小命令缓冲消费信号量，
                // 否则下次 AcquireNextImage 因信号量已信号而永久超时（渲染器永久卡死）。
                // B-DEVLOST: 设备丢失时跳过恢复（设备已死，提交必然失败且徒增日志噪音），直接上抛。
                RecoverSemaphore(imageIndex);
                throw;
            }

            // 3. 呈现（复用预分配数组）
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

            result = VulkanNative.QueuePresentKHR(_queue, _presentInfoArr);
            if (result == Result.ErrorOutOfDateKhr)
            {
                // Present 阶段过期——丢弃本帧并重建 SwapChain。
                // RecreateSwapchain 内部同时重建两个信号量，消除 QueuePresent
                // 失败后 _renderFinishedSemaphore 可能的 signaled 残留。
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
        // 检查 BeginCommandBuffer 返回值
        Result result = VulkanNative.BeginCommandBuffer(_commandBuffer, ref beginInfo);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBeginCommandBuffer 失败: {result}");

        bool swapchainIsBgra = _swapchainFormat == Format.B8G8R8A8Unorm;
        bool usedShaderPath = false;

        // YUV 软帧走 GPU Shader 路径：由 Fragment Shader 采样 Y/U/V 平面并完成 YUV→RGB 转换，
        // 彻底消除 CPU 端逐像素转换。该路径使用 RenderPass，不需要 Transfer 布局屏障。
        if (frame.Resource is SoftwareFrameResource swYuv && IsYuvFormat(swYuv.Format))
        {
            usedShaderPath = true;
            long tConv = Stopwatch.GetTimestamp();
            RenderYuvSoftwareFrame(swYuv, imageIndex, swapchainIsBgra);
            _profConvertTicks += Stopwatch.GetTimestamp() - tConv;
        }
        else
        {
            // Transfer 路径：BGRA/RGBA 软帧与 GPU 纹理零拷贝 Present。
            // Undefined→TransferDst 首屏障 srcStage 必须等于信号量 waitDstStageMask（TransferBit），
            // 才能与 _imageAvailableSemaphore 的等待形成依赖链。
            TransitionImageLayout(swapchainImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                AccessFlags.None, AccessFlags.TransferWriteBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);

            switch (frame.Resource)
            {
                case SoftwareFrameResource sw:
                    long tConv = Stopwatch.GetTimestamp();
                    UploadSoftwareFrame(sw, swapchainImage);
                    _profConvertTicks += Stopwatch.GetTimestamp() - tConv;
                    break;
                case VulkanImageResource vk:
                    BlitVulkanImageResource(vk, swapchainImage);
                    break;
                default:
                    throw new NotSupportedException(
                        $"不支持的帧资源类型：{frame.Resource?.GetType().Name ?? "null"}。");
            }

            // TransferDst→PresentSrc，srcStage=Transfer（前一阶段写入），dstStage=BottomOfPipe（presentation engine 读取）
            TransitionImageLayout(swapchainImage, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr,
                AccessFlags.TransferWriteBit, AccessFlags.MemoryReadBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);
        }

        // 检查 EndCommandBuffer 返回值
        result = VulkanNative.EndCommandBuffer(_commandBuffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkEndCommandBuffer 失败: {result}");

        // 提交
        CommandBuffer cmd = _commandBuffer;
        Semaphore imgAvail = _imageAvailableSemaphore;
        Semaphore renderFin = _renderFinishedSemaphore;
        // Shader 路径首阶段为 ColorAttachmentOutput（RenderPass 写 color attachment），
        // Transfer 路径首阶段为 Transfer（Copy/Blit/LayoutTransition）。waitStage 必须与首阶段一致。
        PipelineStageFlags waitStage = usedShaderPath
            ? PipelineStageFlags.ColorAttachmentOutputBit
            : PipelineStageFlags.TransferBit;

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

        result = VulkanNative.QueueSubmit(_queue, 1, &submitInfo, default);
        ThrowIfDeviceLost(result, "vkQueueSubmit"); // QueueSubmit 是 TDR 设备丢失最常见的浮现点
        if (result != Result.Success)
            throw new InvalidOperationException($"vkQueueSubmit 失败: {result}");

        // 已知性能限制——QueueWaitIdle 每帧同步 GPU 以确保 Command Buffer 可安全复用。
        // 将改用 vkCreateFence + vkWaitForFences 或环形 Command Buffer 消除此阻塞。
        // 「提交成功、GPU 执行中 TDR」的设备丢失恰从 WaitIdle 浮现——必须检测。
        // 其余失败码（OOM 等）保持既有忽略语义不变。
        long tGpu = Stopwatch.GetTimestamp();
        ThrowIfDeviceLost(VulkanNative.QueueWaitIdle(_queue), "vkQueueWaitIdle");
        _profGpuTicks += Stopwatch.GetTimestamp() - tGpu;
        _profFrames++;
    }

    private void UploadSoftwareFrame(SoftwareFrameResource sw, Image dstImage)
    {
        int width = sw.Width;
        int height = sw.Height;
        int rowBytes = width * 4;
        int dataSize = width * height * 4;

        if (IsYuvFormat(sw.Format))
            throw new NotSupportedException("YUV 软帧已迁移到 GPU Shader 路径，不应再走 CPU UploadSoftwareFrame。");

        // staging 已持久映射（HOST_COHERENT 内存，见 EnsureStagingBuffer），直接写 _stagingMapped，
        // 免去每帧 MapMemory/UnmapMemory 仪式开销。BGRA/RGBA 单平面路径把源数据拷入 staging 后上传。
        EnsureStagingBuffer((ulong)dataSize);
        if (_stagingMapped == null)
            throw new InvalidOperationException("staging 缓冲未映射（持久映射初始化失败）。");
        Span<byte> dst = new(_stagingMapped, dataSize);
        {
            bool swapchainIsBgra = _swapchainFormat == Format.B8G8R8A8Unorm;

            // 非 YUV（BGRA32/RGBA32 单平面）：直接拷入 staging，stride / R-B 互换在此处理
            var src = sw.Data.Span;
            int srcRowPitch = sw.Stride > 0 ? sw.Stride : rowBytes;
            if (srcRowPitch < rowBytes)
                throw new InvalidOperationException(
                    $"帧 Stride {srcRowPitch} 小于行字节数 {rowBytes}（{width}x{height}）。");
            long requiredSrcLen = (long)(height - 1) * srcRowPitch + rowBytes;
            if (src.Length < requiredSrcLen)
                throw new InvalidOperationException(
                    $"帧数据长度 {src.Length} 不足以填充 {width}x{height} 帧" +
                    $"（Stride={srcRowPitch}，需要 {requiredSrcLen} 字节）。");

            bool sameChannelOrder = sw.Format switch
            {
                PixelFormat.BGRA32 => swapchainIsBgra,
                PixelFormat.RGBA32 => !swapchainIsBgra,
                _ => throw new NotSupportedException($"Vulkan 渲染器不支持像素格式 {sw.Format}。"),
            };
            if (sameChannelOrder)
            {
                if (srcRowPitch == rowBytes)
                    src.Slice(0, dataSize).CopyTo(dst);
                else
                    CopyStrided(src, dst, width, height, srcRowPitch);
            }
            else
            {
                SwapRbAndCopy(src, dst, width, height, srcRowPitch);
            }
        }

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

        uint swW = _swapchainExtent.Width;
        uint swH = _swapchainExtent.Height;

        // 尺寸匹配 → 直拷快路径（零缩放，零回归）
        bool exactMatch = (uint)width == swW && (uint)height == swH;
        if (exactMatch)
        {
            VulkanNative.CmdCopyBufferToImage(_commandBuffer, _stagingBuffer, dstImage,
                ImageLayout.TransferDstOptimal, 1, &copyRegion);
            return;
        }

        // 尺寸不匹配 → 经 staging image + vkCmdBlitImage 缩放适配（与 BlitVulkanImageResource 同源手法）。
        // 软帧经上方分支已转成 BGRA32/RGBA32 单平面格式，BGRA8/RGBA8 均为
        // Vulkan 规范保证可 blit 的格式，无需着色器，AOT 安全。
        EnsureStagingImage((uint)width, (uint)height, _swapchainFormat);
        // staging buffer → staging image（TRANSFER_DST）
        TransitionImageLayout(_stagingImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            AccessFlags.None, AccessFlags.TransferWriteBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);
        VulkanNative.CmdCopyBufferToImage(_commandBuffer, _stagingBuffer, _stagingImage,
            ImageLayout.TransferDstOptimal, 1, &copyRegion);
        // staging image → TRANSFER_SRC 供 blit 源
        TransitionImageLayout(_stagingImage, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
            AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);

        ComputeBlitRects(width, height, (int)swW, (int)swH, ScaleMode,
            out int sX, out int sY, out int sW, out int sH,
            out int dX, out int dY, out int dW, out int dH, out bool clearBars);

        if (clearBars)
        {
            // 信箱模式：先清黑底，再 blit 居中 fit 矩形，四周留黑边
            ClearColorValue cc = new() { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 0 };
            ImageSubresourceRange range = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };
            VulkanNative.CmdClearColorImage(_commandBuffer, dstImage, ImageLayout.TransferDstOptimal, &cc, 1, &range);
        }

        ImageSubresourceLayers layers = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            MipLevel = 0,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        ImageBlit blit = new()
        {
            SrcSubresource = layers,
            DstSubresource = layers,
        };
        blit.SrcOffsets[0] = new Offset3D(sX, sY, 0);
        blit.SrcOffsets[1] = new Offset3D(sX + sW, sY + sH, 1);
        blit.DstOffsets[0] = new Offset3D(dX, dY, 0);
        blit.DstOffsets[1] = new Offset3D(dX + dW, dY + dH, 1);
        VulkanNative.CmdBlitImage(_commandBuffer, _stagingImage, ImageLayout.TransferSrcOptimal,
            dstImage, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
    }

    /// <summary>
    /// GPU Shader 路径：上传 YUV 平面并由 Fragment Shader 完成转换/缩放，替代 <see cref="UploadSoftwareFrame"/> 的 CPU 转换。
    /// </summary>
    private void RenderYuvSoftwareFrame(SoftwareFrameResource sw, uint imageIndex, bool swapchainIsBgra)
    {
        if (_shaderPipeline is null)
            throw new InvalidOperationException("Shader 管线未初始化。");

        int w = sw.Width, h = sw.Height;
        int stagingSize = CalculateYuvStagingSize(sw);
        EnsureStagingBuffer((ulong)stagingSize);

        ComputeBlitRects(w, h, (int)_swapchainExtent.Width, (int)_swapchainExtent.Height, ScaleMode,
            out int sX, out int sY, out int sW, out int sH,
            out int dX, out int dY, out int dW, out int dH, out _);

        float u0 = (w <= 0 || sW <= 0) ? 0f : (float)sX / w;
        float v0 = (h <= 0 || sH <= 0) ? 0f : (float)sY / h;
        float u1 = (w <= 0) ? 1f : (float)(sX + sW) / w;
        float v1 = (h <= 0) ? 1f : (float)(sY + sH) / h;

        _shaderPipeline.Present(
            sw, imageIndex,
            (dX, dY, dW, dH),
            (u0, v0, u1, v1),
            _stagingBuffer, _stagingMapped, _stagingBufferSize,
            _commandBuffer, swapchainIsBgra);
    }

    private static int CalculateYuvStagingSize(SoftwareFrameResource sw)
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

    private static bool IsYuvFormat(PixelFormat f) => f is
        PixelFormat.YUV420P or PixelFormat.YUV422P or PixelFormat.YUV444P or
        PixelFormat.NV12 or PixelFormat.NV21;

    /// <summary>
    /// 按 <see cref="ScaleMode"/> 计算软帧→SwapChain 的 blit 源/目标矩形。
    /// Fill=拉伸填满（不保比例）；Uniform=信箱（保比例居中留黑边，clearBars=true）；
    /// UniformToFill=高保真全屏（保比例裁剪溢出铺满）。源/目标矩形均为含下界的像素区间。
    /// </summary>
    private static void ComputeBlitRects(
        int srcW, int srcH, int dstW, int dstH, AspectRatioMode mode,
        out int sX, out int sY, out int sW, out int sH,
        out int dX, out int dY, out int dW, out int dH, out bool clearBars)
    {
        sX = 0; sY = 0; sW = srcW; sH = srcH;
        dX = 0; dY = 0; dW = dstW; dH = dstH;
        clearBars = false;

        if (srcW <= 0 || srcH <= 0) return;

        switch (mode)
        {
            case AspectRatioMode.Fill:
                // 拉伸填满目标区域（不保比例，可变畸）
                break;
            case AspectRatioMode.Uniform:
                // 信箱模式：保比例、居中、留黑边
                clearBars = true;
                double fit = Math.Min((double)dstW / srcW, (double)dstH / srcH);
                dW = Math.Max(1, (int)(srcW * fit + 0.5));
                dH = Math.Max(1, (int)(srcH * fit + 0.5));
                dX = (dstW - dW) / 2;
                dY = (dstH - dH) / 2;
                break;
            case AspectRatioMode.UniformToFill:
                // 高保真全屏：保比例、裁剪溢出、铺满（cover）
                double cover = Math.Max((double)dstW / srcW, (double)dstH / srcH);
                int cw = Math.Max(1, (int)(dstW / cover + 0.5));
                int ch = Math.Max(1, (int)(dstH / cover + 0.5));
                sX = (srcW - cw) / 2;
                sY = (srcH - ch) / 2;
                sW = cw;
                sH = ch;
                dW = dstW;
                dH = dstH;
                break;
        }
    }

    private void EnsureStagingImage(uint w, uint h, Format fmt)
    {
        if (_stagingImage.Handle != 0 && _stagingImageW == w && _stagingImageH == h && _stagingImageFormat == fmt)
            return;

        if (_stagingImage.Handle != 0)
        {
            VulkanNative.DestroyImage(_device, _stagingImage, null);
            VulkanNative.FreeMemory(_device, _stagingImageMem, null);
            _stagingImage = default;
            _stagingImageMem = default;
        }

        ImageCreateInfo imgInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = fmt,
            Extent = new Extent3D(w, h, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        Result result = VulkanNative.CreateImage(_device, ref imgInfo, null, out _stagingImage);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage(staging) 失败: {result}");

        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, _stagingImage, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        MemoryAllocateInfo memInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
        };
        result = VulkanNative.AllocateMemory(_device, &memInfo, null, out _stagingImageMem);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory(staging) 失败: {result}");
        result = VulkanNative.BindImageMemory(_device, _stagingImage, _stagingImageMem, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindImageMemory(staging) 失败: {result}");

        _stagingImageW = w;
        _stagingImageH = h;
        _stagingImageFormat = fmt;
    }

    /// <summary>
    /// VK-ZERO：Vulkan GPU 纹理零拷贝 Present 路径。
    /// 将 <see cref="VulkanImageResource"/> 的 <c>VkImage</c> blit/copy 到 SwapChain 图像。
    /// </summary>
    /// <remarks>
    /// <para>同尺寸且格式一致 → <c>vkCmdCopyImage</c>（零缩放，与软帧 CopyBufferToImage 语义一致）；
    /// 尺寸不同（缩放）或格式不同（R/B 顺序 / UNORM↔sRGB 转换）→ <c>vkCmdBlitImage</c>（Linear 过滤，
    /// 与 D3D11 双线性缩放语义一致）。多平面 / 24 位格式（NV12/NV21/YUV*/RGB24）Vulkan blit 不支持。</para>
    /// <para>异步策略：同步原生调用（无 I/O await），符合 Present 的 sync-only 原则。</para>
    /// <para>AOT 兼容：无反射、无新增 P/Invoke（复用 Vortice 源生成 <c>LibraryImport</c> 绑定）。</para>
    /// </remarks>
    internal void BlitVulkanImageResource(VulkanImageResource src, Image dstImage)
    {
        int srcW = src.Width;
        int srcH = src.Height;
        uint dstW = _swapchainExtent.Width;
        uint dstH = _swapchainExtent.Height;

        // 多平面 / 24 位等 Vulkan blit 不支持或需转码
        Format srcVkFormat = src.Format switch
        {
            PixelFormat.BGRA32 => Format.B8G8R8A8Unorm,
            PixelFormat.RGBA32 => Format.R8G8B8A8Unorm,
            PixelFormat.NV12 or PixelFormat.NV21 or PixelFormat.YUV420P
                or PixelFormat.YUV422P or PixelFormat.YUV444P or PixelFormat.RGB24
                => throw new NotSupportedException(
                    $"Vulkan GPU 纹理零拷贝暂不支持格式 {src.Format}（多平面/24 位需 Shader 转码）。"),
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
            VulkanNative.CmdCopyImage(_commandBuffer, src.Image, ImageLayout.TransferSrcOptimal,
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
            VulkanNative.CmdBlitImage(_commandBuffer, src.Image, ImageLayout.TransferSrcOptimal,
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

            // 有限超时
            Span<uint> imageIndexSpan = stackalloc uint[1];
            Result result = VulkanNative.AcquireNextImageKHR(
                _device, _swapchain, AcquireTimeoutNs,
                _imageAvailableSemaphore, default, imageIndexSpan);
            if (result == Result.Timeout)
            {
                _logger.LogWarning("vkAcquireNextImage 超时，跳过 Clear。");
                return;
            }
            if (result == Result.ErrorOutOfDateKhr)
            {
                // 同 Present——过期即重建，跳过本次 Clear
                RecreateSwapchain();
                return;
            }
            if (result == Result.ErrorDeviceLost)
            {
                // 同 Present——Clear 也须类型化上抛（与 D3D11 Clear 的 ThrowIfDeviceLost 对称），
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
                // 检查 BeginCommandBuffer 返回值
        result = VulkanNative.BeginCommandBuffer(_commandBuffer, ref beginInfo);
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkBeginCommandBuffer 失败: {result}");

                // Undefined→TransferDst，srcStage=TransferBit 与信号量 waitDstStageMask 对齐——同 RecordAndSubmitFrame
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

                VulkanNative.CmdClearColorImage(_commandBuffer, swapchainImage,
                    ImageLayout.TransferDstOptimal, &clearColor, 1, &range);

                // TransferDst→PresentSrc
                TransitionImageLayout(swapchainImage, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr,
                    AccessFlags.TransferWriteBit, AccessFlags.MemoryReadBit,
                    PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);

                // 检查 EndCommandBuffer 返回值
                result = VulkanNative.EndCommandBuffer(_commandBuffer);
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkEndCommandBuffer 失败: {result}");

                CommandBuffer cmd = _commandBuffer;
                Semaphore imgAvail = _imageAvailableSemaphore;
                Semaphore renderFin = _renderFinishedSemaphore;
                // TransferBit 而非 ColorAttachmentOutputBit——同 RecordAndSubmitFrame
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

                // 检查 QueueSubmit 返回值
                result = VulkanNative.QueueSubmit(_queue, 1, &submitInfo, default);
                // Clear 的 QueueSubmit 同样须类型化——否则设备丢失变泛化异常
                // 被下方 catch 过滤器捕获吞掉（对称于 Present 侧 RecordAndSubmitFrame）。
                ThrowIfDeviceLost(result, "vkQueueSubmit/Clear");
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkQueueSubmit 失败: {result}");

                // 已知性能限制——同 RecordAndSubmitFrame
                // 同 Present——GPU 执行中 TDR 从 WaitIdle 浮现
                ThrowIfDeviceLost(VulkanNative.QueueWaitIdle(_queue), "vkQueueWaitIdle/Clear");

                // 复用预分配数组
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

                // 检查 QueuePresent 返回值
                result = VulkanNative.QueuePresentKHR(_queue, _presentInfoArr);
                if (result == Result.ErrorOutOfDateKhr)
                {
                    // Clear 的 Present 阶段过期——重建（信号量一并重建，无残留）
                    RecreateSwapchain();
                }
                else if (result == Result.ErrorDeviceLost)
                {
                    // 同 Present——类型化上抛（catch 过滤器放行，见下）
                    _logger.LogError("Vulkan 设备丢失（vkQueuePresent/Clear）。");
                    throw new GpuDeviceLostException("Vulkan 设备已丢失（Clear 的 vkQueuePresent 返回 VK_ERROR_DEVICE_LOST）。需释放并重建渲染会话。");
                }
                else if (result != Result.Success && result != Result.SuboptimalKhr)
                    _logger.LogWarning("vkQueuePresent 失败: {Result}", result);
            }
            catch (Exception ex) when (ex is not GpuDeviceLostException)
            {
                // 信号量恢复——同 Present，Clear 异常后也需消费信号量。
                // GpuDeviceLostException 不在此吞掉——设备已死，信号量恢复无意义
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

    // 获取进程模块句柄供 Win32SurfaceCreateInfoKHR.Hinstance 使用。
    // LibraryImport P/Invoke，NativeAOT 兼容（源生成 marshaller，直接 P/Invoke，无运行时反射式封送）。
    [System.Runtime.InteropServices.LibraryImport("kernel32")]
    private static partial nint GetModuleHandleW(nint lpModuleName);

    private unsafe void CreateSurface(IntPtr handle)
    {
        SurfaceKHR[] surfArr = new SurfaceKHR[1];
        Result result;

        if (OperatingSystem.IsWindows())
        {
            // VUID-VkWin32SurfaceCreateInfoKHR-hinstance-01307 要求有效 HINSTANCE，
            // 不能默认 0 靠驱动宽容（validation layer 必报错）。GetModuleHandleW(null) = 进程模块句柄。
            var info = new Win32SurfaceCreateInfoKHR
            {
                SType = StructureType.Win32SurfaceCreateInfoKhr,
                Hinstance = GetModuleHandleW(0),
                Hwnd = handle,
            };
            result = VulkanNative.CreateWin32SurfaceKHR(_instance, ref info, null, out surfArr[0]);
        }
        else if (OperatingSystem.IsAndroid())
        {
            // handle 本身就是 ANativeWindow*（IRenderTarget 传来的原生窗口指针）。
            // 绝不能写 &handle——那是「指向栈局部变量的指针」，驱动会把栈地址当 ANativeWindow* 解引用（UB）。
            // 对照 Win32 路径 Hwnd = handle 的直接赋值语义：字段里装的必须是窗口指针值本身。
            var info = new AndroidSurfaceCreateInfoKHR
            {
                SType = StructureType.AndroidSurfaceCreateInfoKhr,
                Window = (nint*)handle,
            };
            result = VulkanNative.CreateAndroidSurfaceKHR(_instance, ref info, null, out surfArr[0]);
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
        {
            // MoltenVK 路径：handle 应为宿主提供的 CAMetalLayer*（由 Apple 合成栈 / VideoView 注入）。
            // VK_EXT_metal_surface 须已在实例启用（VulkanRendererFactory.GetPlatformExtensions 已加 Apple 分支）。
            // MetalSurfaceCreateInfoEXT.PLayer 是 IntPtr*（指向 CAMetalLayer*），故取局部副本的地址传入。
            IntPtr layer = handle;
            var info = new MetalSurfaceCreateInfoEXT
            {
                SType = StructureType.MetalSurfaceCreateInfoExt,
                PLayer = &layer,
            };
            result = VulkanNative.CreateMetalSurfaceEXT(_instance, ref info, null, out surfArr[0]);
        }
        else if (OperatingSystem.IsLinux())
        {
            // X11/Wayland Surface 创建需要 Display* 指针（Xlib 的 Dpy / Wayland 的 wl_display*），
            // 当前 IRenderTarget.NativeHandle 仅传递单个 IntPtr（窗口句柄），无法携带 Display 指针。
            // 旧「预留骨架」以缺失 Dpy/Display 的方式调用驱动，属于未定义行为（驱动解引用空 Display）。
            // 按平台范围决策 Linux 原生 Surface 不在范围——明确抛 PNS，快速失败优于 UB。
            // 若排期：需扩展 IRenderTarget 契约（复合句柄/ExtraFields）携带 Display* 后再实现。
            _logger.LogWarning(
                "Linux Vulkan Surface 创建被拒绝（Xlib/Wayland 原生 Surface 暂未集成）——缺少 Display* 传递通道。");
            throw new PlatformNotSupportedException(
                "Linux 原生 Vulkan Surface 需要 Display* 指针（X11 Dpy / wl_display*），" +
                "当前 IRenderTarget.NativeHandle 仅单一窗口句柄无法携带，扩展契约后才支持。");
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
        // 检查 GetPhysicalDeviceSurfaceCapabilities 返回值
        Result capsResult = VulkanNative.GetPhysicalDeviceSurfaceCapabilitiesKHR(_physicalDevice, _surface, capsArr);
        if (capsResult != Result.Success)
            throw new InvalidOperationException($"vkGetPhysicalDeviceSurfaceCapabilitiesKHR 失败: {capsResult}");
        ref SurfaceCapabilitiesKHR caps = ref capsArr[0];

        uint formatCount = 0;
        // 检查 GetPhysicalDeviceSurfaceFormats 返回值
        Result fmtResult = VulkanNative.GetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, ref formatCount, (SurfaceFormatKHR*)null);
        if (fmtResult != Result.Success)
            throw new InvalidOperationException($"vkGetPhysicalDeviceSurfaceFormatsKHR 失败: {fmtResult}");
        if (formatCount == 0)
            throw new InvalidOperationException("Surface 无可用格式。");

        var formats = new SurfaceFormatKHR[formatCount];
        // 检查第二次 GetPhysicalDeviceSurfaceFormats 返回值
        Result fmtResult2 = VulkanNative.GetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, ref formatCount, formats);
        if (fmtResult2 != Result.Success)
            throw new InvalidOperationException($"vkGetPhysicalDeviceSurfaceFormatsKHR (第二次) 失败: {fmtResult2}");

        // 优选 B8G8R8A8Unorm，回退 R8G8B8A8Unorm（部分 Android/移动驱动仅报 RGBA8）。
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
                "Vulkan 渲染器仅支持 8bit BGRA/RGBA SwapChain。");

        _swapchainFormat = selectedFormat.Format;
        // 钳制 Extent 到 Surface 能力范围——CurrentExtent.Width==uint.MaxValue 表示由 SwapChain 决定尺寸
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

        // MinImageCount 下限保护——至少双缓冲（某些驱动返回 0 或 1）
        // 上限保护——不超过 MaxImageCount（MaxImageCount=0 表示无限制）
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
            // 按 Surface 支持的 CompositeAlpha 位选择——Opaque 首选，否则 Inherit/PreMultiplied/PostMultiplied
            // （Wayland/Android 常见仅支持后者，硬写 Opaque 会导致 vkCreateSwapchain 失败）。
            // 补漏：末位不再硬选 PreMultiplied——仅 PostMultiplied 可用的驱动也能命中支持位
            //（规范保证 SupportedCompositeAlpha 至少置位一个，四选一必中）。
            CompositeAlpha = SelectCompositeAlpha(caps.SupportedCompositeAlpha),
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true,
            OldSwapchain = default,
        };

        SwapchainKHR swap;
        Result result = VulkanNative.CreateSwapchainKHR(_device, ref swapInfo, null, out swap);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateSwapchain 失败: {result}");
        _swapchain = swap;

        uint imageCount = 0;
        // 检查 GetSwapchainImages 返回值
        Result imgResult = VulkanNative.GetSwapchainImagesKHR(_device, _swapchain, ref imageCount, (Image*)null);
        if (imgResult != Result.Success)
            throw new InvalidOperationException($"vkGetSwapchainImagesKHR 失败: {imgResult}");
        _swapchainImages = new Image[imageCount];
        // 检查第二次 GetSwapchainImages 返回值
        Result imgResult2 = VulkanNative.GetSwapchainImagesKHR(_device, _swapchain, ref imageCount, _swapchainImages);
        if (imgResult2 != Result.Success)
            throw new InvalidOperationException($"vkGetSwapchainImagesKHR (第二次) 失败: {imgResult2}");
    }

    /// <summary>
    /// 统一的设备丢失检测——结果为 <see cref="Result.ErrorDeviceLost"/> 时
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

    // CompositeAlpha 四级回退——Opaque > Inherit > PreMultiplied > PostMultiplied，
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

        Result result = VulkanNative.CreateCommandPool(_device, ref poolInfo, null, out _commandPool);
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
        // 检查 AllocateCommandBuffers 返回值
        result = VulkanNative.AllocateCommandBuffers(_device, &allocInfo, cmds);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateCommandBuffers 失败: {result}");
        _commandBuffer = cmds[0];
    }

    private void CreateSemaphores()
    {
        SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };

        Result r1 = VulkanNative.CreateSemaphore(_device, ref semInfo, null, out _imageAvailableSemaphore);
        Result r2 = VulkanNative.CreateSemaphore(_device, ref semInfo, null, out _renderFinishedSemaphore);
        if (r1 != Result.Success || r2 != Result.Success)
            throw new InvalidOperationException($"vkCreateSemaphore 失败: {r1}/{r2}");
    }

    private void CreateShaderPipeline()
    {
        _shaderPipeline = new VulkanShaderPipeline(_physicalDevice, _device);
        _shaderPipeline.EnsureSwapchainResources(_swapchainFormat, _swapchainExtent, _swapchainImages);
    }

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
            // 重置为 default 防止双重释放——若后续 CreateBuffer/AllocateMemory 失败，
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

        Result result = VulkanNative.CreateBuffer(_device, ref bufInfo, null, out _stagingBuffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateBuffer 失败: {result}");

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

        result = VulkanNative.AllocateMemory(_device, &memInfo, null, out _stagingMemory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory 失败: {result}");

        // 检查 BindBufferMemory 返回值
        result = VulkanNative.BindBufferMemory(_device, _stagingBuffer, _stagingMemory, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindBufferMemory 失败: {result}");

        // 持久映射整段 staging（HOST_COHERENT 内存，写入经一致性语义自动对 GPU 可见）。
        // 只映射一次并长期复用，UploadSoftwareFrame 直接写 _stagingMapped，免去每帧 Map/Unmap 仪式开销。
        void* mapped = null;
        Result mapResult = VulkanNative.MapMemory(_device, _stagingMemory, 0, memReq.Size, 0, &mapped);
        if (mapResult != Result.Success)
            throw new InvalidOperationException($"vkMapMemory（staging 持久映射）失败: {mapResult}");
        _stagingMapped = mapped;

        // 必须记录 buffer 创建大小（requiredSize），不能记 memReq.Size（≥ requiredSize，含对齐填充）。
        // 否则帧尺寸中途变大时，第 688 行复用判断会误判「够用」——新 requiredSize ≤ 旧 memReq.Size
        // 但 > 旧 buffer 实际 Size，CmdCopyBufferToImage 读取超出 VkBuffer 对象范围（validation error / UB）。
        _stagingBufferSize = requiredSize;
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

        VulkanNative.CmdPipelineBarrier(
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
        // 逐行用 uint 位运算整体交换 R/B 通道，消除逐字节 8 次边界检查（每行宽度必为 4 的倍数）。
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
    /// 信号量恢复——当 AcquireNextImage 成功但后续命令记录/提交失败时，
    /// 提交一个最小命令缓冲来消费 <c>_imageAvailableSemaphore</c> 并呈现，
    /// 否则下次 AcquireNextImage 将因信号量已信号而永久超时（渲染器永久卡死）。
    /// </summary>
    /// <param name="imageIndex">已获取的 SwapChain 图像索引。</param>
    private void RecoverSemaphore(uint imageIndex)
    {
        try
        {
            // 若 RecordAndSubmitFrame 在 BeginCommandBuffer 后、EndCommandBuffer 前抛异常，
            // 命令缓冲处于 recording 状态。Vulkan 规范规定 vkResetCommandBuffer 不能用于
            // recording 状态的命令缓冲——会返回 VK_NOT_READY，后续 BeginCommandBuffer 也失败，
            // 信号量永久泄漏。先调用 EndCommandBuffer（忽略错误）将其移出 recording 状态：
            // 成功→executable 状态；失败→invalid 状态。两种状态均可被 ResetCommandBuffer 重置。
            VulkanNative.EndCommandBuffer(_commandBuffer);
            VulkanNative.ResetCommandBuffer(_commandBuffer, CommandBufferResetFlags.None);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            if (VulkanNative.BeginCommandBuffer(_commandBuffer, ref beginInfo) != Result.Success) return;

            // 将 SwapChain 图像转为 PresentSrc 布局（最小可呈现状态）
            // srcStage=TransferBit 与本提交的信号量 waitDstStageMask（TransferBit）对齐形成依赖链
            Image swapchainImage = _swapchainImages[imageIndex];
            TransitionImageLayout(swapchainImage, ImageLayout.Undefined, ImageLayout.PresentSrcKhr,
                AccessFlags.None, AccessFlags.MemoryReadBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);

            if (VulkanNative.EndCommandBuffer(_commandBuffer) != Result.Success) return;

            // 提交以消费 _imageAvailableSemaphore，信号 _renderFinishedSemaphore
            CommandBuffer cmd = _commandBuffer;
            Semaphore imgAvail = _imageAvailableSemaphore;
            Semaphore renderFin = _renderFinishedSemaphore;
            // 同 RecordAndSubmitFrame——TransferBit
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

            VulkanNative.QueueSubmit(_queue, 1, &submitInfo, default);
            VulkanNative.QueueWaitIdle(_queue);

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

            VulkanNative.QueuePresentKHR(_queue, _presentInfoArr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "信号量恢复失败，渲染器可能需要重新 Attach");
        }
    }

    /// <summary>
    /// SwapChain 过期（ErrorOutOfDateKhr）时就地重建。
    /// 调用方须持有 _gate 锁。DeviceWaitIdle 后销毁旧 SwapChain 并按当前 Surface 尺寸重建；
    /// 两个信号量一并重建——同时消除 QueuePresent 失败后信号量可能的 signaled 残留，
    /// 避免下次 AcquireNextImage 因信号量已信号而校验失败/永久超时。
    /// </summary>
    private void RecreateSwapchain()
    {
        // OutOfDate 重建路径的 DeviceWaitIdle 也可能返回 DeviceLost
        //（如休眠唤醒同时掉驱动）——类型化上抛，避免后续 CreateSwapchain 报泛化错误误导会话层。
        ThrowIfDeviceLost(VulkanNative.DeviceWaitIdle(_device), "vkDeviceWaitIdle/RecreateSwapchain");

        if (_swapchain.Handle != 0)
        { VulkanNative.DestroySwapchainKHR(_device, _swapchain, null); _swapchain = default; }
        _swapchainImages = [];

        if (_imageAvailableSemaphore.Handle != 0)
        { VulkanNative.DestroySemaphore(_device, _imageAvailableSemaphore, null); _imageAvailableSemaphore = default; }
        if (_renderFinishedSemaphore.Handle != 0)
        { VulkanNative.DestroySemaphore(_device, _renderFinishedSemaphore, null); _renderFinishedSemaphore = default; }

        try
        {
            CreateSwapchain(_targetWidth, _targetHeight);
            _shaderPipeline?.EnsureSwapchainResources(_swapchainFormat, _swapchainExtent, _swapchainImages);
            CreateSemaphores();
        }
        catch
        {
            // 多方位补漏：半途失败（如 CreateSwapchain 成功但 CreateSemaphores OOM）
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
            VulkanNative.DeviceWaitIdle(_device);

        _shaderPipeline?.Dispose();
        _shaderPipeline = null;

        if (_imageAvailableSemaphore.Handle != 0)
        { VulkanNative.DestroySemaphore(_device, _imageAvailableSemaphore, null); _imageAvailableSemaphore = default; }
        if (_renderFinishedSemaphore.Handle != 0)
        { VulkanNative.DestroySemaphore(_device, _renderFinishedSemaphore, null); _renderFinishedSemaphore = default; }

        if (_stagingBuffer.Handle != 0)
        {
            if (_stagingMapped != null)
            {
                VulkanNative.UnmapMemory(_device, _stagingMemory);
                _stagingMapped = null;
            }
            VulkanNative.DestroyBuffer(_device, _stagingBuffer, null);
            _stagingBuffer = default;
        }
        if (_stagingMemory.Handle != 0)
        { VulkanNative.FreeMemory(_device, _stagingMemory, null); _stagingMemory = default; }
        _stagingBufferSize = 0;

        if (_stagingImage.Handle != 0)
        { VulkanNative.DestroyImage(_device, _stagingImage, null); _stagingImage = default; }
        if (_stagingImageMem.Handle != 0)
        { VulkanNative.FreeMemory(_device, _stagingImageMem, null); _stagingImageMem = default; }
        _stagingImageW = 0;
        _stagingImageH = 0;
        _stagingImageFormat = default;

        if (_swapchain.Handle != 0)
        { VulkanNative.DestroySwapchainKHR(_device, _swapchain, null); _swapchain = default; }
        _swapchainImages = [];

        if (_surface.Handle != 0)
        { VulkanNative.DestroySurfaceKHR(_instance, _surface, null); _surface = default; }

        if (_commandPool.Handle != 0)
        { VulkanNative.DestroyCommandPool(_device, _commandPool, null); _commandPool = default; }
    }
}

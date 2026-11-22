using System.Diagnostics;
using LingFan.Media.GPUShare.Vulkan;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanRenderer 的 Frame 职责段（部分类，由 VulkanRenderer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Attach/Present/Clear/Detach/Dispose）在主文件；全部同步原生调用，_gate 串行化。
internal sealed unsafe partial class VulkanRenderer
{
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
        // 硬解帧的跨队列同步信号量（video 队列解码完成 → graphics 队列采样前等待）；软帧为空句柄。
        Semaphore decodeDoneSem = default;

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
                case VulkanVideoFrameResource vvfr:
                    // 硬解 DPB 的 NV12 VkImage → 经中性转换器（GPUShare.Vulkan）转 RGBA → 再 blit 到 SwapChain。
                    // 零拷贝、GPU 内 YUV→RGB，无 CPU 回读；转换器与下游 SwapChain 同格式以走零缩放 Copy 快路径。
                    if (_nv12Converter is null)
                        throw new InvalidOperationException("NV12→RGBA 转换器未初始化（Attach 失败）。");
                    decodeDoneSem = vvfr.DecodeDoneSemaphore; // 跨队列依赖：等待 video 队列解码写入完成
                    PixelFormat nv12TargetFmt = swapchainIsBgra ? PixelFormat.BGRA32 : PixelFormat.RGBA32;
                    _nv12Converter.Convert(_commandBuffer, vvfr.Image, vvfr.CurrentLayout, (uint)vvfr.SlotIndex,
                        (uint)vvfr.Width, (uint)vvfr.Height, _swapchainFormat, out var rgbaImage);
                    BlitVulkanImageResource(rgbaImage, nv12TargetFmt, ImageLayout.TransferSrcOptimal,
                        vvfr.Width, vvfr.Height, swapchainImage);
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
        Semaphore renderFin = _renderFinishedSemaphores[imageIndex];
        // Shader 路径首阶段为 ColorAttachmentOutput（RenderPass 写 color attachment），
        // Transfer 路径首阶段为 Transfer（Copy/Blit/LayoutTransition）。waitStage 必须与首阶段一致。
        PipelineStageFlags waitStage = usedShaderPath
            ? PipelineStageFlags.ColorAttachmentOutputBit
            : PipelineStageFlags.TransferBit;

        // 硬解帧：额外等待解码完成信号量（video 队列 → graphics 队列跨队列依赖），
        // waitDstStageMask = FragmentShaderBit（着色器采样为首个真正读取 DPB 的阶段）。
        if (decodeDoneSem.Handle != 0)
        {
            Semaphore[] waits = { imgAvail, decodeDoneSem };
            PipelineStageFlags[] waitStages = { waitStage, PipelineStageFlags.FragmentShaderBit };
            fixed (Semaphore* pWaits = waits)
            fixed (PipelineStageFlags* pStages = waitStages)
            {
                SubmitInfo submitInfo = new()
                {
                    SType = StructureType.SubmitInfo,
                    WaitSemaphoreCount = 2,
                    PWaitSemaphores = pWaits,
                    PWaitDstStageMask = pStages,
                    CommandBufferCount = 1,
                    PCommandBuffers = &cmd,
                    SignalSemaphoreCount = 1,
                    PSignalSemaphores = &renderFin,
                };
                result = VulkanNative.QueueSubmit(_queue, 1, &submitInfo, default);
            }
        }
        else
        {
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
        }
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
    /// Vulkan GPU 纹理零拷贝 Present 路径。
    /// 将 <see cref="VulkanImageResource"/> 的 <c>VkImage</c> blit/copy 到 SwapChain 图像。
    /// </summary>
    /// <remarks>
    /// <para>同尺寸且格式一致 → <c>vkCmdCopyImage</c>（零缩放，与软帧 CopyBufferToImage 语义一致）；
    /// 尺寸不同（缩放）或格式不同（R/B 顺序 / UNORM↔sRGB 转换）→ <c>vkCmdBlitImage</c>（Linear 过滤，
    /// 与 D3D11 双线性缩放语义一致）。多平面 / 24 位格式（NV12/NV21/YUV*/RGB24）Vulkan blit 不支持。</para>
    /// <para>异步策略：同步原生调用（无 I/O await），符合 Present 的 sync-only 原则。</para>
    /// <para>AOT 兼容：无反射、无新增 P/Invoke（复用 Vortice 源生成 <c>LibraryImport</c> 绑定）。</para>
    /// </remarks>
    /// <summary>
    /// Vulkan GPU 纹理零拷贝 Present 路径（VulkanImageResource 重载）。
    /// 将 <see cref="VulkanImageResource"/> 的 <c>VkImage</c> blit/copy 到 SwapChain 图像。
    /// </summary>
    internal void BlitVulkanImageResource(VulkanImageResource src, Image dstImage)
    {
        BlitVulkanImageResource(src.Image, src.Format, src.CurrentLayout,
            src.Width, src.Height, (uint)src.SubresourceIndex, dstImage);
    }

    /// <summary>
    /// Vulkan GPU 纹理零拷贝 Present 路径（原始 Image 重载）。
    /// 将任意 RGBA 单平面 <c>VkImage</c> blit/copy 到 SwapChain 图像。
    /// 供硬解 NV12→RGBA 转换结果（<see cref="VulkanNv12ToRgbaConverter"/> 产出，已处 TransferSrcOptimal）上屏复用。
    /// 多平面 / 24 位格式（NV12/NV21/YUV*/RGB24）Vulkan blit 不支持，须先经 Shader 转码为 RGBA。
    /// </summary>
    internal void BlitVulkanImageResource(
        Image srcImage, PixelFormat srcFormat, ImageLayout srcCurrentLayout,
        int srcW, int srcH, Image dstImage)
    {
        BlitVulkanImageResource(srcImage, srcFormat, srcCurrentLayout, srcW, srcH, 0, dstImage);
    }

    internal void BlitVulkanImageResource(
        Image srcImage, PixelFormat srcFormat, ImageLayout srcCurrentLayout,
        int srcW, int srcH, uint subresourceIndex, Image dstImage)
    {
        uint dstW = _swapchainExtent.Width;
        uint dstH = _swapchainExtent.Height;

        // 仅支持单平面 BGRA32/RGBA32（NV12 等多平面格式已由上游转换器转码为 RGBA）。
        Format srcVkFormat = srcFormat switch
        {
            PixelFormat.BGRA32 => Format.B8G8R8A8Unorm,
            PixelFormat.RGBA32 => Format.R8G8B8A8Unorm,
            _ => throw new NotSupportedException(
                $"Vulkan GPU 纹理零拷贝仅支持 BGRA32/RGBA32 单平面格式（收到 {srcFormat}）。"),
        };

        // 源图像：交付布局 → TransferSrcOptimal（生产者可能以其他布局交付）。
        // 转换结果已处 TransferSrcOptimal 时此屏障为本状态自转移（no-op 语义，安全）。
        TransitionImageLayout(srcImage, srcCurrentLayout, ImageLayout.TransferSrcOptimal,
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
                    BaseArrayLayer = subresourceIndex,
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
            VulkanNative.CmdCopyImage(_commandBuffer, srcImage, ImageLayout.TransferSrcOptimal,
                dstImage, ImageLayout.TransferDstOptimal, 1, &region);
        }
        else
        {
            // 尺寸不同（缩放）或格式不同（R/B 顺序 / UNORM↔sRGB 转换）→ Blit（Linear 过滤）
            // 修正：此前直接 SrcOffsets=整源 → DstOffsets=整目标（拉伸填满），
            // 未应用 ScaleMode（默认 Uniform 保比例）→ 竖屏视频在横屏窗口被拉变形。
            // 与软帧路径（UploadSoftwareFrame 的 ComputeBlitRects）对齐：按 ScaleMode 计算源/目标矩形，
            // Uniform 时先清黑底留信箱边（Fill=拉伸满、Uniform=居中保比例黑边、UniformToFill=cover 裁剪）。
            ComputeBlitRects(srcW, srcH, (int)dstW, (int)dstH, ScaleMode,
                out int sX, out int sY, out int sW, out int sH,
                out int dX, out int dY, out int dW, out int dH, out bool clearBars);

            if (clearBars)
            {
                // 信箱模式：先清黑底，再 blit 居中 fit 矩形，四周留黑边（与软帧路径同源手法）。
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

            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = subresourceIndex,
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
            blit.SrcOffsets[0] = new Offset3D(sX, sY, 0);
            blit.SrcOffsets[1] = new Offset3D(sX + sW, sY + sH, 1);
            blit.DstOffsets[0] = new Offset3D(dX, dY, 0);
            blit.DstOffsets[1] = new Offset3D(dX + dW, dY + dH, 1);
            VulkanNative.CmdBlitImage(_commandBuffer, srcImage, ImageLayout.TransferSrcOptimal,
                dstImage, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
        }
    }

    // Clear

    /// <summary>
    /// 有头呈现的「Present→上屏」延迟：swapchain 用 FIFO（严格 vsync），即一个刷新周期。
    /// </summary>
    private static readonly TimeSpan PresentationLatencyValue = TimeSpan.FromMilliseconds(16.67);

    /// <inheritdoc />
    /// <remarks>
    /// FIFO 呈现模式（<c>PresentModeKHR.FifoKhr</c>）下 Present 受 vsync 门控，队列满时阻塞，
    /// 因此「Present→上屏」= 一个刷新周期，按 60Hz 近似 16.67ms。返回零会让同步器永不给提前窗口，
    /// 全程系统性晚一帧。
    /// </remarks>
    public TimeSpan PresentationLatency => PresentationLatencyValue;

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
}

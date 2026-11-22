using System.Diagnostics;
using LingFan.Media.GPUShare.Vulkan;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanRenderer 的软帧上传职责段（部分类，按职责拆分，编译期等价）。
// BGRA/RGBA 软帧 staging 上传与 YUV 软帧 Shader 路径；帧记录提交与 GPU 纹理 blit 在 Frame 主段。
internal sealed unsafe partial class VulkanRenderer
{
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
}

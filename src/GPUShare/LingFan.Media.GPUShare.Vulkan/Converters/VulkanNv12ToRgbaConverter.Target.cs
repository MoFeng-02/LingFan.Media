using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;
using LingFan.Media.Abstractions;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNv12ToRgbaConverter 的 Target 职责段（部分类，由 VulkanNv12ToRgbaConverter.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Convert/RenderInto/Dispose）在主文件；GPU 转换零反射原生调用。
public sealed unsafe partial class VulkanNv12ToRgbaConverter
{
    /// <summary>
    /// 为 NV12 源图像创建 Y/UV 平面视图（采样用）。调用方在命令提交完成后须经 <see cref="DestroyPlaneViews"/> 销毁，
    /// 避免与内部 <c>_planeViews</c> 缓存（仅服务长生命周期 DPB）冲突——导入纹理为瞬态，句柄值可能被复用导致陈旧条目。
    /// </summary>
    public (ImageView Y, ImageView UV) CreatePlaneViews(Image nv12Source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (CreatePlaneView(nv12Source, ImageAspectFlags.Plane0BitKhr, Format.R8Unorm, 0),
                CreatePlaneView(nv12Source, ImageAspectFlags.Plane1BitKhr, Format.R8G8Unorm, 0));
    }

    /// <summary>销毁经 <see cref="CreatePlaneViews"/> 创建的平面视图（须于命令提交完成后调用）。</summary>
    public void DestroyPlaneViews((ImageView Y, ImageView UV) pv)
    {
        if (_device.Handle == 0) return;
        if (pv.Y.Handle != 0) VulkanNative.DestroyImageView(_device, pv.Y, null);
        if (pv.UV.Handle != 0) VulkanNative.DestroyImageView(_device, pv.UV, null);
    }

    private void EnsureRgbaTarget(uint width, uint height, Format format)
    {
        if (_rgbaImage.Handle != 0 && _rgbaW == width && _rgbaH == height && _rgbaFormat == format) return;

        ReleaseFramebuffer();
        ReleaseRgbaTarget();

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        if (VulkanNative.CreateImage(_device, &imageInfo, null, out _rgbaImage) != Result.Success)
            throw new InvalidOperationException("vkCreateImage（NV12 转换 RGBA 目标）失败。");

        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, _rgbaImage, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
        };
        if (VulkanNative.AllocateMemory(_device, &allocInfo, null, out _rgbaMemory) != Result.Success)
            throw new InvalidOperationException("vkAllocateMemory（NV12 转换 RGBA 目标）失败。");
        if (VulkanNative.BindImageMemory(_device, _rgbaImage, _rgbaMemory, 0) != Result.Success)
            throw new InvalidOperationException("vkBindImageMemory（NV12 转换 RGBA 目标）失败。");

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _rgbaImage,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        if (VulkanNative.CreateImageView(_device, &viewInfo, null, out _rgbaView) != Result.Success)
            throw new InvalidOperationException("vkCreateImageView（NV12 转换 RGBA 目标）失败。");

        _rgbaW = width; _rgbaH = height; _rgbaFormat = format;
    }

    private void EnsureFramebuffer()
    {
        if (_framebuffer.Handle != 0) return;
        if (_renderPass.Handle == 0 || _rgbaView.Handle == 0) return;
        var view = _rgbaView;
        FramebufferCreateInfo fbInfo = new()
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = _rgbaW,
            Height = _rgbaH,
            Layers = 1,
        };
        if (VulkanNative.CreateFramebuffer(_device, &fbInfo, null, out _framebuffer) != Result.Success)
            throw new InvalidOperationException("vkCreateFramebuffer（NV12 转换）失败。");
    }

    private void EnsurePlaneViews(Image nv12Source, uint baseArrayLayer)
    {
        var key = (nv12Source.Handle, baseArrayLayer);
        if (_planeViews.ContainsKey(key)) return;
        ImageView yView = CreatePlaneView(nv12Source, ImageAspectFlags.Plane0BitKhr, Format.R8Unorm, baseArrayLayer);
        ImageView uvView = CreatePlaneView(nv12Source, ImageAspectFlags.Plane1BitKhr, Format.R8G8Unorm, baseArrayLayer);
        _planeViews[key] = (yView, uvView);
    }

    private ImageView CreatePlaneView(Image img, ImageAspectFlags aspect, Format format, uint baseArrayLayer)
    {
        // per-plane 视图（R8/R8G8）须链 VkImageViewUsageCreateInfo、usage=Sampled（子集），
        // 避免继承图像的多 planar 用法触发 VUID-08333/08335；图像已带 MUTABLE_FORMAT_BIT（VUID-12397/01564）。
        ImageViewUsageCreateInfo viewUsage;
        viewUsage.SType = StructureType.ImageViewUsageCreateInfo;
        viewUsage.PNext = null;
        viewUsage.Usage = ImageUsageFlags.SampledBit;
        ImageViewCreateInfo vi = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = img,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = baseArrayLayer,
                LayerCount = 1,
            },
            PNext = &viewUsage,
        };
        ImageView view;
        if (VulkanNative.CreateImageView(_device, &vi, null, out view) != Result.Success)
            throw new InvalidOperationException("vkCreateImageView（NV12 平面视图）失败。");
        return view;
    }

    private void ReleaseFramebuffer()
    {
        if (_device.Handle == 0) return;
        if (_framebuffer.Handle != 0) { VulkanNative.DestroyFramebuffer(_device, _framebuffer, null); _framebuffer = default; }
    }

    private void ReleaseRgbaTarget()
    {
        if (_device.Handle == 0) return;
        if (_rgbaView.Handle != 0) { VulkanNative.DestroyImageView(_device, _rgbaView, null); _rgbaView = default; }
        if (_rgbaImage.Handle != 0) { VulkanNative.DestroyImage(_device, _rgbaImage, null); _rgbaImage = default; }
        if (_rgbaMemory.Handle != 0) { VulkanNative.FreeMemory(_device, _rgbaMemory, null); _rgbaMemory = default; }
        _rgbaW = 0; _rgbaH = 0; _rgbaFormat = (Format)0;
    }

    // ── YCbCr 整图采样路径（硬解 DPB 专用，无需 MUTABLE_FORMAT）──────────────────
    // 部分驱动视频格式支持集 flags=0（不含 MUTABLE），per-plane R8/R8G8 视图不可建；
    // 改经整图多平面视图 + YCbCr 采样器一次 texture() 取回转换后 RGB（ycbcr.frag）。
    // 复用 VulkanYcbcrToRgbaConverter 的转换/管线机制，本类仅持有其实例与 DPB 视图缓存。

    private VulkanYcbcrToRgbaConverter? _ycbcrConverter;
    private readonly Dictionary<(ulong ImageHandle, uint Layer), ImageView> _dpbMultiPlaneViews = new();
    private Framebuffer _ycbcrFramebuffer;
    private ImageView _ycbcrFramebufferView;
    private uint _ycbcrFbW, _ycbcrFbH;

    /// <summary>
    /// 把硬解 DPB 的 NV12 图像经 YCbCr 整图采样转 RGBA（内部缓存目标，转后 TransferSrcOptimal）。
    /// 与 <see cref="Convert(CommandBuffer, Image, ImageLayout, uint, uint, uint, Format, out Image)"/>
    /// 的区别：不建 per-plane 视图、不要求源图像带 MUTABLE_FORMAT。
    /// </summary>
    /// <param name="cmd">正在记录的命令缓冲。</param>
    /// <param name="nv12Source">NV12 DPB 图像。</param>
    /// <param name="srcLayout">源交付布局（VideoDecodeDpbKhr）。</param>
    /// <param name="baseArrayLayer">DPB 槽数组层。</param>
    /// <param name="width">宽度（像素）。</param>
    /// <param name="height">高度（像素）。</param>
    /// <param name="targetFormat">RGBA 目标格式（与 SwapChain 一致）。</param>
    /// <param name="rgbaTarget">转出的 RGBA VkImage（TransferSrcOptimal）。</param>
    public void ConvertViaYcbcr(CommandBuffer cmd, Image nv12Source, ImageLayout srcLayout, uint baseArrayLayer,
        uint width, uint height, Format targetFormat, out Image rgbaTarget)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ycbcrConverter ??= new VulkanYcbcrToRgbaConverter(_device, _physicalDevice, _logger);
        // BT.601 全范围（与 yuv.frag NV12 数学同口径；色彩元数据驱动的模型选择为后续增强）。
        _ycbcrConverter.EnsurePipelineForFormat(targetFormat, Format.G8B8R82Plane420Unorm,
            SamplerYcbcrModelConversion.Ycbcr601, SamplerYcbcrRange.Full);
        EnsureRgbaTarget(width, height, targetFormat);
        EnsureYcbcrFramebuffer(width, height);

        var key = (nv12Source.Handle, baseArrayLayer);
        if (!_dpbMultiPlaneViews.TryGetValue(key, out var view))
        {
            view = _ycbcrConverter.CreateMultiPlaneView(nv12Source, Format.G8B8R82Plane420Unorm, baseArrayLayer);
            _dpbMultiPlaneViews[key] = view;
        }

        _ycbcrConverter.ConvertMultiPlane(cmd, nv12Source, view, srcLayout, baseArrayLayer,
            width, height, _rgbaImage, _ycbcrFramebuffer);
        rgbaTarget = _rgbaImage;
    }

    private void EnsureYcbcrFramebuffer(uint width, uint height)
    {
        if (_ycbcrFramebuffer.Handle != 0 && _ycbcrFramebufferView.Handle == _rgbaView.Handle
            && _ycbcrFbW == width && _ycbcrFbH == height) return;
        if (_ycbcrFramebuffer.Handle != 0)
        {
            VulkanNative.DestroyFramebuffer(_device, _ycbcrFramebuffer, null);
            _ycbcrFramebuffer = default;
        }
        var view = _rgbaView;
        FramebufferCreateInfo fbInfo = new()
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _ycbcrConverter!.RenderPassForFramebuffer,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = width,
            Height = height,
            Layers = 1,
        };
        if (VulkanNative.CreateFramebuffer(_device, &fbInfo, null, out _ycbcrFramebuffer) != Result.Success)
            throw new InvalidOperationException("vkCreateFramebuffer（YCbCr DPB 转换）失败。");
        _ycbcrFramebufferView = _rgbaView;
        _ycbcrFbW = width; _ycbcrFbH = height;
    }
}

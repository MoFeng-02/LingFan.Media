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
}

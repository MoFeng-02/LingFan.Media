namespace LingFan.Media.Renderers.Vulkan;

// VulkanShaderPipeline 的平面图像资源段（部分类，由 Upload 段再拆，编译期等价）。
// 字段与核心编排（RenderFrameTo/Dispose）在主文件；全部同步原生调用，由 VulkanRenderer._gate 串行化。
internal sealed unsafe partial class VulkanShaderPipeline
{
    private void EnsurePlaneImages(int width, int height, PixelFormat format)
    {
        if (_cachedWidth == width && _cachedHeight == height && _cachedFormat == format) return;

        ReleasePlaneImages();

        switch (format)
        {
            case PixelFormat.BGRA32:
                CreatePlane(0, width, height, Format.B8G8R8A8Unorm);
                break;
            case PixelFormat.RGBA32:
                CreatePlane(0, width, height, Format.R8G8B8A8Unorm);
                break;
            case PixelFormat.YUV420P:
                CreatePlane(0, width, height, Format.R8Unorm);
                CreatePlane(1, (width + 1) >> 1, (height + 1) >> 1, Format.R8Unorm);
                CreatePlane(2, (width + 1) >> 1, (height + 1) >> 1, Format.R8Unorm);
                break;
            case PixelFormat.YUV422P:
                CreatePlane(0, width, height, Format.R8Unorm);
                CreatePlane(1, (width + 1) >> 1, height, Format.R8Unorm);
                CreatePlane(2, (width + 1) >> 1, height, Format.R8Unorm);
                break;
            case PixelFormat.YUV444P:
                CreatePlane(0, width, height, Format.R8Unorm);
                CreatePlane(1, width, height, Format.R8Unorm);
                CreatePlane(2, width, height, Format.R8Unorm);
                break;
            case PixelFormat.NV12:
            case PixelFormat.NV21:
                CreatePlane(0, width, height, Format.R8Unorm);
                CreatePlane(1, (width + 1) >> 1, (height + 1) >> 1, Format.R8G8Unorm);
                break;
            default:
                throw new NotSupportedException($"Vulkan Shader 管线不支持像素格式 {format}。");
        }

        _cachedWidth = width;
        _cachedHeight = height;
        _cachedFormat = format;
    }

    private void CreatePlane(int index, int width, int height, Format format)
    {
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        Result result = VulkanNative.CreateImage(_device, &imageInfo, null, out Image image);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage（平面 {index}）失败: {result}");

        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, image, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
        };
        result = VulkanNative.AllocateMemory(_device, &allocInfo, null, out DeviceMemory memory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory（平面 {index}）失败: {result}");

        result = VulkanNative.BindImageMemory(_device, image, memory, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindImageMemory（平面 {index}）失败: {result}");

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
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
        result = VulkanNative.CreateImageView(_device, &viewInfo, null, out ImageView view);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView（平面 {index}）失败: {result}");

        _planeImages[index] = image;
        _planeMemories[index] = memory;
        _planeViews[index] = view;
    }

    private void ReleasePlaneImages()
    {
        if (_device.Handle == 0) return;
        for (int i = 0; i < _planeImages.Length; i++)
        {
            if (_planeViews[i].Handle != 0) { VulkanNative.DestroyImageView(_device, _planeViews[i], null); _planeViews[i] = default; }
            if (_planeImages[i].Handle != 0) { VulkanNative.DestroyImage(_device, _planeImages[i], null); _planeImages[i] = default; }
            if (_planeMemories[i].Handle != 0) { VulkanNative.FreeMemory(_device, _planeMemories[i], null); _planeMemories[i] = default; }
        }
        _cachedWidth = 0;
        _cachedHeight = 0;
        _cachedFormat = (PixelFormat)(-1);
    }
}

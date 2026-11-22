using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanShaderPipeline 的 Swapchain 职责段（部分类，由 VulkanShaderPipeline.cs 按职责拆分，编译期等价）。
// 字段与核心编排（RenderFrameTo/Dispose）在主文件；全部同步原生调用，由 VulkanRenderer._gate 串行化。
internal sealed unsafe partial class VulkanShaderPipeline
{
    /// <summary>
    /// 当 SwapChain 创建/重建时调用，确保 RenderPass/Framebuffer/ImageView 与当前 SwapChain 匹配。
    /// </summary>
    public void EnsureSwapchainResources(Format format, Extent2D extent, Image[] images)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool formatChanged = _swapchainFormat != format || _renderPass.Handle == 0;
        bool imagesChanged = formatChanged
            || _swapchainImages.Length != images.Length
            || _swapchainExtent.Width != extent.Width
            || _swapchainExtent.Height != extent.Height;

        if (!formatChanged && !imagesChanged) return;

        ReleaseFramebuffersAndViews();

        if (formatChanged)
        {
            ReleasePipelineAndRenderPass();
            CreateRenderPassAndPipeline(format);
        }

        _swapchainFormat = format;
        _swapchainExtent = extent;
        _swapchainImages = images;
        CreateImageViewsAndFramebuffers();
    }

    /// <summary>
    /// 用 GPU Shader 路径呈现软件帧到 SwapChain 图像。
    /// </summary>
    /// <param name="sw">软件帧资源。</param>
    /// <param name="imageIndex">SwapChain 图像索引。</param>
    /// <param name="dstRect">目标矩形（相对于 SwapChain，单位像素）。</param>
    /// <param name="srcCrop">源裁剪 UV（相对源帧的 [u0,v0,u1,v1]）。</param>
    /// <param name="stagingBuffer">已持久映射的 staging 缓冲。</param>
    /// <param name="stagingMapped">staging 持久映射指针。</param>
    /// <param name="stagingBufferSize">staging 缓冲大小（字节）。</param>
    /// <param name="cmd">正在记录的 Command Buffer。</param>
    /// <param name="swapchainIsBgra">SwapChain 是否为 B8G8R8A8 格式（影响输出 R/B 通道顺序）。</param>
    public void Present(
        SoftwareFrameResource sw,
        uint imageIndex,
        (int X, int Y, int W, int H) dstRect,
        (float U0, float V0, float U1, float V1) srcCrop,
        Buffer stagingBuffer,
        void* stagingMapped,
        ulong stagingBufferSize,
        CommandBuffer cmd,
        bool swapchainIsBgra)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenderFrameTo(cmd, _renderPass, _pipeline, _framebuffers[imageIndex], _swapchainExtent,
            sw, dstRect, srcCrop, stagingBuffer, stagingMapped, stagingBufferSize);
    }

    private void CreateRenderPassAndPipeline(Format format)
    {
        EnsureShaderModules();
        EnsureDescriptorSetLayout();
        EnsureSampler();
        EnsureDescriptorPoolAndSet();

        // RenderPass：一个 color attachment，load=Clear 清黑底，store=Store，finalLayout=PresentSrc
        AttachmentDescription colorAttachment = new()
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };

        AttachmentReference colorRef = new()
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal,
        };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };

        SubpassDependency dependency = new()
        {
            SrcSubpass = uint.MaxValue,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
        };

        RenderPassCreateInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };

        Result result = VulkanNative.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateRenderPass 失败: {result}");

        // PipelineLayout 由 EnsurePipelineLayout() 幂等创建（SwapChain 与离屏路径共用），
        // 故此处仅调用，无重复局部变量声明。
        EnsurePipelineLayout();

        // GraphicsPipeline
        CreateGraphicsPipeline(format, _renderPass, out _pipeline);
    }

    private void CreateImageViewsAndFramebuffers()
    {
        int n = _swapchainImages.Length;
        _swapchainImageViews = new ImageView[n];
        _framebuffers = new Framebuffer[n];

        for (int i = 0; i < n; i++)
        {
            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchainFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            Result result = VulkanNative.CreateImageView(_device, &viewInfo, null, out _swapchainImageViews[i]);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateImageView（SwapChain）失败: {result}");

            var swapchainView = _swapchainImageViews[i];
            FramebufferCreateInfo fbInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &swapchainView,
                Width = _swapchainExtent.Width,
                Height = _swapchainExtent.Height,
                Layers = 1,
            };
            result = VulkanNative.CreateFramebuffer(_device, &fbInfo, null, out _framebuffers[i]);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateFramebuffer 失败: {result}");
        }
    }

    private void ReleaseFramebuffersAndViews()
    {
        if (_device.Handle == 0) return;
        foreach (var fb in _framebuffers)
        {
            if (fb.Handle != 0) VulkanNative.DestroyFramebuffer(_device, fb, null);
        }
        _framebuffers = [];
        foreach (var view in _swapchainImageViews)
        {
            if (view.Handle != 0) VulkanNative.DestroyImageView(_device, view, null);
        }
        _swapchainImageViews = [];
    }

    private void ReleasePipelineAndRenderPass()
    {
        if (_device.Handle == 0) return;
        if (_pipeline.Handle != 0) { VulkanNative.DestroyPipeline(_device, _pipeline, null); _pipeline = default; }
        if (_pipelineLayout.Handle != 0) { VulkanNative.DestroyPipelineLayout(_device, _pipelineLayout, null); _pipelineLayout = default; }
        if (_renderPass.Handle != 0) { VulkanNative.DestroyRenderPass(_device, _renderPass, null); _renderPass = default; }
    }


    // 帧平面上传
}

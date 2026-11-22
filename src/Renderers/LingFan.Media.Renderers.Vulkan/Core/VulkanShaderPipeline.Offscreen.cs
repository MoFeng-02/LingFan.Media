using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanShaderPipeline 的 Offscreen 职责段（部分类，由 VulkanShaderPipeline.cs 按职责拆分，编译期等价）。
// 字段与核心编排（RenderFrameTo/Dispose）在主文件；全部同步原生调用，由 VulkanRenderer._gate 串行化。
internal sealed unsafe partial class VulkanShaderPipeline
{
    /// <summary>
    /// 用 GPU Shader 路径把软件帧渲染进<b>离屏可导出 VkImage</b>（no-airspace 共享表面源专用）。
    /// 离屏图像固定尺寸 = 帧尺寸，由调用方在 <see cref="EnsureOffscreenResources"/> 中绑定；
    /// 缩放/信箱交给消费方合成层处理，故此处 dstRect 填满整张离屏图像、srcCrop 取全帧。
    /// </summary>
    /// <param name="sw">软件帧资源。</param>
    /// <param name="cmd">正在记录的 Command Buffer。</param>
    /// <param name="dstRect">目标矩形（离屏图像内，单位像素）。</param>
    /// <param name="srcCrop">源裁剪 UV（相对源帧的 [u0,v0,u1,v1]）。</param>
    public void PresentOffscreen(
        SoftwareFrameResource sw,
        CommandBuffer cmd,
        (int X, int Y, int W, int H) dstRect,
        (float U0, float V0, float U1, float V1) srcCrop)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_offscreenFramebuffer.Handle == 0)
            throw new InvalidOperationException("离屏渲染目标未初始化（请先调用 EnsureOffscreenResources）。");

        int stagingSize = CalculateStagingSize(sw);
        EnsureStagingBuffer((ulong)stagingSize);

        RenderFrameTo(cmd, _offscreenRenderPass, _offscreenPipeline, _offscreenFramebuffer, _offscreenExtent,
            sw, dstRect, srcCrop, _stagingBuffer, _stagingMapped, _stagingBufferSize);
    }

    /// <summary>
    /// 确保离屏渲染目标就绪：为给定的可导出 VkImage 的 ImageView 创建离屏 RenderPass/Pipeline/Framebuffer。
    /// 格式/尺寸变化时重建（离屏图像固定，仅在源重建共享图像时变化）。
    /// </summary>
    /// <param name="format">离屏图像格式（固定 B8G8R8A8Unorm，与消费方导入格式一致）。</param>
    /// <param name="extent">离屏图像尺寸（=帧尺寸）。</param>
    /// <param name="imageView">离屏可导出 VkImage 的 ImageView（由源创建并持有）。</param>
    public void EnsureOffscreenResources(Format format, Extent2D extent, ImageView imageView)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool formatChanged = _offscreenFormat != format || _offscreenRenderPass.Handle == 0;
        bool extentChanged = formatChanged
            || _offscreenExtent.Width != extent.Width
            || _offscreenExtent.Height != extent.Height;

        if (!formatChanged && !extentChanged && _offscreenImageView.Handle == imageView.Handle) return;

        ReleaseOffscreenFramebuffer();

        if (formatChanged)
        {
            ReleaseOffscreenPipelineAndRenderPass();
            CreateOffscreenRenderPassAndPipeline(format);
        }

        _offscreenFormat = format;
        _offscreenExtent = extent;
        _offscreenImageView = imageView;
        CreateOffscreenFramebuffer();
    }

    /// <summary>
    /// 创建离屏 RenderPass/Pipeline：与 SwapChain 同构，仅 attachment 的 finalLayout 为
    /// <see cref="ImageLayout.ColorAttachmentOptimal"/>（渲染器侧写完后交还，消费方导入后自管布局）。
    /// 复用已创建的着色器模块/描述符布局/采样器/描述符集（Ensure* 幂等）。
    /// </summary>
    private void CreateOffscreenRenderPassAndPipeline(Format format)
    {
        EnsureShaderModules();
        EnsureDescriptorSetLayout();
        EnsureSampler();
        EnsureDescriptorPoolAndSet();

        AttachmentDescription colorAttachment = new()
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
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

        Result result = VulkanNative.CreateRenderPass(_device, &renderPassInfo, null, out _offscreenRenderPass);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateRenderPass（离屏）失败: {result}");

        EnsurePipelineLayout();

        CreateGraphicsPipeline(format, _offscreenRenderPass, out _offscreenPipeline);
    }

    /// <summary>为绑定到 <see cref="_offscreenImageView"/> 的可导出离屏 VkImage 创建离屏 Framebuffer。</summary>
    private void CreateOffscreenFramebuffer()
    {
        var view = _offscreenImageView;
        FramebufferCreateInfo fbInfo = new()
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _offscreenRenderPass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = _offscreenExtent.Width,
            Height = _offscreenExtent.Height,
            Layers = 1,
        };
        Result result = VulkanNative.CreateFramebuffer(_device, &fbInfo, null, out _offscreenFramebuffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateFramebuffer（离屏）失败: {result}");
    }

    /// <summary>释放离屏 Framebuffer。注意：<see cref="_offscreenImageView"/> 由共享表面源持有，此处不销毁。</summary>
    private void ReleaseOffscreenFramebuffer()
    {
        if (_device.Handle == 0) return;
        if (_offscreenFramebuffer.Handle != 0)
        {
            VulkanNative.DestroyFramebuffer(_device, _offscreenFramebuffer, null);
            _offscreenFramebuffer = default;
        }
    }

    /// <summary>释放离屏 RenderPass/Pipeline。共享的 <see cref="_pipelineLayout"/> 由 SwapChain 路径的
    /// <see cref="ReleasePipelineAndRenderPass"/> 统一销毁，此处不触碰。</summary>
    private void ReleaseOffscreenPipelineAndRenderPass()
    {
        if (_device.Handle == 0) return;
        if (_offscreenPipeline.Handle != 0)
        {
            VulkanNative.DestroyPipeline(_device, _offscreenPipeline, null);
            _offscreenPipeline = default;
        }
        if (_offscreenRenderPass.Handle != 0)
        {
            VulkanNative.DestroyRenderPass(_device, _offscreenRenderPass, null);
            _offscreenRenderPass = default;
        }
    }
}

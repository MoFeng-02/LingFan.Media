using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;
using LingFan.Media.Abstractions;

namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// 中性「外部格式 YCbCr → RGBA」转换器（GPUShare.Vulkan，零反射 AOT 友好）。
/// 把 Android AHardwareBuffer 经 <c>VK_ANDROID_external_memory_android_hardware_buffer</c> 导入的
/// 外部格式（UNDEFINED + externalFormat）VkImage，用带 <c>VkSamplerYcbcrConversion</c> 的采样器
/// 在片元着色器内单次采样完成 YUV→RGB（转换由采样器在采样时执行，零 CPU 回读），
/// 输出 RGBA 目标图像供任意渲染器零拷贝 blit 上屏。
/// </summary>
/// <remarks>
/// <para><b>零拷贝语义</b>：AHB 图像仅能以 SAMPLED 用法创建（外部格式规范限制，无 TRANSFER 用法），
/// 故本转换器走「采样渲染」路径而非 blit：片元着色器经绑定 YCbCr 转换的采样器一次 <c>texture()</c>
/// 取回已转换 RGB（「1 GPU hop」，无 CPU 拷贝），写入调用方提供的 RGBA 目标（同 NV12 转换器交付约定：
/// 目标置于 <see cref="ImageLayout.TransferSrcOptimal"/>）。</para>
/// <para><b>规范要求</b>：同一 <c>VkSamplerYcbcrConversion</c> 须同时挂到采样器
/// （<c>SamplerCreateInfo.pNext=SamplerYcbcrConversionInfo</c>）与图像视图
/// （<c>ImageViewCreateInfo.pNext=SamplerYcbcrConversionInfo</c>）；描述符布局绑定为
/// <b>immutable sampler</b>（Y′CBCR 转换须在管线创建期固定）；转换参数（model/range/offset/components）
/// 逐字段取自 <c>vkGetAndroidHardwareBufferPropertiesANDROID</c> 返回的建议值，不自行猜测。</para>
/// <para><b>生命周期</b>：转换对象/采样器/描述符布局/管线按 externalFormat 缓存（同一视频流恒定）；
/// 视图为瞬态（每帧经 <see cref="CreateImageView"/> 创建、命令提交完成后 <see cref="DestroyImageView"/> 销毁）。</para>
/// <para><b>异步策略</b>：全部同步原生调用（无 I/O await），与 Present 的 sync-only 分类一致。</para>
/// <para><b>AOT</b>：仅依赖 Silk.NET 纯数据定义与已编译 SPIR-V（Shaders.g.cs，运行期零反射）；无 [DllImport]/反射。</para>
/// </remarks>
public sealed unsafe partial class VulkanYcbcrToRgbaConverter : IDisposable
{
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly ILogger? _logger;

    // 着色器模块 / 描述符 / 管线（按 externalFormat 缓存）
    private ShaderModule _vertModule;
    private ShaderModule _fragModule;
    private SamplerYcbcrConversion _ycbcrConversion;
    private Sampler _sampler;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private RenderPass _renderPass;
    private Format _pipelineTargetFormat = (Format)0;
    private ulong _pipelineExternalFormat;      // 当前管线对应的 externalFormat（0=未建）

    // 帧缓冲缓存：须<b>存活至引用它的命令缓冲执行完毕</b>。记录后立即销毁会让命令缓冲悬挂引用
    // 已释放对象，Adreno 在 vkEndCommandBuffer 收拢时解引用它 → 进程级原生崩溃（空指针解引用）。
    // 按（目标视图, 尺寸）惰性复用；管线/RenderPass 重建或 Dispose 时释放。
    private Framebuffer _framebuffer;
    private ImageView _framebufferView;
    private uint _framebufferW, _framebufferH;

    private bool _disposed;

    public VulkanYcbcrToRgbaConverter(Device device, PhysicalDevice physicalDevice, ILogger? logger = null)
    {
        _device = device;
        _physicalDevice = physicalDevice;
        _logger = logger;
    }

    /// <summary>
    /// 为 AHB 外部格式图像创建采样视图（format=UNDEFINED + YCbCr 转换 pNext，aspect=Color）。
    /// 调用方在命令提交完成后须经 <see cref="DestroyImageView"/> 销毁（视图为瞬态，不缓存）。
    /// </summary>
    public ImageView CreateImageView(Image ahbImage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ycbcrConversion.Handle == 0)
            throw new InvalidOperationException("YCbCr 转换尚未创建（须先调用 EnsurePipeline）。");

        // 规范：外部格式图像的视图 format 须为 UNDEFINED，且挂与采样器相同的 YCbCr 转换。
        var ycbcrInfo = new SamplerYcbcrConversionInfo
        {
            SType = StructureType.SamplerYcbcrConversionInfo,
            Conversion = _ycbcrConversion,
        };
        ImageViewCreateInfo vi = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = ahbImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.Undefined,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            PNext = &ycbcrInfo,
        };
        ImageView view;
        if (VulkanNative.CreateImageView(_device, &vi, null, out view) != Result.Success)
            throw new InvalidOperationException("vkCreateImageView（AHB 外部格式采样视图）失败。");
        return view;
    }

    /// <summary>销毁经 <see cref="CreateImageView"/> 创建的采样视图（须于命令提交完成后调用）。</summary>
    public void DestroyImageView(ImageView view)
    {
        if (_device.Handle == 0 || view.Handle == 0) return;
        VulkanNative.DestroyImageView(_device, view, null);
    }

    /// <summary>
    /// 把 AHB 外部格式源图像经 YCbCr 采样转换到<b>调用方提供的</b> RGBA 目标图像
    /// （同命令缓冲内记录；目标置于 <see cref="ImageLayout.TransferSrcOptimal"/>）。
    /// 供 <c>VulkanGpuFrameProducer</c> 导入 AHB 场景：RGBA 目标由调用方创建并拥有
    /// （交付 <c>VulkanImageResource</c>），本转换器不缓存、不持有该目标。
    /// </summary>
    /// <param name="cmd">正在记录的命令缓冲（调用方持有锁）。</param>
    /// <param name="ahbSource">AHB 导入的 VkImage（format=UNDEFINED + externalFormat）。</param>
    /// <param name="ahbView">经 <see cref="CreateImageView"/> 创建的采样视图。</param>
    /// <param name="width">宽度（像素）。</param>
    /// <param name="height">高度（像素）。</param>
    /// <param name="targetFormat">目标 RGBA 格式（须与下游 SwapChain 一致）。</param>
    /// <param name="rgbaTarget">调用方提供的 RGBA VkImage（转后处于 TransferSrcOptimal）。</param>
    /// <param name="rgbaView">调用方提供的 RGBA 图像视图（绑定临时帧缓冲）。</param>
    public void Convert(CommandBuffer cmd, Image ahbSource, ImageView ahbView, uint width, uint height, Format targetFormat, Image rgbaTarget, ImageView rgbaView)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline.Handle == 0)
            throw new InvalidOperationException("管线尚未创建（须先调用 EnsurePipeline）。");

        // 帧缓冲须与命令缓冲同生命周期：缓存复用（目标视图/尺寸变化时重建），
        // 绝不在记录后立即销毁（悬挂引用会导致命令缓冲解引用已释放对象，见字段注释）。
        if (_framebuffer.Handle == 0 || _framebufferView.Handle != rgbaView.Handle
            || _framebufferW != width || _framebufferH != height)
        {
            if (_framebuffer.Handle != 0)
                VulkanNative.DestroyFramebuffer(_device, _framebuffer, null);
            var attachView = rgbaView;
            FramebufferCreateInfo fbInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &attachView,
                Width = width,
                Height = height,
                Layers = 1,
            };
            if (VulkanNative.CreateFramebuffer(_device, &fbInfo, null, out _framebuffer) != Result.Success)
                throw new InvalidOperationException("vkCreateFramebuffer（YCbCr 转换·外部 RGBA 目标）失败。");
            _framebufferView = rgbaView;
            _framebufferW = width;
            _framebufferH = height;
        }

        RenderInto(cmd, ahbSource, ahbView, width, height, rgbaTarget, _framebuffer);
    }

    /// <summary>核心绘制：AHB 源 → ShaderReadOnlyOptimal（采样），渲染进 RGBA 目标并置于 TransferSrcOptimal。</summary>
    private void RenderInto(CommandBuffer cmd, Image ahbSource, ImageView ahbView, uint width, uint height, Image rgbaImage, Framebuffer framebuffer)
    {
        // AHB 源 → ShaderReadOnlyOptimal：导入内存由解码器（Vulkan 之外）写入，
        // 用「全屏障」等价覆盖外部写入（src=AllCommands+MemoryWrite），与硬解 DPB 路径同源手法。
        TransitionImageLayout(cmd, ahbSource, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.MemoryWriteBit, AccessFlags.ShaderReadBit,
            PipelineStageFlags.AllCommandsBit, PipelineStageFlags.FragmentShaderBit,
            ImageAspectFlags.ColorBit);

        // RGBA 目标 → ColorAttachmentOptimal（render pass loadOp=Clear）
        TransitionImageLayout(cmd, rgbaImage, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
            AccessFlags.None, AccessFlags.ColorAttachmentWriteBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ColorAttachmentOutputBit,
            ImageAspectFlags.ColorBit);

        UpdateDescriptorSet(ahbView);

        var clear = new ClearColorValue { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 0 };
        var clearValue = new ClearValue { Color = clear };
        var rpBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D { Offset = new Offset2D(0, 0), Extent = new Extent2D(width, height) },
            ClearValueCount = 1,
            PClearValues = &clearValue,
        };
        VulkanNative.CmdBeginRenderPass(cmd, &rpBegin, SubpassContents.Inline);
        VulkanNative.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);

        Viewport viewport = new() { X = 0, Y = 0, Width = width, Height = height, MinDepth = 0f, MaxDepth = 1f };
        VulkanNative.CmdSetViewport(cmd, 0, 1, &viewport);
        Rect2D scissor = new() { Offset = new Offset2D(0, 0), Extent = new Extent2D(width, height) };
        VulkanNative.CmdSetScissor(cmd, 0, 1, &scissor);

        var descriptorSet = _descriptorSet;
        VulkanNative.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &descriptorSet, 0, null);
        VulkanNative.CmdDraw(cmd, 3, 1, 0, 0);
        VulkanNative.CmdEndRenderPass(cmd);

        // RGBA 目标 → TransferSrcOptimal（供调用方 blit 到 SwapChain）
        TransitionImageLayout(cmd, rgbaImage, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
            ImageAspectFlags.ColorBit);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device.Handle == 0) return;

        ReleaseAll();
        if (_vertModule.Handle != 0) { VulkanNative.DestroyShaderModule(_device, _vertModule, null); _vertModule = default; }
        if (_fragModule.Handle != 0) { VulkanNative.DestroyShaderModule(_device, _fragModule, null); _fragModule = default; }
    }

    private void TransitionImageLayout(CommandBuffer cmd, Image image, ImageLayout oldLayout, ImageLayout newLayout,
        AccessFlags srcAccess, AccessFlags dstAccess, PipelineStageFlags srcStage, PipelineStageFlags dstStage, ImageAspectFlags aspect)
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = aspect,
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
        VulkanNative.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    /// <summary>externalFormat 变化时的全量重建：按「管线 → 布局 → 描述符 → 采样器/转换」依赖逆序释放。</summary>
    private void ReleaseAll()
    {
        if (_device.Handle == 0) return;
        ReleasePipelineAndRenderPass();
        if (_pipelineLayout.Handle != 0) { VulkanNative.DestroyPipelineLayout(_device, _pipelineLayout, null); _pipelineLayout = default; }
        if (_descriptorPool.Handle != 0) { VulkanNative.DestroyDescriptorPool(_device, _descriptorPool, null); _descriptorPool = default; }
        if (_descriptorSetLayout.Handle != 0) { VulkanNative.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null); _descriptorSetLayout = default; }
        ReleaseConversionAndSampler();
        _pipelineTargetFormat = (Format)0;
    }
}

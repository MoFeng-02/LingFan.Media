using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// 中性「普通 RGBA → RGBA」转换器（GPUShare.Vulkan，零反射 AOT 友好）。
/// 把经 <c>VK_ANDROID_external_memory_android_hardware_buffer</c> 导入的
/// <b>非外部格式</b> RGBA AHardwareBuffer（如 ImageReader 以 HardwareBufferFormat.Rgba8888 产出的 AHB）
/// 用普通（无 <c>VkSamplerYcbcrConversion</c>）采样器采样，片元着色器一次 <c>texture()</c> 直出 RGBA。
/// </summary>
/// <remarks>
/// <para><b>存在意义</b>：Adreno 驱动对「外部格式（YUV）AHB + YCbCr 转换采样」的原生绘制会空指针解引用
/// （进程级原生崩溃），而 Mali 上能过——与 OPAQUE_FD 导出的厂商分歧同一性质。绕开之法是不让
/// 解码侧产出 YUV AHB，而是令其产出 <b>RGBA AHB</b>，本转换器按普通 RGBA 纹理采样上屏，彻底避开崩溃。</para>
/// <para><b>与 VulkanYcbcrToRgbaConverter 的区别</b>：无 externalFormat、无 <c>VkSamplerYcbcrConversion</c>、
/// 描述符用<b>可变</b>采样器（写入 <c>DescriptorImageInfo.Sampler</c>），规避部分驱动对 immutable sampler
/// 仍读空 Sampler 字段的缺陷。输出 RGBA 目标图像供调用方零拷贝 blit 上屏。</para>
/// <para><b>异步策略</b>：全部同步原生调用（无 I/O await），与 Present 的 sync-only 分类一致。</para>
/// <para><b>AOT</b>：仅依赖 Silk.NET 纯数据定义与已编译 SPIR-V（Shaders.g.cs，运行期零反射）；无 [DllImport]/反射。</para>
/// </remarks>
public sealed unsafe partial class VulkanRgbaToRgbaConverter : IDisposable
{
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly ILogger? _logger;

    private ShaderModule _vertModule;
    private ShaderModule _fragModule;
    private Sampler _sampler;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private RenderPass _renderPass;
    private Format _pipelineTargetFormat = (Format)0;

    // 帧缓冲缓存：须<b>存活至引用它的命令缓冲执行完毕</b>。记录后立即销毁会让命令缓冲悬挂引用
    // 已释放对象，Adreno 在 vkEndCommandBuffer 收拢时解引用它 → 进程级原生崩溃（空指针解引用）。
    // 按（目标视图, 尺寸）惰性复用；管线/RenderPass 重建或 Dispose 时释放。
    private Framebuffer _framebuffer;
    private ImageView _framebufferView;
    private uint _framebufferW, _framebufferH;

    private bool _disposed;

    /// <summary>创建 RGBA→RGBA（格式/翻转）转换器（管线等 GPU 资源在首次使用时惰性创建）。</summary>
    /// <param name="device">借用的 VkDevice（Dispose 不销毁，生命周期归外部共享设备）。</param>
    /// <param name="physicalDevice">借用的 VkPhysicalDevice（Dispose 不销毁）。</param>
    /// <param name="logger">可选日志器。</param>
    public VulkanRgbaToRgbaConverter(Device device, PhysicalDevice physicalDevice, ILogger? logger = null)
    {
        _device = device;
        _physicalDevice = physicalDevice;
        _logger = logger;
    }

    /// <summary>确保对给定目标格式的转换管线就绪（幂等）。</summary>
    public void EnsurePipeline(Format targetFormat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_vertModule.Handle == 0 || _fragModule.Handle == 0)
        {
            _vertModule = CreateShaderModule(EmbeddedShaders.yuv_vert);
            _fragModule = CreateShaderModule(EmbeddedShaders.rgba_passthrough_frag);
        }
        if (_sampler.Handle == 0) EnsureSampler();
        if (_descriptorSetLayout.Handle == 0) EnsureDescriptorSetLayout();
        if (_descriptorPool.Handle == 0) EnsureDescriptorPoolAndSet();
        if (_pipelineLayout.Handle == 0) EnsurePipelineLayout();

        if (_pipelineTargetFormat == targetFormat && _renderPass.Handle != 0) return;

        ReleasePipelineAndRenderPass();
        CreateRenderPassAndPipeline(targetFormat);
        _pipelineTargetFormat = targetFormat;
    }

    /// <summary>为 RGBA 源图像创建普通采样视图（format=目标格式，无 YCbCr 转换 pNext）。</summary>
    public ImageView CreateImageView(Image rgbaImage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ImageViewCreateInfo vi = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = rgbaImage,
            ViewType = ImageViewType.Type2D,
            Format = _pipelineTargetFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        ImageView view;
        if (VulkanNative.CreateImageView(_device, &vi, null, out view) != Result.Success)
            throw new InvalidOperationException("vkCreateImageView（RGBA 源·普通视图）失败。");
        return view;
    }

    /// <summary>销毁经 <see cref="CreateImageView"/> 创建的采样视图（须于命令提交完成后调用）。</summary>
    public void DestroyImageView(ImageView view)
    {
        if (_device.Handle == 0 || view.Handle == 0) return;
        VulkanNative.DestroyImageView(_device, view, null);
    }

    /// <summary>把 RGBA 源图像经普通采样转换到<b>调用方提供的</b> RGBA 目标图像（同命令缓冲内记录）。</summary>
    public void Convert(CommandBuffer cmd, Image source, ImageView sourceView, uint width, uint height, Image target, ImageView targetView)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline.Handle == 0)
            throw new InvalidOperationException("管线尚未创建（须先调用 EnsurePipeline）。");

        // 帧缓冲须与命令缓冲同生命周期：缓存复用（目标视图/尺寸变化时重建），
        // 绝不在记录后立即销毁（悬挂引用会导致命令缓冲解引用已释放对象，见字段注释）。
        if (_framebuffer.Handle == 0 || _framebufferView.Handle != targetView.Handle
            || _framebufferW != width || _framebufferH != height)
        {
            if (_framebuffer.Handle != 0)
                VulkanNative.DestroyFramebuffer(_device, _framebuffer, null);
            var attachView = targetView;
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
                throw new InvalidOperationException("vkCreateFramebuffer（RGBA 转换）失败。");
            _framebufferView = targetView;
            _framebufferW = width;
            _framebufferH = height;
        }

        RenderInto(cmd, source, sourceView, width, height, target, _framebuffer);
    }

    private void RenderInto(CommandBuffer cmd, Image source, ImageView sourceView, uint width, uint height, Image targetImage, Framebuffer framebuffer)
    {
        TransitionImageLayout(cmd, source, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.MemoryWriteBit, AccessFlags.ShaderReadBit,
            PipelineStageFlags.AllCommandsBit, PipelineStageFlags.FragmentShaderBit,
            ImageAspectFlags.ColorBit);

        TransitionImageLayout(cmd, targetImage, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
            AccessFlags.None, AccessFlags.ColorAttachmentWriteBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ColorAttachmentOutputBit,
            ImageAspectFlags.ColorBit);

        UpdateDescriptorSet(sourceView);

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

        TransitionImageLayout(cmd, targetImage, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
            ImageAspectFlags.ColorBit);
    }

    /// <summary>
    /// 释放转换器持有的 GPU 资源（管线 / RenderPass / 帧缓冲缓存 / 描述符 / 采样器 / 着色器模块；幂等）。
    /// 不销毁借用的 VkDevice；源/目标图像所有权归调用方，不在释放范围。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device.Handle == 0) return;

        ReleaseAll();
        if (_vertModule.Handle != 0) { VulkanNative.DestroyShaderModule(_device, _vertModule, null); _vertModule = default; }
        if (_fragModule.Handle != 0) { VulkanNative.DestroyShaderModule(_device, _fragModule, null); _fragModule = default; }
    }

    private void ReleaseAll()
    {
        if (_device.Handle == 0) return;
        ReleasePipelineAndRenderPass();
        if (_pipelineLayout.Handle != 0) { VulkanNative.DestroyPipelineLayout(_device, _pipelineLayout, null); _pipelineLayout = default; }
        if (_descriptorPool.Handle != 0) { VulkanNative.DestroyDescriptorPool(_device, _descriptorPool, null); _descriptorPool = default; }
        if (_descriptorSetLayout.Handle != 0) { VulkanNative.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null); _descriptorSetLayout = default; }
        if (_sampler.Handle != 0) { VulkanNative.DestroySampler(_device, _sampler, null); _sampler = default; }
    }
}

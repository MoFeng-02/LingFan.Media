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
public sealed unsafe class VulkanYcbcrToRgbaConverter : IDisposable
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

    private bool _disposed;

    public VulkanYcbcrToRgbaConverter(Device device, PhysicalDevice physicalDevice, ILogger? logger = null)
    {
        _device = device;
        _physicalDevice = physicalDevice;
        _logger = logger;
    }

    /// <summary>
    /// 确保对给定 externalFormat 的转换管线就绪（幂等）。externalFormat / 建议转换参数变化时重建管线与采样器。
    /// </summary>
    /// <param name="targetFormat">RGBA 目标格式（须与下游 SwapChain 一致）。</param>
    /// <param name="externalFormat">AHB 外部格式标识（来自 VkAndroidHardwareBufferFormatPropertiesANDROID）。</param>
    /// <param name="formatProps">AHB 格式属性（转换参数建议值 + 采样能力位）。</param>
    public void EnsurePipeline(Format targetFormat, ulong externalFormat, in AndroidHardwareBufferFormatPropertiesANDROID formatProps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // externalFormat 变化（罕见：流格式切换）：转换对象/采样器/描述符布局/管线整体重建——
        // immutable sampler 固定在描述符布局里、pipeline layout 引用布局，均不可跨 externalFormat 复用。
        if (_pipelineExternalFormat != externalFormat && _ycbcrConversion.Handle != 0)
            ReleaseAll();

        if (_vertModule.Handle == 0 || _fragModule.Handle == 0)
        {
            _vertModule = CreateShaderModule(EmbeddedShaders.yuv_vert);
            _fragModule = CreateShaderModule(EmbeddedShaders.ycbcr_frag);
        }
        if (_ycbcrConversion.Handle == 0) CreateYcbcrConversion(externalFormat, formatProps);
        if (_sampler.Handle == 0) EnsureSampler();
        if (_descriptorSetLayout.Handle == 0) EnsureDescriptorSetLayout();
        if (_descriptorPool.Handle == 0) EnsureDescriptorPoolAndSet();
        if (_pipelineLayout.Handle == 0) EnsurePipelineLayout();

        if (_pipelineTargetFormat == targetFormat && _renderPass.Handle != 0) return;

        ReleasePipelineAndRenderPass();
        CreateRenderPassAndPipeline(targetFormat);
        _pipelineTargetFormat = targetFormat;
        _pipelineExternalFormat = externalFormat;
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
        Framebuffer fb;
        if (VulkanNative.CreateFramebuffer(_device, &fbInfo, null, out fb) != Result.Success)
            throw new InvalidOperationException("vkCreateFramebuffer（YCbCr 转换·外部 RGBA 目标）失败。");
        try
        {
            RenderInto(cmd, ahbSource, ahbView, width, height, rgbaTarget, fb);
        }
        finally
        {
            VulkanNative.DestroyFramebuffer(_device, fb, null);
        }
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

    // ── 转换对象 / 采样器 / 描述符构建 ──

    private void CreateYcbcrConversion(ulong externalFormat, in AndroidHardwareBufferFormatPropertiesANDROID formatProps)
    {
        // 规范：使用 externalFormat 时 format 须为 UNDEFINED，且 VkExternalFormatANDROID 挂 pNext。
        // 转换参数逐字段取 AHB 属性的建议值（model/range/offset/components），不自行猜测；
        // 色度过滤按能力位选择：支持 YcbcrConversionLinearFilter 用 Linear，否则 Nearest（规范 VUID）。
        var externalFormatInfo = new ExternalFormatANDROID
        {
            SType = StructureType.ExternalFormatAndroid,
            ExternalFormat = externalFormat,
        };
        bool linearChroma = (formatProps.FormatFeatures & FormatFeatureFlags.SampledImageYcbcrConversionLinearFilterBit) != 0;
        var ci = new SamplerYcbcrConversionCreateInfo
        {
            SType = StructureType.SamplerYcbcrConversionCreateInfo,
            Format = Format.Undefined,
            YcbcrModel = formatProps.SuggestedYcbcrModel,
            YcbcrRange = formatProps.SuggestedYcbcrRange,
            Components = formatProps.SamplerYcbcrConversionComponents,
            XChromaOffset = formatProps.SuggestedXChromaOffset,
            YChromaOffset = formatProps.SuggestedYChromaOffset,
            ChromaFilter = linearChroma ? Filter.Linear : Filter.Nearest,
            ForceExplicitReconstruction = false,
            PNext = &externalFormatInfo,
        };
        if (VulkanNative.CreateSamplerYcbcrConversion(_device, &ci, null, out _ycbcrConversion) != Result.Success)
            throw new InvalidOperationException("vkCreateSamplerYcbcrConversion（AHB 外部格式）失败。");
    }

    private void EnsureSampler()
    {
        if (_sampler.Handle != 0) return;
        // 规范：与视图相同的 YCbCr 转换须挂到采样器（SamplerYcbcrConversionInfo pNext）。
        var ycbcrInfo = new SamplerYcbcrConversionInfo
        {
            SType = StructureType.SamplerYcbcrConversionInfo,
            Conversion = _ycbcrConversion,
        };
        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            AnisotropyEnable = false,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = false,
            CompareEnable = false,
            MipmapMode = SamplerMipmapMode.Nearest,
            MinLod = 0f,
            MaxLod = 0f,
            PNext = &ycbcrInfo,
        };
        if (VulkanNative.CreateSampler(_device, &samplerInfo, null, out _sampler) != Result.Success)
            throw new InvalidOperationException("vkCreateSampler（YCbCr 转换采样器）失败。");
    }

    private void EnsureDescriptorSetLayout()
    {
        if (_descriptorSetLayout.Handle != 0) return;
        // 规范：Y′CBCR 转换须在管线创建期固定——combined image sampler 绑定使用 immutable sampler。
        var immutableSampler = _sampler;
        var bindings = stackalloc DescriptorSetLayoutBinding[1];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = &immutableSampler,
        };
        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = bindings,
        };
        if (VulkanNative.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _descriptorSetLayout) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorSetLayout（YCbCr 转换）失败。");
    }

    private void EnsureDescriptorPoolAndSet()
    {
        if (_descriptorPool.Handle != 0) return;
        DescriptorPoolSize poolSize = new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        if (VulkanNative.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorPool（YCbCr 转换）失败。");
        var dsl = _descriptorSetLayout;
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &dsl,
        };
        if (VulkanNative.AllocateDescriptorSets(_device, &allocInfo, out _descriptorSet) != Result.Success)
            throw new InvalidOperationException("vkAllocateDescriptorSets（YCbCr 转换）失败。");
    }

    private void UpdateDescriptorSet(ImageView ahbView)
    {
        // immutable sampler 场景：DescriptorImageInfo.Sampler 被忽略（布局已固定采样器），仅写视图与布局。
        var info = new DescriptorImageInfo
        {
            Sampler = default,
            ImageView = ahbView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info,
        };
        VulkanNative.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    // ── 管线构建（与 VulkanNv12ToRgbaConverter 同构，仅无推送常量——翻转烘焙进着色器）──

    private void EnsurePipelineLayout()
    {
        if (_pipelineLayout.Handle != 0) return;
        var dsl = _descriptorSetLayout;
        PipelineLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &dsl,
        };
        if (VulkanNative.CreatePipelineLayout(_device, &layoutInfo, null, out _pipelineLayout) != Result.Success)
            throw new InvalidOperationException("vkCreatePipelineLayout（YCbCr 转换）失败。");
    }

    private void CreateRenderPassAndPipeline(Format format)
    {
        AttachmentDescription colorAttachment = new()
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ColorAttachmentOptimal,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
        };
        AttachmentReference colorRef = new() { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
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
        RenderPassCreateInfo rpInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };
        if (VulkanNative.CreateRenderPass(_device, &rpInfo, null, out _renderPass) != Result.Success)
            throw new InvalidOperationException("vkCreateRenderPass（YCbCr 转换）失败。");

        CreateGraphicsPipeline(format, _renderPass, out _pipeline);
    }

    private void CreateGraphicsPipeline(Format format, RenderPass renderPass, out Pipeline pipeline)
    {
        PipelineShaderStageCreateInfo vertStage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _vertModule,
            PName = VulkanNative.StringToPtr("main"),
        };
        PipelineShaderStageCreateInfo fragStage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _fragModule,
            PName = VulkanNative.StringToPtr("main"),
        };
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = vertStage;
        stages[1] = fragStage;

        PipelineVertexInputStateCreateInfo vertexInput = new() { SType = StructureType.PipelineVertexInputStateCreateInfo };
        PipelineInputAssemblyStateCreateInfo inputAssembly = new()
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };
        PipelineViewportStateCreateInfo viewportState = new() { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        PipelineRasterizationStateCreateInfo rasterizer = new()
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.Clockwise,
            LineWidth = 1f,
        };
        PipelineMultisampleStateCreateInfo multisampling = new()
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        PipelineDepthStencilStateCreateInfo depthStencil = new()
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = false,
            DepthWriteEnable = false,
            StencilTestEnable = false,
        };
        PipelineColorBlendAttachmentState colorBlendAttachment = new()
        {
            BlendEnable = false,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };
        PipelineColorBlendStateCreateInfo colorBlending = new()
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment,
        };
        var dynamicStates = stackalloc DynamicState[2];
        dynamicStates[0] = DynamicState.Viewport;
        dynamicStates[1] = DynamicState.Scissor;
        PipelineDynamicStateCreateInfo dynamicState = new()
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates,
        };
        GraphicsPipelineCreateInfo pipelineInfo = new()
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling,
            PDepthStencilState = &depthStencil,
            PColorBlendState = &colorBlending,
            PDynamicState = &dynamicState,
            Layout = _pipelineLayout,
            RenderPass = renderPass,
            Subpass = 0,
        };
        Pipeline localPipe;
        Result result = VulkanNative.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, &localPipe);
        VulkanNative.FreeStringPtr(vertStage.PName);
        VulkanNative.FreeStringPtr(fragStage.PName);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateGraphicsPipelines（YCbCr 转换）失败: {result}");
        pipeline = localPipe;
    }

    private ShaderModule CreateShaderModule(ReadOnlyMemory<byte> code)
    {
        var span = code.Span;
        ShaderModuleCreateInfo info = new() { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)span.Length };
        ShaderModule module = default;
        Result result;
        fixed (byte* p = &MemoryMarshal.GetReference(span))
        {
            info.PCode = (uint*)p;
            result = VulkanNative.CreateShaderModule(_device, &info, null, out module);
        }
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateShaderModule（YCbCr 转换）失败: {result}");
        return module;
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

    private void ReleasePipelineAndRenderPass()
    {
        if (_device.Handle == 0) return;
        if (_pipeline.Handle != 0) { VulkanNative.DestroyPipeline(_device, _pipeline, null); _pipeline = default; }
        if (_renderPass.Handle != 0) { VulkanNative.DestroyRenderPass(_device, _renderPass, null); _renderPass = default; }
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

    private void ReleaseConversionAndSampler()
    {
        if (_device.Handle == 0) return;
        if (_sampler.Handle != 0) { VulkanNative.DestroySampler(_device, _sampler, null); _sampler = default; }
        if (_ycbcrConversion.Handle != 0) { VulkanNative.DestroySamplerYcbcrConversion(_device, _ycbcrConversion, null); _ycbcrConversion = default; }
    }
}

using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;
using LingFan.Media.Abstractions;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanYcbcrToRgbaConverter 的 Pipeline 职责段（部分类，由 VulkanYcbcrToRgbaConverter.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Convert/RenderInto/Dispose）在主文件；YCbCr 转换零反射原生调用。
public sealed unsafe partial class VulkanYcbcrToRgbaConverter
{
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
        // 可变采样器：DescriptorImageInfo.Sampler 由 UpdateDescriptorSet 显式写入（规避 Adreno 读空 immutable 字段）。
        // YCbCr 转换所需的 VkSamplerYcbcrConversion 经由 _sampler 创建时的 PNext 链携带，运行时在描述符里绑定，
        // 不依赖 immutable 布局固定——Android/Adreno 上 immutable YCbCr sampler 布局存在驱动兼容性空指针风险。
        var bindings = stackalloc DescriptorSetLayoutBinding[1];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = null,
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
        // 可变采样器：显式写入带 YCbCr 转换（创建时经 PNext 链携带）的 _sampler；Adreno 不能依赖 immutable 布局固定。
        var info = new DescriptorImageInfo
        {
            Sampler = _sampler,
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

    private void ReleasePipelineAndRenderPass()
    {
        if (_device.Handle == 0) return;
        // RenderPass 重建即令缓存帧缓冲失效（其创建时绑定了旧 RenderPass），一并释放。
        if (_framebuffer.Handle != 0)
        {
            VulkanNative.DestroyFramebuffer(_device, _framebuffer, null);
            _framebuffer = default;
            _framebufferView = default;
            _framebufferW = 0;
            _framebufferH = 0;
        }
        if (_pipeline.Handle != 0) { VulkanNative.DestroyPipeline(_device, _pipeline, null); _pipeline = default; }
        if (_renderPass.Handle != 0) { VulkanNative.DestroyRenderPass(_device, _renderPass, null); _renderPass = default; }
    }

    private void ReleaseConversionAndSampler()
    {
        if (_device.Handle == 0) return;
        if (_sampler.Handle != 0) { VulkanNative.DestroySampler(_device, _sampler, null); _sampler = default; }
        if (_ycbcrConversion.Handle != 0) { VulkanNative.DestroySamplerYcbcrConversion(_device, _ycbcrConversion, null); _ycbcrConversion = default; }
    }
}

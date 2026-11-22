using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;
using LingFan.Media.Abstractions;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNv12ToRgbaConverter 的 Pipeline 职责段（部分类，由 VulkanNv12ToRgbaConverter.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Convert/RenderInto/Dispose）在主文件；GPU 转换零反射原生调用。
public sealed unsafe partial class VulkanNv12ToRgbaConverter
{
    private void EnsurePipeline(Format targetFormat)
    {
        if (_vertModule.Handle == 0 || _fragModule.Handle == 0)
        {
            _vertModule = CreateShaderModule(EmbeddedShaders.yuv_vert);
            _fragModule = CreateShaderModule(EmbeddedShaders.yuv_frag);
        }
        if (_sampler.Handle == 0) EnsureSampler();
        if (_descriptorSetLayout.Handle == 0) EnsureDescriptorSetLayout();
        if (_descriptorPool.Handle == 0) EnsureDescriptorPoolAndSet();
        if (_pipelineLayout.Handle == 0) EnsurePipelineLayout();

        if (_pipelineTargetFormat == targetFormat && _renderPass.Handle != 0) return;
        ReleaseFramebuffer();
        ReleasePipelineAndRenderPass();

        CreateRenderPassAndPipeline(targetFormat);
        _pipelineTargetFormat = targetFormat;
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
            throw new InvalidOperationException("vkCreateRenderPass（NV12 转换）失败。");

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
            throw new InvalidOperationException($"vkCreateGraphicsPipelines（NV12 转换）失败: {result}");
        pipeline = localPipe;
    }

    private void EnsurePipelineLayout()
    {
        if (_pipelineLayout.Handle != 0) return;
        PushConstantRange pcRange = new()
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(PushConstants),
        };
        var dsl = _descriptorSetLayout;
        var pc = pcRange;
        PipelineLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &dsl,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pc,
        };
        if (VulkanNative.CreatePipelineLayout(_device, &layoutInfo, null, out _pipelineLayout) != Result.Success)
            throw new InvalidOperationException("vkCreatePipelineLayout（NV12 转换）失败。");
    }

    private void EnsureDescriptorSetLayout()
    {
        if (_descriptorSetLayout.Handle != 0) return;
        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        for (uint i = 0; i < 3; i++)
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };
        if (VulkanNative.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _descriptorSetLayout) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorSetLayout（NV12 转换）失败。");
    }

    private void EnsureSampler()
    {
        if (_sampler.Handle != 0) return;
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
            MipmapMode = SamplerMipmapMode.Linear,
            MinLod = 0f,
            MaxLod = 1f,
        };
        if (VulkanNative.CreateSampler(_device, &samplerInfo, null, out _sampler) != Result.Success)
            throw new InvalidOperationException("vkCreateSampler（NV12 转换）失败。");
    }

    private void EnsureDescriptorPoolAndSet()
    {
        if (_descriptorPool.Handle != 0) return;
        DescriptorPoolSize poolSize = new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 3 };
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        if (VulkanNative.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorPool（NV12 转换）失败。");
        var dsl = _descriptorSetLayout;
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &dsl,
        };
        if (VulkanNative.AllocateDescriptorSets(_device, &allocInfo, out _descriptorSet) != Result.Success)
            throw new InvalidOperationException("vkAllocateDescriptorSets（NV12 转换）失败。");
    }

    private void UpdateDescriptorSet((ImageView Y, ImageView UV) pv)
    {
        // NV12：binding0=Y, binding1=UV, binding2 复用 UV（着色器 NV12 分支不采样 binding2）
        var infos = stackalloc DescriptorImageInfo[3];
        infos[0] = new DescriptorImageInfo { Sampler = _sampler, ImageView = pv.Y, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        infos[1] = new DescriptorImageInfo { Sampler = _sampler, ImageView = pv.UV, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        infos[2] = new DescriptorImageInfo { Sampler = _sampler, ImageView = pv.UV, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };

        var writes = stackalloc WriteDescriptorSet[3];
        for (uint i = 0; i < 3; i++)
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSet,
                DstBinding = i,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &infos[i],
            };
        VulkanNative.UpdateDescriptorSets(_device, 3, writes, 0, null);
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
            throw new InvalidOperationException($"vkCreateShaderModule（NV12 转换）失败: {result}");
        return module;
    }

    private void ReleasePipelineAndRenderPass()
    {
        if (_device.Handle == 0) return;
        if (_pipeline.Handle != 0) { VulkanNative.DestroyPipeline(_device, _pipeline, null); _pipeline = default; }
        if (_renderPass.Handle != 0) { VulkanNative.DestroyRenderPass(_device, _renderPass, null); _renderPass = default; }
    }
}

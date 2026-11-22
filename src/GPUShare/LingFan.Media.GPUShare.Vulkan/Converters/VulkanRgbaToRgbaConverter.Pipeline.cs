using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanRgbaToRgbaConverter 的管线职责段（部分类，按职责拆分，编译期等价）。
// 采样器/描述符/管线布局/RenderPass/着色器创建与释放；Convert/RenderInto 编排在主文件。
public sealed unsafe partial class VulkanRgbaToRgbaConverter : IDisposable
{
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
            MipmapMode = SamplerMipmapMode.Nearest,
            MinLod = 0f,
            MaxLod = 0f,
        };
        if (VulkanNative.CreateSampler(_device, &samplerInfo, null, out _sampler) != Result.Success)
            throw new InvalidOperationException("vkCreateSampler（RGBA 普通采样器）失败。");
    }

    private void EnsureDescriptorSetLayout()
    {
        if (_descriptorSetLayout.Handle != 0) return;
        // 可变采样器：DescriptorImageInfo.Sampler 由 UpdateDescriptorSet 写入（规避 Adreno 读空 immutable 字段）。
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
            throw new InvalidOperationException("vkCreateDescriptorSetLayout（RGBA 转换）失败。");
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
            throw new InvalidOperationException("vkCreateDescriptorPool（RGBA 转换）失败。");
        var dsl = _descriptorSetLayout;
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &dsl,
        };
        if (VulkanNative.AllocateDescriptorSets(_device, &allocInfo, out _descriptorSet) != Result.Success)
            throw new InvalidOperationException("vkAllocateDescriptorSets（RGBA 转换）失败。");
    }

    private void UpdateDescriptorSet(ImageView sourceView)
    {
        var info = new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = sourceView,
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
            throw new InvalidOperationException("vkCreatePipelineLayout（RGBA 转换）失败。");
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
            throw new InvalidOperationException("vkCreateRenderPass（RGBA 转换）失败。");

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
            throw new InvalidOperationException($"vkCreateGraphicsPipelines（RGBA 转换）失败: {result}");
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
            throw new InvalidOperationException($"vkCreateShaderModule（RGBA 转换）失败: {result}");
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
}

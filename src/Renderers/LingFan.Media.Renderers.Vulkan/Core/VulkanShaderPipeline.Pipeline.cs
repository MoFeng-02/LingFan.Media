using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanShaderPipeline 的 Pipeline 职责段（部分类，由 VulkanShaderPipeline.cs 按职责拆分，编译期等价）。
// 字段与核心编排（RenderFrameTo/Dispose）在主文件；全部同步原生调用，由 VulkanRenderer._gate 串行化。
internal sealed unsafe partial class VulkanShaderPipeline
{
    private void EnsurePipelineLayout()
    {
        if (_pipelineLayout.Handle != 0) return;

        PushConstantRange pcRange = new()
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(PushConstants),
        };

        var descriptorSetLayout = _descriptorSetLayout;
        var pushConstantRange = pcRange;
        PipelineLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorSetLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange,
        };
        Result result = VulkanNative.CreatePipelineLayout(_device, &layoutInfo, null, out _pipelineLayout);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreatePipelineLayout 失败: {result}");
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

        PipelineVertexInputStateCreateInfo vertexInput = new()
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
        };

        PipelineInputAssemblyStateCreateInfo inputAssembly = new()
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };

        PipelineViewportStateCreateInfo viewportState = new()
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };

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

        // 清理入口点名称内存（VulkanNative 分配的 UTF-8 字符串）
        VulkanNative.FreeStringPtr(vertStage.PName);
        VulkanNative.FreeStringPtr(fragStage.PName);

        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateGraphicsPipelines 失败: {result}");

        pipeline = localPipe;
    }

    private void EnsureShaderModules()
    {
        if (_vertModule.Handle != 0 && _fragModule.Handle != 0) return;

        _vertModule = CreateShaderModule(EmbeddedShaders.yuv_vert);
        _fragModule = CreateShaderModule(EmbeddedShaders.yuv_frag);
    }

    private ShaderModule CreateShaderModule(ReadOnlyMemory<byte> code)
    {
        var span = code.Span;
        ShaderModuleCreateInfo info = new()
        {
            SType = StructureType.ShaderModuleCreateInfo,
            CodeSize = (nuint)span.Length,
        };
        ShaderModule module = default;
        Result result;
        fixed (byte* p = &MemoryMarshal.GetReference(span))
        {
            info.PCode = (uint*)p;
            result = VulkanNative.CreateShaderModule(_device, &info, null, out module);
        }
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateShaderModule 失败: {result}");
        return module;
    }

    private void EnsureDescriptorSetLayout()
    {
        if (_descriptorSetLayout.Handle != 0) return;

        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        for (uint i = 0; i < 3; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        }

        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };
        Result result = VulkanNative.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _descriptorSetLayout);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateDescriptorSetLayout 失败: {result}");
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
            MaxLod = 1f, // 平面纹理无 mip，只采样 level 0
        };
        Result result = VulkanNative.CreateSampler(_device, &samplerInfo, null, out _sampler);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateSampler 失败: {result}");
    }

    private void EnsureDescriptorPoolAndSet()
    {
        if (_descriptorPool.Handle != 0) return;

        DescriptorPoolSize poolSize = new()
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 3,
        };
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        Result result = VulkanNative.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateDescriptorPool 失败: {result}");

        var dsl = _descriptorSetLayout;
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &dsl,
        };
        result = VulkanNative.AllocateDescriptorSets(_device, &allocInfo, out _descriptorSet);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateDescriptorSets 失败: {result}");
    }


    // SwapChain ImageView / Framebuffer

    private void UpdateDescriptorSet(PixelFormat format)
    {
        // 绑定规则：
        // - 直接/半平面：binding1/2 复用 binding0 或 binding1 的视图（片段着色器对应分支不会采样它们）。
        // - 平面：binding0=Y, binding1=U, binding2=V。
        // 这样保证描述符始终合法，无需创建 dummy 视图。
        ImageView view0 = _planeViews[0];
        ImageView view1 = _planeViews[1].Handle != 0 ? _planeViews[1] : view0;
        ImageView view2 = _planeViews[2].Handle != 0 ? _planeViews[2] : view1;

        var infos = stackalloc DescriptorImageInfo[3];
        infos[0] = new DescriptorImageInfo { Sampler = _sampler, ImageView = view0, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        infos[1] = new DescriptorImageInfo { Sampler = _sampler, ImageView = view1, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        infos[2] = new DescriptorImageInfo { Sampler = _sampler, ImageView = view2, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };

        var writes = stackalloc WriteDescriptorSet[3];
        for (uint i = 0; i < 3; i++)
        {
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
        }
        VulkanNative.UpdateDescriptorSets(_device, 3, writes, 0, null);
    }


    // 辅助
}

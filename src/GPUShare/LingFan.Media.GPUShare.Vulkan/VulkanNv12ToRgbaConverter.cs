using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;
using LingFan.Media.Abstractions;

namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// 中性 NV12→RGBA 转换器（GPUShare.Vulkan，零反射 AOT 友好）。
/// 把外部 NV12 <c>VkImage</c>（Vulkan 硬解 DPB / 导入的 VAAPI·Android NV12 纹理）经片元着色器转 RGBA，
/// 供任意渲染器零拷贝 blit 上屏。复用 <c>yuv.frag</c> 的 NV12 数学（binding0=Y、binding1=UV）。
/// </summary>
/// <remarks>
/// <para><b>零拷贝语义</b>：仅对 NV12 源建两个平面视图（PLANE_0→R8 Y、PLANE_1→R8G8 UV）绑定描述符，
/// 着色器在 GPU 内完成 YUV→RGB，无任何 CPU 回读（符合「YUV→RGB 永在 GPU 片元着色器」宪法铁律）。</para>
/// <para><b>复用</b>：Vulkan 硬解路径的渲染器在 Present 时调用 <see cref="Convert"/>，把解码 DPB 的 NV12 图像转 RGBA；
/// <see cref="VulkanGpuFrameProducer"/> 导入 NV12 外部纹理后亦可调本转换器产出 RGBA 资源——单一 NV12→RGBA 归宿，
/// 渲染器保持 RGBA-only（与 GPUShare.D3D11.D3D11Nv12ToRgbaConverter 对称）。</para>
/// <para><b>异步策略</b>：全部同步原生调用（无 I/O await），与 Present 的 sync-only 分类一致。</para>
/// <para><b>AOT</b>：仅依赖 Silk.NET 纯数据定义与已编译 SPIR-V（Shaders.g.cs，运行期零反射）；无 [DllImport]/反射。</para>
/// </remarks>
public sealed unsafe class VulkanNv12ToRgbaConverter : IDisposable
{
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly ILogger? _logger;

    // 着色器模块 / 描述符 / 管线
    private ShaderModule _vertModule;
    private ShaderModule _fragModule;
    private Sampler _sampler;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private RenderPass _renderPass;
    private Framebuffer _framebuffer;
    private Format _pipelineTargetFormat = (Format)0;

    // 离屏 RGBA 目标（按尺寸/格式缓存）
    private Image _rgbaImage;
    private DeviceMemory _rgbaMemory;
    private ImageView _rgbaView;
    private uint _rgbaW, _rgbaH;
    private Format _rgbaFormat = (Format)0;

    // NV12 平面视图缓存（按源图像句柄；DPB 槽池固定，故条目上界有限）。
    // 视图在命令记录后即被 GPU 引用，须存活至命令执行完毕（渲染器每帧 QueueWaitIdle），故缓存而非每帧销毁。
    private readonly Dictionary<ulong, (ImageView Y, ImageView UV)> _planeViews = new();

    private bool _disposed;

    // 推送常量：vec4 srcCrop + int format + int isBgra + int flipY + int pad = 32 字节
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct PushConstants
    {
        [FieldOffset(0)] public fixed float SrcCrop[4];
        [FieldOffset(16)] public int Format;
        [FieldOffset(20)] public int IsBgra;
        [FieldOffset(24)] public int FlipY;
        [FieldOffset(28)] public int Pad;
    }

    private const int FormatNv12 = 2;

    public VulkanNv12ToRgbaConverter(Device device, PhysicalDevice physicalDevice, ILogger? logger = null)
    {
        _device = device;
        _physicalDevice = physicalDevice;
        _logger = logger;
    }

    /// <summary>
    /// 把 NV12 源图像转成 RGBA 目标图像（在同一条命令缓冲内记录；目标置于 <see cref="ImageLayout.TransferSrcOptimal"/> 供调用方 blit）。
    /// </summary>
    /// <param name="cmd">正在记录的命令缓冲（调用方持有 _gate 锁）。</param>
    /// <param name="nv12Source">NV12 VkImage（硬解 DPB 或导入纹理），格式须为 multi-planar NV12。</param>
    /// <param name="srcLayout">源图像交付时的布局（解码器传 TransferSrcOptimal）。</param>
    /// <param name="width">源/目标宽度（像素）。</param>
    /// <param name="height">源/目标高度（像素）。</param>
    /// <param name="targetFormat">目标 RGBA 格式（须与下游 SwapChain 格式一致，避免 blit 跨 R/B 顺序）；通常为 B8G8R8A8Unorm / R8G8B8A8Unorm。</param>
    /// <param name="rgbaTarget">转出的 RGBA VkImage（转后处于 TransferSrcOptimal）。</param>
    public void Convert(CommandBuffer cmd, Image nv12Source, ImageLayout srcLayout, uint width, uint height, Format targetFormat, out Image rgbaTarget)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsurePipeline(targetFormat);
        EnsureRgbaTarget(width, height, targetFormat);
        EnsureFramebuffer();
        EnsurePlaneViews(nv12Source);

        // NV12 两平面 → ShaderReadOnlyOptimal（供着色器采样；源已具备 SampledBit 用法）
        TransitionImageLayout(cmd, nv12Source, srcLayout, ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.None, AccessFlags.ShaderReadBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit,
            ImageAspectFlags.Plane0BitKhr);
        TransitionImageLayout(cmd, nv12Source, srcLayout, ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.None, AccessFlags.ShaderReadBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit,
            ImageAspectFlags.Plane1BitKhr);

        // RGBA 目标 → ColorAttachmentOptimal（render pass loadOp=Clear）
        TransitionImageLayout(cmd, _rgbaImage, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
            AccessFlags.None, AccessFlags.ColorAttachmentWriteBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ColorAttachmentOutputBit,
            ImageAspectFlags.ColorBit);

        UpdateDescriptorSet(_planeViews[nv12Source.Handle]);

        var clear = new ClearColorValue { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 0 };
        var clearValue = new ClearValue { Color = clear };
        var rpBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffer,
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

        PushConstants pc = new();
        pc.SrcCrop[0] = 0f; pc.SrcCrop[1] = 0f; pc.SrcCrop[2] = 1f; pc.SrcCrop[3] = 1f;
        pc.Format = FormatNv12;
        pc.IsBgra = 0; // 逻辑 R/G/B/A，由 attachment 格式决定存储分量顺序
        pc.FlipY = 1;  // 与软件帧路径一致，避免画面上下颠倒
        pc.Pad = 0;
        VulkanNative.CmdPushConstants(cmd, _pipelineLayout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            0, (uint)sizeof(PushConstants), &pc);

        var descriptorSet = _descriptorSet;
        VulkanNative.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &descriptorSet, 0, null);
        VulkanNative.CmdDraw(cmd, 3, 1, 0, 0);
        VulkanNative.CmdEndRenderPass(cmd);

        // RGBA 目标 → TransferSrcOptimal（供调用方 blit 到 SwapChain）
        TransitionImageLayout(cmd, _rgbaImage, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
            ImageAspectFlags.ColorBit);

        rgbaTarget = _rgbaImage;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device.Handle == 0) return;

        foreach (var kv in _planeViews)
        {
            if (kv.Value.Y.Handle != 0) VulkanNative.DestroyImageView(_device, kv.Value.Y, null);
            if (kv.Value.UV.Handle != 0) VulkanNative.DestroyImageView(_device, kv.Value.UV, null);
        }
        _planeViews.Clear();

        ReleaseFramebuffer();
        ReleasePipelineAndRenderPass();
        if (_pipelineLayout.Handle != 0) { VulkanNative.DestroyPipelineLayout(_device, _pipelineLayout, null); _pipelineLayout = default; }
        if (_descriptorPool.Handle != 0) { VulkanNative.DestroyDescriptorPool(_device, _descriptorPool, null); _descriptorPool = default; }
        if (_descriptorSetLayout.Handle != 0) { VulkanNative.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null); _descriptorSetLayout = default; }
        if (_sampler.Handle != 0) { VulkanNative.DestroySampler(_device, _sampler, null); _sampler = default; }
        if (_vertModule.Handle != 0) { VulkanNative.DestroyShaderModule(_device, _vertModule, null); _vertModule = default; }
        if (_fragModule.Handle != 0) { VulkanNative.DestroyShaderModule(_device, _fragModule, null); _fragModule = default; }

        ReleaseRgbaTarget();
    }

    // ── 管线 / 渲染目标构建 ──

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

    private void EnsureRgbaTarget(uint width, uint height, Format format)
    {
        if (_rgbaImage.Handle != 0 && _rgbaW == width && _rgbaH == height && _rgbaFormat == format) return;

        ReleaseFramebuffer();
        ReleaseRgbaTarget();

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        if (VulkanNative.CreateImage(_device, &imageInfo, null, out _rgbaImage) != Result.Success)
            throw new InvalidOperationException("vkCreateImage（NV12 转换 RGBA 目标）失败。");

        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, _rgbaImage, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
        };
        if (VulkanNative.AllocateMemory(_device, &allocInfo, null, out _rgbaMemory) != Result.Success)
            throw new InvalidOperationException("vkAllocateMemory（NV12 转换 RGBA 目标）失败。");
        if (VulkanNative.BindImageMemory(_device, _rgbaImage, _rgbaMemory, 0) != Result.Success)
            throw new InvalidOperationException("vkBindImageMemory（NV12 转换 RGBA 目标）失败。");

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _rgbaImage,
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
        if (VulkanNative.CreateImageView(_device, &viewInfo, null, out _rgbaView) != Result.Success)
            throw new InvalidOperationException("vkCreateImageView（NV12 转换 RGBA 目标）失败。");

        _rgbaW = width; _rgbaH = height; _rgbaFormat = format;
    }

    private void EnsureFramebuffer()
    {
        if (_framebuffer.Handle != 0) return;
        if (_renderPass.Handle == 0 || _rgbaView.Handle == 0) return;
        var view = _rgbaView;
        FramebufferCreateInfo fbInfo = new()
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = _rgbaW,
            Height = _rgbaH,
            Layers = 1,
        };
        if (VulkanNative.CreateFramebuffer(_device, &fbInfo, null, out _framebuffer) != Result.Success)
            throw new InvalidOperationException("vkCreateFramebuffer（NV12 转换）失败。");
    }

    private void EnsurePlaneViews(Image nv12Source)
    {
        if (_planeViews.ContainsKey(nv12Source.Handle)) return;
        ImageView yView = CreatePlaneView(nv12Source, ImageAspectFlags.Plane0BitKhr, Format.R8Unorm);
        ImageView uvView = CreatePlaneView(nv12Source, ImageAspectFlags.Plane1BitKhr, Format.R8G8Unorm);
        _planeViews[nv12Source.Handle] = (yView, uvView);
    }

    private ImageView CreatePlaneView(Image img, ImageAspectFlags aspect, Format format)
    {
        ImageViewCreateInfo vi = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = img,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        ImageView view;
        if (VulkanNative.CreateImageView(_device, &vi, null, out view) != Result.Success)
            throw new InvalidOperationException("vkCreateImageView（NV12 平面视图）失败。");
        return view;
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

    private void ReleaseFramebuffer()
    {
        if (_device.Handle == 0) return;
        if (_framebuffer.Handle != 0) { VulkanNative.DestroyFramebuffer(_device, _framebuffer, null); _framebuffer = default; }
    }

    private void ReleasePipelineAndRenderPass()
    {
        if (_device.Handle == 0) return;
        if (_pipeline.Handle != 0) { VulkanNative.DestroyPipeline(_device, _pipeline, null); _pipeline = default; }
        if (_renderPass.Handle != 0) { VulkanNative.DestroyRenderPass(_device, _renderPass, null); _renderPass = default; }
    }

    private void ReleaseRgbaTarget()
    {
        if (_device.Handle == 0) return;
        if (_rgbaView.Handle != 0) { VulkanNative.DestroyImageView(_device, _rgbaView, null); _rgbaView = default; }
        if (_rgbaImage.Handle != 0) { VulkanNative.DestroyImage(_device, _rgbaImage, null); _rgbaImage = default; }
        if (_rgbaMemory.Handle != 0) { VulkanNative.FreeMemory(_device, _rgbaMemory, null); _rgbaMemory = default; }
        _rgbaW = 0; _rgbaH = 0; _rgbaFormat = (Format)0;
    }

    private uint FindMemoryType(uint memoryTypeBits, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties props;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &props);
        for (uint i = 0; i < props.MemoryTypeCount; i++)
            if ((memoryTypeBits & (1u << (int)i)) != 0 && (props.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        for (uint i = 0; i < props.MemoryTypeCount; i++)
            if ((memoryTypeBits & (1u << (int)i)) != 0) return i;
        throw new InvalidOperationException("未找到合适的 Vulkan 内存类型（NV12 转换）。");
    }
}

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
        EnsurePipelineCore(targetFormat);
        _pipelineExternalFormat = externalFormat;
    }

    /// <summary>
    /// 确保对<b>具体多平面格式</b>（如 NV12 G8_B8R8_2PLANE_420_UNORM）的转换管线就绪（幂等）。
    /// 供 Vulkan Video 硬解 DPB 路径：图像为具体格式（非 AHB 外部格式），整图多平面视图 +
    /// YCbCr 采样器一次 texture() 取回转换后 RGB——无需 per-plane R8/R8G8 视图，
    /// 因而不要求图像带 MUTABLE_FORMAT（部分驱动视频格式支持集 flags=0 不含该位）。
    /// </summary>
    /// <param name="targetFormat">RGBA 目标格式（须与下游 SwapChain 一致）。</param>
    /// <param name="sourceFormat">源多平面格式（NV12）。</param>
    /// <param name="ycbcrModel">YCbCr 模型（BT.601/709 等，按流色彩元数据选择）。</param>
    /// <param name="ycbcrRange">YCbCr 范围（Full/ItuNarrow，按流色彩元数据选择）。</param>
    public void EnsurePipelineForFormat(Format targetFormat, Format sourceFormat,
        SamplerYcbcrModelConversion ycbcrModel, SamplerYcbcrRange ycbcrRange)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 源格式/模型变化：整体重建（与 externalFormat 分支同口径）。
        if ((_pipelineSourceFormat != sourceFormat || _pipelineYcbcrModel != ycbcrModel || _pipelineYcbcrRange != ycbcrRange)
            && _ycbcrConversion.Handle != 0)
            ReleaseAll();

        if (_vertModule.Handle == 0 || _fragModule.Handle == 0)
        {
            _vertModule = CreateShaderModule(EmbeddedShaders.yuv_vert);
            _fragModule = CreateShaderModule(EmbeddedShaders.ycbcr_frag);
        }
        if (_ycbcrConversion.Handle == 0)
        {
            // 具体格式：format 填真实格式、无 ExternalFormat pNext；色度中心/重建按规范默认。
            var ci = new SamplerYcbcrConversionCreateInfo
            {
                SType = StructureType.SamplerYcbcrConversionCreateInfo,
                Format = sourceFormat,
                YcbcrModel = ycbcrModel,
                YcbcrRange = ycbcrRange,
                Components = new ComponentMapping
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity,
                },
                XChromaOffset = ChromaLocation.CositedEven,
                YChromaOffset = ChromaLocation.Midpoint,
                ChromaFilter = Filter.Linear,
                ForceExplicitReconstruction = false,
            };
            if (VulkanNative.CreateSamplerYcbcrConversion(_device, &ci, null, out _ycbcrConversion) != Result.Success)
                throw new InvalidOperationException("vkCreateSamplerYcbcrConversion（NV12 具体格式）失败。");
        }
        EnsurePipelineCore(targetFormat);
        _pipelineSourceFormat = sourceFormat;
        _pipelineYcbcrModel = ycbcrModel;
        _pipelineYcbcrRange = ycbcrRange;
    }

    /// <summary>管线公共段：采样器/描述符/布局/RenderPass（两种 EnsurePipeline 入口共用）。</summary>
    private void EnsurePipelineCore(Format targetFormat)
    {
        if (_sampler.Handle == 0) EnsureSampler();
        if (_descriptorSetLayout.Handle == 0) EnsureDescriptorSetLayout();
        if (_descriptorPool.Handle == 0) EnsureDescriptorPoolAndSet();
        if (_pipelineLayout.Handle == 0) EnsurePipelineLayout();

        if (_pipelineTargetFormat == targetFormat && _renderPass.Handle != 0) return;

        ReleasePipelineAndRenderPass();
        CreateRenderPassAndPipeline(targetFormat);
        _pipelineTargetFormat = targetFormat;
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
        // 规范 VUID-02738/08114：视图带 YCbCr 转换时，描述符布局必须以 immutable sampler 固定
        // 同一转换的采样器（可变写入非法）。AHB 路径此前用可变写入规避 Adreno 空指针风险，
        // 但那是驱动缺陷 workaround；规范正解是 immutable。此处按规范用 immutable sampler。
        var sampler = _sampler;
        var bindings = stackalloc DescriptorSetLayoutBinding[1];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = &sampler,
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
        // immutable sampler 已在布局固定（含 YCbCr 转换）；此处 Sampler 留空（immutable 时忽略），
        // 仅写视图与布局。规范 VUID-02738：带转换的视图必须配 immutable sampler 布局。
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

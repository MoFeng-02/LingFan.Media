using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Descriptors 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
    /// <summary>创建帧缓冲（vkCreateFramebuffer；把图像视图集合绑定到渲染通道附件）。</summary>
    public static unsafe Result CreateFramebuffer(Device device, FramebufferCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out Framebuffer pFramebuffer)
    {
        Framebuffer tmp;
        var r = _createFramebuffer(device, pCreateInfo, pAllocator, &tmp);
        pFramebuffer = tmp;
        return r;
    }

    /// <summary>销毁帧缓冲（vkDestroyFramebuffer；不影响其引用的图像视图）。</summary>
    public static unsafe void DestroyFramebuffer(Device device, Framebuffer framebuffer, AllocationCallbacks* pAllocator)
        => _destroyFramebuffer(device, framebuffer, pAllocator);

    /// <summary>创建渲染通道（vkCreateRenderPass；附件格式/负载存储操作/子通道依赖）。</summary>
    public static unsafe Result CreateRenderPass(Device device, RenderPassCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out RenderPass pRenderPass)
    {
        RenderPass tmp;
        var r = _createRenderPass(device, pCreateInfo, pAllocator, &tmp);
        pRenderPass = tmp;
        return r;
    }

    /// <summary>销毁渲染通道（vkDestroyRenderPass）。</summary>
    public static unsafe void DestroyRenderPass(Device device, RenderPass renderPass, AllocationCallbacks* pAllocator)
        => _destroyRenderPass(device, renderPass, pAllocator);

    /// <summary>创建着色器模块（vkCreateShaderModule；输入 SPIR-V 字节码）。</summary>
    public static unsafe Result CreateShaderModule(Device device, ShaderModuleCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out ShaderModule pShaderModule)
    {
        ShaderModule tmp;
        var r = _createShaderModule(device, pCreateInfo, pAllocator, &tmp);
        pShaderModule = tmp;
        return r;
    }

    /// <summary>销毁着色器模块（vkDestroyShaderModule；已建管线不受影响）。</summary>
    public static unsafe void DestroyShaderModule(Device device, ShaderModule shaderModule, AllocationCallbacks* pAllocator)
        => _destroyShaderModule(device, shaderModule, pAllocator);

    /// <summary>创建管线布局（vkCreatePipelineLayout；描述符集布局 + push constant 范围）。</summary>
    public static unsafe Result CreatePipelineLayout(Device device, PipelineLayoutCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out PipelineLayout pPipelineLayout)
    {
        PipelineLayout tmp;
        var r = _createPipelineLayout(device, pCreateInfo, pAllocator, &tmp);
        pPipelineLayout = tmp;
        return r;
    }

    /// <summary>销毁管线布局（vkDestroyPipelineLayout）。</summary>
    public static unsafe void DestroyPipelineLayout(Device device, PipelineLayout pipelineLayout, AllocationCallbacks* pAllocator)
        => _destroyPipelineLayout(device, pipelineLayout, pAllocator);

    /// <summary>批量创建图形管线（vkCreateGraphicsPipelines；pipelineCache 可为 null）。</summary>
    public static unsafe Result CreateGraphicsPipelines(Device device, PipelineCache pipelineCache, uint createInfoCount, GraphicsPipelineCreateInfo* pCreateInfos, AllocationCallbacks* pAllocator, Pipeline* pPipelines)
        => _createGraphicsPipelines(device, pipelineCache, createInfoCount, pCreateInfos, pAllocator, pPipelines);

    /// <summary>销毁管线（vkDestroyPipeline）。</summary>
    public static unsafe void DestroyPipeline(Device device, Pipeline pipeline, AllocationCallbacks* pAllocator)
        => _destroyPipeline(device, pipeline, pAllocator);

    /// <summary>创建描述符集布局（vkCreateDescriptorSetLayout；声明各绑定的类型/数量/着色阶段）。</summary>
    public static unsafe Result CreateDescriptorSetLayout(Device device, DescriptorSetLayoutCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out DescriptorSetLayout pSetLayout)
    {
        DescriptorSetLayout tmp;
        var r = _createDescriptorSetLayout(device, pCreateInfo, pAllocator, &tmp);
        pSetLayout = tmp;
        return r;
    }

    /// <summary>销毁描述符集布局（vkDestroyDescriptorSetLayout）。</summary>
    public static unsafe void DestroyDescriptorSetLayout(Device device, DescriptorSetLayout descriptorSetLayout, AllocationCallbacks* pAllocator)
        => _destroyDescriptorSetLayout(device, descriptorSetLayout, pAllocator);

    /// <summary>创建描述符池（vkCreateDescriptorPool；描述符集从其分配）。</summary>
    public static unsafe Result CreateDescriptorPool(Device device, DescriptorPoolCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out DescriptorPool pDescriptorPool)
    {
        DescriptorPool tmp;
        var r = _createDescriptorPool(device, pCreateInfo, pAllocator, &tmp);
        pDescriptorPool = tmp;
        return r;
    }

    /// <summary>销毁描述符池（vkDestroyDescriptorPool；其分配的描述符集一并失效）。</summary>
    public static unsafe void DestroyDescriptorPool(Device device, DescriptorPool descriptorPool, AllocationCallbacks* pAllocator)
        => _destroyDescriptorPool(device, descriptorPool, pAllocator);

    /// <summary>从描述符池分配单个描述符集（vkAllocateDescriptorSets 单集包装）。</summary>
    public static unsafe Result AllocateDescriptorSets(Device device, DescriptorSetAllocateInfo* pAllocateInfo, out DescriptorSet pDescriptorSets)
    {
        DescriptorSet tmp;
        var r = _allocateDescriptorSets(device, pAllocateInfo, &tmp);
        pDescriptorSets = tmp;
        return r;
    }

    /// <summary>写入/复制描述符绑定（vkUpdateDescriptorSets；把图像视图/采样器/缓冲绑定到描述符集）。</summary>
    public static unsafe void UpdateDescriptorSets(Device device, uint descriptorWriteCount, WriteDescriptorSet* pDescriptorWrites, uint descriptorCopyCount, CopyDescriptorSet* pDescriptorCopies)
        => _updateDescriptorSets(device, descriptorWriteCount, pDescriptorWrites, descriptorCopyCount, pDescriptorCopies);
}

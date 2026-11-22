using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Descriptors 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
    public static unsafe Result CreateFramebuffer(Device device, FramebufferCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out Framebuffer pFramebuffer)
    {
        Framebuffer tmp;
        var r = _createFramebuffer(device, pCreateInfo, pAllocator, &tmp);
        pFramebuffer = tmp;
        return r;
    }

    public static unsafe void DestroyFramebuffer(Device device, Framebuffer framebuffer, AllocationCallbacks* pAllocator)
        => _destroyFramebuffer(device, framebuffer, pAllocator);

    public static unsafe Result CreateRenderPass(Device device, RenderPassCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out RenderPass pRenderPass)
    {
        RenderPass tmp;
        var r = _createRenderPass(device, pCreateInfo, pAllocator, &tmp);
        pRenderPass = tmp;
        return r;
    }

    public static unsafe void DestroyRenderPass(Device device, RenderPass renderPass, AllocationCallbacks* pAllocator)
        => _destroyRenderPass(device, renderPass, pAllocator);

    public static unsafe Result CreateShaderModule(Device device, ShaderModuleCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out ShaderModule pShaderModule)
    {
        ShaderModule tmp;
        var r = _createShaderModule(device, pCreateInfo, pAllocator, &tmp);
        pShaderModule = tmp;
        return r;
    }

    public static unsafe void DestroyShaderModule(Device device, ShaderModule shaderModule, AllocationCallbacks* pAllocator)
        => _destroyShaderModule(device, shaderModule, pAllocator);

    public static unsafe Result CreatePipelineLayout(Device device, PipelineLayoutCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out PipelineLayout pPipelineLayout)
    {
        PipelineLayout tmp;
        var r = _createPipelineLayout(device, pCreateInfo, pAllocator, &tmp);
        pPipelineLayout = tmp;
        return r;
    }

    public static unsafe void DestroyPipelineLayout(Device device, PipelineLayout pipelineLayout, AllocationCallbacks* pAllocator)
        => _destroyPipelineLayout(device, pipelineLayout, pAllocator);

    public static unsafe Result CreateGraphicsPipelines(Device device, PipelineCache pipelineCache, uint createInfoCount, GraphicsPipelineCreateInfo* pCreateInfos, AllocationCallbacks* pAllocator, Pipeline* pPipelines)
        => _createGraphicsPipelines(device, pipelineCache, createInfoCount, pCreateInfos, pAllocator, pPipelines);

    public static unsafe void DestroyPipeline(Device device, Pipeline pipeline, AllocationCallbacks* pAllocator)
        => _destroyPipeline(device, pipeline, pAllocator);

    public static unsafe Result CreateDescriptorSetLayout(Device device, DescriptorSetLayoutCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out DescriptorSetLayout pSetLayout)
    {
        DescriptorSetLayout tmp;
        var r = _createDescriptorSetLayout(device, pCreateInfo, pAllocator, &tmp);
        pSetLayout = tmp;
        return r;
    }

    public static unsafe void DestroyDescriptorSetLayout(Device device, DescriptorSetLayout descriptorSetLayout, AllocationCallbacks* pAllocator)
        => _destroyDescriptorSetLayout(device, descriptorSetLayout, pAllocator);

    public static unsafe Result CreateDescriptorPool(Device device, DescriptorPoolCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out DescriptorPool pDescriptorPool)
    {
        DescriptorPool tmp;
        var r = _createDescriptorPool(device, pCreateInfo, pAllocator, &tmp);
        pDescriptorPool = tmp;
        return r;
    }

    public static unsafe void DestroyDescriptorPool(Device device, DescriptorPool descriptorPool, AllocationCallbacks* pAllocator)
        => _destroyDescriptorPool(device, descriptorPool, pAllocator);

    public static unsafe Result AllocateDescriptorSets(Device device, DescriptorSetAllocateInfo* pAllocateInfo, out DescriptorSet pDescriptorSets)
    {
        DescriptorSet tmp;
        var r = _allocateDescriptorSets(device, pAllocateInfo, &tmp);
        pDescriptorSets = tmp;
        return r;
    }

    public static unsafe void UpdateDescriptorSets(Device device, uint descriptorWriteCount, WriteDescriptorSet* pDescriptorWrites, uint descriptorCopyCount, CopyDescriptorSet* pDescriptorCopies)
        => _updateDescriptorSets(device, descriptorWriteCount, pDescriptorWrites, descriptorCopyCount, pDescriptorCopies);
}

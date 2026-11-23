using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Resources 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
    /// <summary>创建 VkImage（vkCreateImage ref 重载；外部内存导入经 pNext 链声明，创建后须绑定内存才可用）。</summary>
    public static unsafe Result CreateImage(Device device, ref ImageCreateInfo pCreateInfo, AllocationCallbacks* pAllocator, out Image pImage)
    {
        fixed (ImageCreateInfo* p = &pCreateInfo)
        {
            Image tmp;
            var r = _createImage(device, p, pAllocator, &tmp);
            pImage = tmp;
            return r;
        }
    }

    /// <summary>创建 VkImage（vkCreateImage 指针重载；便于携带 pNext 扩展链）。</summary>
    public static unsafe Result CreateImage(Device device, ImageCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out Image pImage)
    {
        Image tmp;
        var r = _createImage(device, pCreateInfo, pAllocator, &tmp);
        pImage = tmp;
        return r;
    }

    /// <summary>销毁 VkImage（vkDestroyImage；调用方须确保无在途 GPU 访问）。</summary>
    public static unsafe void DestroyImage(Device device, Image image, AllocationCallbacks* pAllocator) => _destroyImage(device, image, pAllocator);

    /// <summary>创建图像视图（vkCreateImageView；采样/附件访问图像的入口，Y′CBCR 转换经 pNext 挂接）。</summary>
    public static unsafe Result CreateImageView(Device device, ImageViewCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out ImageView pView)
    {
        ImageView tmp;
        var r = _createImageView(device, pCreateInfo, pAllocator, &tmp);
        pView = tmp;
        return r;
    }

    /// <summary>销毁图像视图（vkDestroyImageView；不影响底层图像）。</summary>
    public static unsafe void DestroyImageView(Device device, ImageView imageView, AllocationCallbacks* pAllocator)
        => _destroyImageView(device, imageView, pAllocator);

    /// <summary>创建采样器 Y′CBCR 转换（须设备已启用 samplerYcbcrConversion 特性）。</summary>
    public static unsafe Result CreateSamplerYcbcrConversion(Device device, SamplerYcbcrConversionCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out SamplerYcbcrConversion pYcbcrConversion)
    {
        if (_createSamplerYcbcrConversion == null)
            throw new InvalidOperationException("vkCreateSamplerYcbcrConversion 不可用（Vulkan 1.1 / VK_KHR_sampler_ycbcr_conversion 不支持）。");
        SamplerYcbcrConversion tmp;
        Result r = _createSamplerYcbcrConversion(device, pCreateInfo, pAllocator, &tmp);
        pYcbcrConversion = tmp;
        return r;
    }

    /// <summary>销毁采样器 Y′CBCR 转换。</summary>
    public static unsafe void DestroySamplerYcbcrConversion(Device device, SamplerYcbcrConversion ycbcrConversion, AllocationCallbacks* pAllocator)
    {
        if (_destroySamplerYcbcrConversion == null) return;
        _destroySamplerYcbcrConversion(device, ycbcrConversion, pAllocator);
    }

    /// <summary>创建 VkBuffer（vkCreateBuffer；回读/上传缓冲，创建后须绑定内存才可用）。</summary>
    public static unsafe Result CreateBuffer(Device device, ref BufferCreateInfo pCreateInfo, AllocationCallbacks* pAllocator, out Buffer pBuffer)
    {
        fixed (BufferCreateInfo* p = &pCreateInfo)
        {
            Buffer tmp;
            var r = _createBuffer(device, p, pAllocator, &tmp);
            pBuffer = tmp;
            return r;
        }
    }

    /// <summary>销毁 VkBuffer（vkDestroyBuffer；调用方须确保无在途 GPU 访问）。</summary>
    public static unsafe void DestroyBuffer(Device device, Buffer buffer, AllocationCallbacks* pAllocator) => _destroyBuffer(device, buffer, pAllocator);

    /// <summary>创建采样器（vkCreateSampler；过滤/寻址模式，Y′CBCR 不可变采样器经 pNext 挂接）。</summary>
    public static unsafe Result CreateSampler(Device device, SamplerCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out Sampler pSampler)
    {
        Sampler tmp;
        var r = _createSampler(device, pCreateInfo, pAllocator, &tmp);
        pSampler = tmp;
        return r;
    }

    /// <summary>销毁采样器（vkDestroySampler）。</summary>
    public static unsafe void DestroySampler(Device device, Sampler sampler, AllocationCallbacks* pAllocator)
        => _destroySampler(device, sampler, pAllocator);

}

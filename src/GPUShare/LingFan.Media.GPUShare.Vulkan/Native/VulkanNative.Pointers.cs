namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的函数指针字段段（部分类，由 VulkanNative.cs 按职责拆分，编译期等价）。
// 全部经引导符号分阶段解析赋值（InitBootstrap/InitInstance/InitDevice 见其余分部）。
public static unsafe partial class VulkanNative
{
    // 函数指针字段
    private static unsafe delegate* unmanaged[Stdcall]<InstanceCreateInfo*, AllocationCallbacks*, Instance*, Result> _createInstance;
    private static unsafe delegate* unmanaged[Stdcall]<Instance, AllocationCallbacks*, void> _destroyInstance;
    private static unsafe delegate* unmanaged[Stdcall]<Instance, uint*, PhysicalDevice*, Result> _enumeratePhysicalDevices;
    private static unsafe delegate* unmanaged[Stdcall]<PhysicalDevice, PhysicalDeviceProperties*, void> _getPhysicalDeviceProperties;
    private static unsafe delegate* unmanaged[Stdcall]<PhysicalDevice, PhysicalDeviceMemoryProperties*, void> _getPhysicalDeviceMemoryProperties;
    private static unsafe delegate* unmanaged[Stdcall]<PhysicalDevice, uint*, QueueFamilyProperties*, void> _getPhysicalDeviceQueueFamilyProperties;
    private static unsafe delegate* unmanaged[Stdcall]<PhysicalDevice, DeviceCreateInfo*, AllocationCallbacks*, Device*, Result> _createDevice;
    private static unsafe delegate* unmanaged[Stdcall]<Device, AllocationCallbacks*, void> _destroyDevice;
    private static unsafe delegate* unmanaged[Stdcall]<Device, uint, uint, Queue*, void> _getDeviceQueue;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Result> _deviceWaitIdle;
    private static unsafe delegate* unmanaged[Stdcall]<byte*, uint*, ExtensionProperties*, Result> _enumerateInstanceExtensionProperties;

    private static unsafe delegate* unmanaged[Stdcall]<Device, CommandPoolCreateInfo*, AllocationCallbacks*, CommandPool*, Result> _createCommandPool;
    private static unsafe delegate* unmanaged[Stdcall]<Device, CommandPool, AllocationCallbacks*, void> _destroyCommandPool;
    private static unsafe delegate* unmanaged[Stdcall]<Device, CommandBufferAllocateInfo*, CommandBuffer*, Result> _allocateCommandBuffers;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, CommandBufferBeginInfo*, Result> _beginCommandBuffer;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, Result> _endCommandBuffer;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, CommandBufferResetFlags, Result> _resetCommandBuffer;
    private static unsafe delegate* unmanaged[Stdcall]<Queue, uint, SubmitInfo*, nint, Result> _queueSubmit;
    private static unsafe delegate* unmanaged[Stdcall]<Queue, Result> _queueWaitIdle;
    private static unsafe delegate* unmanaged[Stdcall]<Device, SemaphoreCreateInfo*, AllocationCallbacks*, Semaphore*, Result> _createSemaphore;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Semaphore, AllocationCallbacks*, void> _destroySemaphore;
    private static unsafe delegate* unmanaged[Stdcall]<Device, FenceCreateInfo*, AllocationCallbacks*, Fence*, Result> _createFence;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Fence, AllocationCallbacks*, void> _destroyFence;
    private static unsafe delegate* unmanaged[Stdcall]<Device, uint, Fence*, uint, ulong, Result> _waitForFences;
    private static unsafe delegate* unmanaged[Stdcall]<Device, uint, Fence*, Result> _resetFences;

    private static unsafe delegate* unmanaged[Stdcall]<Device, ImageCreateInfo*, AllocationCallbacks*, Image*, Result> _createImage;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Image, AllocationCallbacks*, void> _destroyImage;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Image, MemoryRequirements*, void> _getImageMemoryRequirements;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Image, ImageSubresource*, SubresourceLayout*, void> _getImageSubresourceLayout;
    private static unsafe delegate* unmanaged[Stdcall]<Device, ImageMemoryRequirementsInfo2*, MemoryRequirements2*, void> _getImageMemoryRequirements2;
    private static unsafe delegate* unmanaged[Stdcall]<Device, MemoryAllocateInfo*, AllocationCallbacks*, DeviceMemory*, Result> _allocateMemory;
    private static unsafe delegate* unmanaged[Stdcall]<Device, DeviceMemory, AllocationCallbacks*, void> _freeMemory;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Image, DeviceMemory, ulong, Result> _bindImageMemory;
    private static unsafe delegate* unmanaged[Stdcall]<Device, ImageViewCreateInfo*, AllocationCallbacks*, ImageView*, Result> _createImageView;
    private static unsafe delegate* unmanaged[Stdcall]<Device, ImageView, AllocationCallbacks*, void> _destroyImageView;
    private static unsafe delegate* unmanaged[Stdcall]<Device, BufferCreateInfo*, AllocationCallbacks*, Buffer*, Result> _createBuffer;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Buffer, AllocationCallbacks*, void> _destroyBuffer;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Buffer, MemoryRequirements*, void> _getBufferMemoryRequirements;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Buffer, DeviceMemory, ulong, Result> _bindBufferMemory;
    private static unsafe delegate* unmanaged[Stdcall]<Device, DeviceMemory, ulong, ulong, uint, void**, Result> _mapMemory;
    private static unsafe delegate* unmanaged[Stdcall]<Device, DeviceMemory, void> _unmapMemory;
    private static unsafe delegate* unmanaged[Stdcall]<Device, FramebufferCreateInfo*, AllocationCallbacks*, Framebuffer*, Result> _createFramebuffer;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Framebuffer, AllocationCallbacks*, void> _destroyFramebuffer;
    private static unsafe delegate* unmanaged[Stdcall]<Device, RenderPassCreateInfo*, AllocationCallbacks*, RenderPass*, Result> _createRenderPass;
    private static unsafe delegate* unmanaged[Stdcall]<Device, RenderPass, AllocationCallbacks*, void> _destroyRenderPass;
    private static unsafe delegate* unmanaged[Stdcall]<Device, ShaderModuleCreateInfo*, AllocationCallbacks*, ShaderModule*, Result> _createShaderModule;
    private static unsafe delegate* unmanaged[Stdcall]<Device, ShaderModule, AllocationCallbacks*, void> _destroyShaderModule;
    private static unsafe delegate* unmanaged[Stdcall]<Device, PipelineLayoutCreateInfo*, AllocationCallbacks*, PipelineLayout*, Result> _createPipelineLayout;
    private static unsafe delegate* unmanaged[Stdcall]<Device, PipelineLayout, AllocationCallbacks*, void> _destroyPipelineLayout;
    private static unsafe delegate* unmanaged[Stdcall]<Device, PipelineCache, uint, GraphicsPipelineCreateInfo*, AllocationCallbacks*, Pipeline*, Result> _createGraphicsPipelines;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Pipeline, AllocationCallbacks*, void> _destroyPipeline;
    private static unsafe delegate* unmanaged[Stdcall]<Device, DescriptorSetLayoutCreateInfo*, AllocationCallbacks*, DescriptorSetLayout*, Result> _createDescriptorSetLayout;
    private static unsafe delegate* unmanaged[Stdcall]<Device, DescriptorSetLayout, AllocationCallbacks*, void> _destroyDescriptorSetLayout;
    private static unsafe delegate* unmanaged[Stdcall]<Device, DescriptorPoolCreateInfo*, AllocationCallbacks*, DescriptorPool*, Result> _createDescriptorPool;
    private static unsafe delegate* unmanaged[Stdcall]<Device, DescriptorPool, AllocationCallbacks*, void> _destroyDescriptorPool;
    private static unsafe delegate* unmanaged[Stdcall]<Device, DescriptorSetAllocateInfo*, DescriptorSet*, Result> _allocateDescriptorSets;
    private static unsafe delegate* unmanaged[Stdcall]<Device, uint, WriteDescriptorSet*, uint, CopyDescriptorSet*, void> _updateDescriptorSets;
    private static unsafe delegate* unmanaged[Stdcall]<Device, SamplerCreateInfo*, AllocationCallbacks*, Sampler*, Result> _createSampler;
    private static unsafe delegate* unmanaged[Stdcall]<Device, Sampler, AllocationCallbacks*, void> _destroySampler;

    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, RenderPassBeginInfo*, SubpassContents, void> _cmdBeginRenderPass;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, void> _cmdEndRenderPass;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, PipelineBindPoint, Pipeline, void> _cmdBindPipeline;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, uint, uint, Viewport*, void> _cmdSetViewport;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, uint, uint, Rect2D*, void> _cmdSetScissor;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, PipelineLayout, ShaderStageFlags, uint, uint, void*, void> _cmdPushConstants;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, PipelineBindPoint, PipelineLayout, uint, uint, DescriptorSet*, uint, uint*, void> _cmdBindDescriptorSets;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, uint, uint, uint, uint, void> _cmdDraw;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, PipelineStageFlags, PipelineStageFlags, uint, uint, MemoryBarrier*, uint, BufferMemoryBarrier*, uint, ImageMemoryBarrier*, void> _cmdPipelineBarrier;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, Buffer, Image, ImageLayout, uint, BufferImageCopy*, void> _cmdCopyBufferToImage;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, Image, ImageLayout, Buffer, uint, BufferImageCopy*, void> _cmdCopyImageToBuffer;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, Image, ImageLayout, Image, ImageLayout, uint, ImageCopy*, void> _cmdCopyImage;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, Image, ImageLayout, Image, ImageLayout, uint, ImageBlit*, Filter, void> _cmdBlitImage;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, Image, ImageLayout, ClearColorValue*, uint, ImageSubresourceRange*, void> _cmdClearColorImage;

    private static unsafe delegate* unmanaged[Stdcall]<Instance, Win32SurfaceCreateInfoKHR*, AllocationCallbacks*, SurfaceKHR*, Result> _createWin32SurfaceKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Instance, AndroidSurfaceCreateInfoKHR*, AllocationCallbacks*, SurfaceKHR*, Result> _createAndroidSurfaceKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Instance, MetalSurfaceCreateInfoEXT*, AllocationCallbacks*, SurfaceKHR*, Result> _createMetalSurfaceEXT;
    private static unsafe delegate* unmanaged[Stdcall]<Instance, XlibSurfaceCreateInfoKHR*, AllocationCallbacks*, SurfaceKHR*, Result> _createXlibSurfaceKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Instance, WaylandSurfaceCreateInfoKHR*, AllocationCallbacks*, SurfaceKHR*, Result> _createWaylandSurfaceKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Instance, SurfaceKHR, AllocationCallbacks*, void> _destroySurfaceKHR;
    private static unsafe delegate* unmanaged[Stdcall]<PhysicalDevice, SurfaceKHR, SurfaceCapabilitiesKHR*, Result> _getPhysicalDeviceSurfaceCapabilitiesKHR;
    private static unsafe delegate* unmanaged[Stdcall]<PhysicalDevice, SurfaceKHR, uint*, SurfaceFormatKHR*, Result> _getPhysicalDeviceSurfaceFormatsKHR;
    private static unsafe delegate* unmanaged[Stdcall]<PhysicalDevice, PhysicalDeviceProperties2*, Result> _getPhysicalDeviceProperties2;
    private static unsafe delegate* unmanaged[Stdcall]<PhysicalDevice, PhysicalDeviceFeatures2*, Result> _getPhysicalDeviceFeatures2;
    private static unsafe delegate* unmanaged[Stdcall]<PhysicalDevice, byte*, uint*, ExtensionProperties*, Result> _enumerateDeviceExtensionProperties;

    private static unsafe delegate* unmanaged[Stdcall]<Device, SwapchainCreateInfoKHR*, AllocationCallbacks*, SwapchainKHR*, Result> _createSwapchainKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, SwapchainKHR, AllocationCallbacks*, void> _destroySwapchainKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, SwapchainKHR, uint*, Image*, Result> _getSwapchainImagesKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result> _acquireNextImageKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Queue, PresentInfoKHR*, Result> _queuePresentKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, MemoryGetWin32HandleInfoKHR*, void*, Result> _getMemoryWin32HandleKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, MemoryGetFdInfoKHR*, int*, Result> _getMemoryFdKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, ExternalMemoryHandleTypeFlags, int, MemoryFdPropertiesKHR*, Result> _getMemoryFdPropertiesKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, SemaphoreGetWin32HandleInfoKHR*, void*, Result> _getSemaphoreWin32HandleKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, SemaphoreGetFdInfoKHR*, int*, Result> _getSemaphoreFdKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, ExportMetalObjectsInfoEXT*, void> _exportMetalObjectsEXT;
    private static unsafe delegate* unmanaged[Stdcall]<Device, nint, AndroidHardwareBufferPropertiesANDROID*, Result> _getAndroidHardwareBufferPropertiesANDROID;
    private static unsafe delegate* unmanaged[Stdcall]<Device, MemoryGetAndroidHardwareBufferInfoANDROID*, nint*, Result> _getMemoryAndroidHardwareBufferANDROID;
    private static unsafe delegate* unmanaged[Stdcall]<Device, SamplerYcbcrConversionCreateInfo*, AllocationCallbacks*, SamplerYcbcrConversion*, Result> _createSamplerYcbcrConversion;
    private static unsafe delegate* unmanaged[Stdcall]<Device, SamplerYcbcrConversion, AllocationCallbacks*, void> _destroySamplerYcbcrConversion;
}

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Init 职责段（部分类，由 VulkanNative.cs 按职责拆分，编译期等价）。
// 函数指针字段与 proc-addr 解析在主文件；本段为三阶段初始化（Bootstrap/Instance/Device）与就绪断言。
// Vulkan 规范要求 vkGetInstanceProcAddr(NULL,…) 只保证引导子集，其余须分阶段经实例/设备句柄解析。
public static unsafe partial class VulkanNative
{
    /// <summary>枚举实例层可用扩展名（全局命令，实例创建前可调用；须先 <see cref="InitBootstrap"/>）。</summary>
    public static unsafe string[] EnumerateInstanceExtensionNames()
    {
        uint count = 0;
        if (_enumerateInstanceExtensionProperties == null
            || _enumerateInstanceExtensionProperties((byte*)null, &count, null) != Result.Success
            || count == 0)
            return [];
        var props = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = props)
        {
            if (_enumerateInstanceExtensionProperties((byte*)null, &count, p) != Result.Success)
                return [];
            var names = new string[count];
            for (uint i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> span = new(p[i].ExtensionName, 256);
                int nul = span.IndexOf((byte)0);
                names[i] = System.Text.Encoding.UTF8.GetString(nul >= 0 ? span[..nul] : span);
            }
            return names;
        }
    }

    /// <summary>
    /// 阶段 0：引导解析。仅经 <c>vkGetInstanceProcAddr(NULL, name)</c> 解析「引导子集」
    /// （<c>vkCreateInstance</c> / <c>vkEnumerateInstanceExtensionProperties</c>）。
    /// <para><b>为什么不能一次解析全部</b>：Vulkan 的全局派发（<c>instance = NULL</c>）只保证返回
    /// 引导子集的函数指针；其余函数（含 <c>vkDestroyInstance</c> / <c>vkCreateDevice</c> 等）经 NULL 派发
    /// 会静默返回 <c>NULL</c>。这是 Vulkan 跨 loader 兼容的强制要求，
    /// 也是 Silk.NET / volk 的实际做法：实例级函数用实例句柄、设备级函数用设备句柄二次解析。</para>
    /// <para>须在创建 VkInstance 之前调用一次。</para>
    /// </summary>
    public static void InitBootstrap()
    {
        if (_bootstrapped) return;
        lock (_loadLock)
        {
            if (_bootstrapped) return;
            _createInstance = (delegate* unmanaged[Stdcall]<InstanceCreateInfo*, AllocationCallbacks*, Instance*, Result>)vkGetInstanceProcAddr(nint.Zero, "vkCreateInstance");
            _enumerateInstanceExtensionProperties = (delegate* unmanaged[Stdcall]<byte*, uint*, ExtensionProperties*, Result>)vkGetInstanceProcAddr(nint.Zero, "vkEnumerateInstanceExtensionProperties");
            AssertBootstrap();
            _bootstrapped = true;
        }
    }

    /// <summary>
    /// 阶段 1：实例级函数解析。须传入已创建（且已启用所需 WSI 扩展）的 <see cref="Instance"/> 句柄。
    /// 经 <c>vkGetInstanceProcAddr(instance, name)</c> 解析实例级函数与 KHR 实例扩展。
    /// 须在 <c>vkCreateInstance</c> 成功后、任何实例级调用前调用一次。
    /// </summary>
    public static unsafe void InitInstance(Instance instance)
    {
        if (_instanceReady) return;
        lock (_loadLock)
        {
            if (_instanceReady) return;
            nint h = instance.Handle;
            _destroyInstance = (delegate* unmanaged[Stdcall]<Instance, AllocationCallbacks*, void>)vkGetInstanceProcAddr(h, "vkDestroyInstance");
            _enumeratePhysicalDevices = (delegate* unmanaged[Stdcall]<Instance, uint*, PhysicalDevice*, Result>)vkGetInstanceProcAddr(h, "vkEnumeratePhysicalDevices");
            _getPhysicalDeviceProperties = (delegate* unmanaged[Stdcall]<PhysicalDevice, PhysicalDeviceProperties*, void>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceProperties");
            _getPhysicalDeviceMemoryProperties = (delegate* unmanaged[Stdcall]<PhysicalDevice, PhysicalDeviceMemoryProperties*, void>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceMemoryProperties");
            _getPhysicalDeviceQueueFamilyProperties = (delegate* unmanaged[Stdcall]<PhysicalDevice, uint*, QueueFamilyProperties*, void>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceQueueFamilyProperties");
            _createDevice = (delegate* unmanaged[Stdcall]<PhysicalDevice, DeviceCreateInfo*, AllocationCallbacks*, Device*, Result>)vkGetInstanceProcAddr(h, "vkCreateDevice");
            // KHR WSI 实例扩展（须实例已启用对应扩展）
            _createWin32SurfaceKHR = (delegate* unmanaged[Stdcall]<Instance, Win32SurfaceCreateInfoKHR*, AllocationCallbacks*, SurfaceKHR*, Result>)vkGetInstanceProcAddr(h, "vkCreateWin32SurfaceKHR");
            _createAndroidSurfaceKHR = (delegate* unmanaged[Stdcall]<Instance, AndroidSurfaceCreateInfoKHR*, AllocationCallbacks*, SurfaceKHR*, Result>)vkGetInstanceProcAddr(h, "vkCreateAndroidSurfaceKHR");
            // VK_EXT_metal_surface（Apple / MoltenVK；Silk.NET 静态包装不含 vkCreateMetalSurfaceEXT，须运行时经 vkGetInstanceProcAddr 解析）
            _createMetalSurfaceEXT = (delegate* unmanaged[Stdcall]<Instance, MetalSurfaceCreateInfoEXT*, AllocationCallbacks*, SurfaceKHR*, Result>)vkGetInstanceProcAddr(h, "vkCreateMetalSurfaceEXT");
            // KHR WSI Linux 扩展（X11 / Wayland；须实例已启用对应扩展，未启用则为 null，调用方自检）
            _createXlibSurfaceKHR = (delegate* unmanaged[Stdcall]<Instance, XlibSurfaceCreateInfoKHR*, AllocationCallbacks*, SurfaceKHR*, Result>)vkGetInstanceProcAddr(h, "vkCreateXlibSurfaceKHR");
            _createWaylandSurfaceKHR = (delegate* unmanaged[Stdcall]<Instance, WaylandSurfaceCreateInfoKHR*, AllocationCallbacks*, SurfaceKHR*, Result>)vkGetInstanceProcAddr(h, "vkCreateWaylandSurfaceKHR");
            _destroySurfaceKHR = (delegate* unmanaged[Stdcall]<Instance, SurfaceKHR, AllocationCallbacks*, void>)vkGetInstanceProcAddr(h, "vkDestroySurfaceKHR");
            _getPhysicalDeviceSurfaceCapabilitiesKHR = (delegate* unmanaged[Stdcall]<PhysicalDevice, SurfaceKHR, SurfaceCapabilitiesKHR*, Result>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            _getPhysicalDeviceSurfaceFormatsKHR = (delegate* unmanaged[Stdcall]<PhysicalDevice, SurfaceKHR, uint*, SurfaceFormatKHR*, Result>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceSurfaceFormatsKHR");
            // 设备能力查询（UUID/LUID 对齐、设备扩展枚举）
            _getPhysicalDeviceProperties2 = (delegate* unmanaged[Stdcall]<PhysicalDevice, PhysicalDeviceProperties2*, Result>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceProperties2");
            // 实例级设备特性查询（Vulkan 1.1 core；首参 VkPhysicalDevice 须 vkGetInstanceProcAddr 解析）：
            // Android AHB 零拷贝须先经它查询 samplerYcbcrConversion 特性，仅在支持时才于设备创建处链入特性结构。
            _getPhysicalDeviceFeatures2 = (delegate* unmanaged[Stdcall]<PhysicalDevice, PhysicalDeviceFeatures2*, Result>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceFeatures2");
            _enumerateDeviceExtensionProperties = (delegate* unmanaged[Stdcall]<PhysicalDevice, byte*, uint*, ExtensionProperties*, Result>)vkGetInstanceProcAddr(h, "vkEnumerateDeviceExtensionProperties");
            // 物理设备级视频能力查询（VK_KHR_video_queue 实例扩展函数，须经 vkGetInstanceProcAddr）
            // 注意：vkGetPhysicalDeviceVideoCapabilitiesKHR 首参为 VkPhysicalDevice，属实例级函数；
            // 经 vkGetDeviceProcAddr 解析必返回 NULL（设备派发表不含物理设备级函数），此坑曾导致硬解初始化崩溃。
            _getPhysicalDeviceVideoCapabilitiesKHR = (delegate* unmanaged[Stdcall]<PhysicalDevice, VideoProfileInfoKHR*, VideoCapabilitiesKHR*, Result>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceVideoCapabilitiesKHR");
            // 实例级视频格式属性查询（VK_KHR_video_queue）：首参 VkPhysicalDevice，须 vkGetInstanceProcAddr 解析。
            // 解码 DPB / 码流图带 VIDEO_DECODE_* usage 时，其 usage/flags 必须经本查询取返回值（VUID-06811 规范要求），
            // 硬编码组合会被判"profile 无关"→ 解码静默 no-op → 全零 DPB 绿屏。
            _getPhysicalDeviceVideoFormatPropertiesKHR = (delegate* unmanaged[Stdcall]<PhysicalDevice, PhysicalDeviceVideoFormatInfoKHR*, uint*, VideoFormatPropertiesKHR*, Result>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceVideoFormatPropertiesKHR");
            AssertInstance();
            _instanceReady = true;
        }
    }

    /// <summary>
    /// 阶段 2：设备级函数解析。须传入已创建（且已启用 <c>VK_KHR_swapchain</c>）的 <see cref="Device"/> 句柄。
    /// 经 <c>vkGetDeviceProcAddr(device, name)</c> 解析设备级函数与 KHR swapchain 设备扩展。
    /// 须在 <c>vkCreateDevice</c> 成功后、任何设备级调用前调用一次。
    /// </summary>
    public static unsafe void InitDevice(Device device, bool samplerYcbcrFeatureEnabled = false)
    {
        if (_deviceReady) return;
        lock (_loadLock)
        {
            if (_deviceReady) return;
            _samplerYcbcrFeatureEnabled = samplerYcbcrFeatureEnabled;
            nint h = device.Handle;
            _destroyDevice = (delegate* unmanaged[Stdcall]<Device, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyDevice");
            _getDeviceQueue = (delegate* unmanaged[Stdcall]<Device, uint, uint, Queue*, void>)vkGetDeviceProcAddr(h, "vkGetDeviceQueue");
            _deviceWaitIdle = (delegate* unmanaged[Stdcall]<Device, Result>)vkGetDeviceProcAddr(h, "vkDeviceWaitIdle");
            _createCommandPool = (delegate* unmanaged[Stdcall]<Device, CommandPoolCreateInfo*, AllocationCallbacks*, CommandPool*, Result>)vkGetDeviceProcAddr(h, "vkCreateCommandPool");
            _destroyCommandPool = (delegate* unmanaged[Stdcall]<Device, CommandPool, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyCommandPool");
            _allocateCommandBuffers = (delegate* unmanaged[Stdcall]<Device, CommandBufferAllocateInfo*, CommandBuffer*, Result>)vkGetDeviceProcAddr(h, "vkAllocateCommandBuffers");
            _beginCommandBuffer = (delegate* unmanaged[Stdcall]<CommandBuffer, CommandBufferBeginInfo*, Result>)vkGetDeviceProcAddr(h, "vkBeginCommandBuffer");
            _endCommandBuffer = (delegate* unmanaged[Stdcall]<CommandBuffer, Result>)vkGetDeviceProcAddr(h, "vkEndCommandBuffer");
            _resetCommandBuffer = (delegate* unmanaged[Stdcall]<CommandBuffer, CommandBufferResetFlags, Result>)vkGetDeviceProcAddr(h, "vkResetCommandBuffer");
            _queueSubmit = (delegate* unmanaged[Stdcall]<Queue, uint, SubmitInfo*, nint, Result>)vkGetDeviceProcAddr(h, "vkQueueSubmit");
            _queueWaitIdle = (delegate* unmanaged[Stdcall]<Queue, Result>)vkGetDeviceProcAddr(h, "vkQueueWaitIdle");
            _createSemaphore = (delegate* unmanaged[Stdcall]<Device, SemaphoreCreateInfo*, AllocationCallbacks*, Semaphore*, Result>)vkGetDeviceProcAddr(h, "vkCreateSemaphore");
            _destroySemaphore = (delegate* unmanaged[Stdcall]<Device, Semaphore, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroySemaphore");
            // Fence（no-airspace 共享表面源的有限超时 ping-pong，与 D3D11 的 16ms keyed mutex 超时对称）
            _createFence = (delegate* unmanaged[Stdcall]<Device, FenceCreateInfo*, AllocationCallbacks*, Fence*, Result>)vkGetDeviceProcAddr(h, "vkCreateFence");
            _destroyFence = (delegate* unmanaged[Stdcall]<Device, Fence, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyFence");
            _waitForFences = (delegate* unmanaged[Stdcall]<Device, uint, Fence*, uint, ulong, Result>)vkGetDeviceProcAddr(h, "vkWaitForFences");
            _resetFences = (delegate* unmanaged[Stdcall]<Device, uint, Fence*, Result>)vkGetDeviceProcAddr(h, "vkResetFences");
            _createImage = (delegate* unmanaged[Stdcall]<Device, ImageCreateInfo*, AllocationCallbacks*, Image*, Result>)vkGetDeviceProcAddr(h, "vkCreateImage");
            _destroyImage = (delegate* unmanaged[Stdcall]<Device, Image, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyImage");
            _getImageMemoryRequirements = (delegate* unmanaged[Stdcall]<Device, Image, MemoryRequirements*, void>)vkGetDeviceProcAddr(h, "vkGetImageMemoryRequirements");
            // 外部内存导入纹理的内存需求必须用 v2（带回 VkMemoryDedicatedRequirements）取权威 size/memoryTypeBits；
            // v1 vkGetImageMemoryRequirements 对带 VK_EXTERNAL_MEMORY 的 image 返回的 memoryTypeBits/size 在导入场景下不可靠
            // （NVIDIA 上尤甚，导致 vkAllocateMemory 误判为真实分配 → ErrorOutOfDeviceMemory）。
            _getImageMemoryRequirements2 = (delegate* unmanaged[Stdcall]<Device, ImageMemoryRequirementsInfo2*, MemoryRequirements2*, void>)vkGetDeviceProcAddr(h, "vkGetImageMemoryRequirements2");
            _allocateMemory = (delegate* unmanaged[Stdcall]<Device, MemoryAllocateInfo*, AllocationCallbacks*, DeviceMemory*, Result>)vkGetDeviceProcAddr(h, "vkAllocateMemory");
            _freeMemory = (delegate* unmanaged[Stdcall]<Device, DeviceMemory, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkFreeMemory");
            _bindImageMemory = (delegate* unmanaged[Stdcall]<Device, Image, DeviceMemory, ulong, Result>)vkGetDeviceProcAddr(h, "vkBindImageMemory");
            _createImageView = (delegate* unmanaged[Stdcall]<Device, ImageViewCreateInfo*, AllocationCallbacks*, ImageView*, Result>)vkGetDeviceProcAddr(h, "vkCreateImageView");
            _destroyImageView = (delegate* unmanaged[Stdcall]<Device, ImageView, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyImageView");
            _createBuffer = (delegate* unmanaged[Stdcall]<Device, BufferCreateInfo*, AllocationCallbacks*, Buffer*, Result>)vkGetDeviceProcAddr(h, "vkCreateBuffer");
            _destroyBuffer = (delegate* unmanaged[Stdcall]<Device, Buffer, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyBuffer");
            _getBufferMemoryRequirements = (delegate* unmanaged[Stdcall]<Device, Buffer, MemoryRequirements*, void>)vkGetDeviceProcAddr(h, "vkGetBufferMemoryRequirements");
            _bindBufferMemory = (delegate* unmanaged[Stdcall]<Device, Buffer, DeviceMemory, ulong, Result>)vkGetDeviceProcAddr(h, "vkBindBufferMemory");
            _mapMemory = (delegate* unmanaged[Stdcall]<Device, DeviceMemory, ulong, ulong, uint, void**, Result>)vkGetDeviceProcAddr(h, "vkMapMemory");
            _unmapMemory = (delegate* unmanaged[Stdcall]<Device, DeviceMemory, void>)vkGetDeviceProcAddr(h, "vkUnmapMemory");
            _createFramebuffer = (delegate* unmanaged[Stdcall]<Device, FramebufferCreateInfo*, AllocationCallbacks*, Framebuffer*, Result>)vkGetDeviceProcAddr(h, "vkCreateFramebuffer");
            _destroyFramebuffer = (delegate* unmanaged[Stdcall]<Device, Framebuffer, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyFramebuffer");
            _createRenderPass = (delegate* unmanaged[Stdcall]<Device, RenderPassCreateInfo*, AllocationCallbacks*, RenderPass*, Result>)vkGetDeviceProcAddr(h, "vkCreateRenderPass");
            _destroyRenderPass = (delegate* unmanaged[Stdcall]<Device, RenderPass, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyRenderPass");
            _createShaderModule = (delegate* unmanaged[Stdcall]<Device, ShaderModuleCreateInfo*, AllocationCallbacks*, ShaderModule*, Result>)vkGetDeviceProcAddr(h, "vkCreateShaderModule");
            _destroyShaderModule = (delegate* unmanaged[Stdcall]<Device, ShaderModule, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyShaderModule");
            _createPipelineLayout = (delegate* unmanaged[Stdcall]<Device, PipelineLayoutCreateInfo*, AllocationCallbacks*, PipelineLayout*, Result>)vkGetDeviceProcAddr(h, "vkCreatePipelineLayout");
            _destroyPipelineLayout = (delegate* unmanaged[Stdcall]<Device, PipelineLayout, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyPipelineLayout");
            _createGraphicsPipelines = (delegate* unmanaged[Stdcall]<Device, PipelineCache, uint, GraphicsPipelineCreateInfo*, AllocationCallbacks*, Pipeline*, Result>)vkGetDeviceProcAddr(h, "vkCreateGraphicsPipelines");
            _destroyPipeline = (delegate* unmanaged[Stdcall]<Device, Pipeline, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyPipeline");
            _createDescriptorSetLayout = (delegate* unmanaged[Stdcall]<Device, DescriptorSetLayoutCreateInfo*, AllocationCallbacks*, DescriptorSetLayout*, Result>)vkGetDeviceProcAddr(h, "vkCreateDescriptorSetLayout");
            _destroyDescriptorSetLayout = (delegate* unmanaged[Stdcall]<Device, DescriptorSetLayout, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyDescriptorSetLayout");
            _createDescriptorPool = (delegate* unmanaged[Stdcall]<Device, DescriptorPoolCreateInfo*, AllocationCallbacks*, DescriptorPool*, Result>)vkGetDeviceProcAddr(h, "vkCreateDescriptorPool");
            _destroyDescriptorPool = (delegate* unmanaged[Stdcall]<Device, DescriptorPool, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyDescriptorPool");
            _allocateDescriptorSets = (delegate* unmanaged[Stdcall]<Device, DescriptorSetAllocateInfo*, DescriptorSet*, Result>)vkGetDeviceProcAddr(h, "vkAllocateDescriptorSets");
            _updateDescriptorSets = (delegate* unmanaged[Stdcall]<Device, uint, WriteDescriptorSet*, uint, CopyDescriptorSet*, void>)vkGetDeviceProcAddr(h, "vkUpdateDescriptorSets");
            _createSampler = (delegate* unmanaged[Stdcall]<Device, SamplerCreateInfo*, AllocationCallbacks*, Sampler*, Result>)vkGetDeviceProcAddr(h, "vkCreateSampler");
            _destroySampler = (delegate* unmanaged[Stdcall]<Device, Sampler, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroySampler");
            _cmdBeginRenderPass = (delegate* unmanaged[Stdcall]<CommandBuffer, RenderPassBeginInfo*, SubpassContents, void>)vkGetDeviceProcAddr(h, "vkCmdBeginRenderPass");
            _cmdEndRenderPass = (delegate* unmanaged[Stdcall]<CommandBuffer, void>)vkGetDeviceProcAddr(h, "vkCmdEndRenderPass");
            _cmdBindPipeline = (delegate* unmanaged[Stdcall]<CommandBuffer, PipelineBindPoint, Pipeline, void>)vkGetDeviceProcAddr(h, "vkCmdBindPipeline");
            _cmdSetViewport = (delegate* unmanaged[Stdcall]<CommandBuffer, uint, uint, Viewport*, void>)vkGetDeviceProcAddr(h, "vkCmdSetViewport");
            _cmdSetScissor = (delegate* unmanaged[Stdcall]<CommandBuffer, uint, uint, Rect2D*, void>)vkGetDeviceProcAddr(h, "vkCmdSetScissor");
            _cmdPushConstants = (delegate* unmanaged[Stdcall]<CommandBuffer, PipelineLayout, ShaderStageFlags, uint, uint, void*, void>)vkGetDeviceProcAddr(h, "vkCmdPushConstants");
            _cmdBindDescriptorSets = (delegate* unmanaged[Stdcall]<CommandBuffer, PipelineBindPoint, PipelineLayout, uint, uint, DescriptorSet*, uint, uint*, void>)vkGetDeviceProcAddr(h, "vkCmdBindDescriptorSets");
            _cmdDraw = (delegate* unmanaged[Stdcall]<CommandBuffer, uint, uint, uint, uint, void>)vkGetDeviceProcAddr(h, "vkCmdDraw");
            _cmdPipelineBarrier = (delegate* unmanaged[Stdcall]<CommandBuffer, PipelineStageFlags, PipelineStageFlags, uint, uint, MemoryBarrier*, uint, BufferMemoryBarrier*, uint, ImageMemoryBarrier*, void>)vkGetDeviceProcAddr(h, "vkCmdPipelineBarrier");
            _cmdCopyBufferToImage = (delegate* unmanaged[Stdcall]<CommandBuffer, Buffer, Image, ImageLayout, uint, BufferImageCopy*, void>)vkGetDeviceProcAddr(h, "vkCmdCopyBufferToImage");
            _cmdCopyImageToBuffer = (delegate* unmanaged[Stdcall]<CommandBuffer, Image, ImageLayout, Buffer, uint, BufferImageCopy*, void>)vkGetDeviceProcAddr(h, "vkCmdCopyImageToBuffer");
            _cmdCopyImage = (delegate* unmanaged[Stdcall]<CommandBuffer, Image, ImageLayout, Image, ImageLayout, uint, ImageCopy*, void>)vkGetDeviceProcAddr(h, "vkCmdCopyImage");
            _cmdBlitImage = (delegate* unmanaged[Stdcall]<CommandBuffer, Image, ImageLayout, Image, ImageLayout, uint, ImageBlit*, Filter, void>)vkGetDeviceProcAddr(h, "vkCmdBlitImage");
            _cmdClearColorImage = (delegate* unmanaged[Stdcall]<CommandBuffer, Image, ImageLayout, ClearColorValue*, uint, ImageSubresourceRange*, void>)vkGetDeviceProcAddr(h, "vkCmdClearColorImage");
            // KHR swapchain 设备扩展（须设备已启用 VK_KHR_swapchain）
            _createSwapchainKHR = (delegate* unmanaged[Stdcall]<Device, SwapchainCreateInfoKHR*, AllocationCallbacks*, SwapchainKHR*, Result>)vkGetDeviceProcAddr(h, "vkCreateSwapchainKHR");
            _destroySwapchainKHR = (delegate* unmanaged[Stdcall]<Device, SwapchainKHR, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroySwapchainKHR");
            _getSwapchainImagesKHR = (delegate* unmanaged[Stdcall]<Device, SwapchainKHR, uint*, Image*, Result>)vkGetDeviceProcAddr(h, "vkGetSwapchainImagesKHR");
            _acquireNextImageKHR = (delegate* unmanaged[Stdcall]<Device, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result>)vkGetDeviceProcAddr(h, "vkAcquireNextImageKHR");
            _queuePresentKHR = (delegate* unmanaged[Stdcall]<Queue, PresentInfoKHR*, Result>)vkGetDeviceProcAddr(h, "vkQueuePresentKHR");
            // 外部内存/信号量导出（仅 no-airspace 共享表面源使用；未启用对应扩展时为 null，调用方自检）
            _getMemoryWin32HandleKHR = (delegate* unmanaged[Stdcall]<Device, MemoryGetWin32HandleInfoKHR*, void*, Result>)vkGetDeviceProcAddr(h, "vkGetMemoryWin32HandleKHR");
            _getMemoryFdKHR = (delegate* unmanaged[Stdcall]<Device, MemoryGetFdInfoKHR*, int*, Result>)vkGetDeviceProcAddr(h, "vkGetMemoryFdKHR");
            _getMemoryFdPropertiesKHR = (delegate* unmanaged[Stdcall]<Device, ExternalMemoryHandleTypeFlags, int, MemoryFdPropertiesKHR*, Result>)vkGetDeviceProcAddr(h, "vkGetMemoryFdPropertiesKHR");
            _getSemaphoreWin32HandleKHR = (delegate* unmanaged[Stdcall]<Device, SemaphoreGetWin32HandleInfoKHR*, void*, Result>)vkGetDeviceProcAddr(h, "vkGetSemaphoreWin32HandleKHR");
            _getSemaphoreFdKHR = (delegate* unmanaged[Stdcall]<Device, SemaphoreGetFdInfoKHR*, int*, Result>)vkGetDeviceProcAddr(h, "vkGetSemaphoreFdKHR");
            // VK_EXT_metal_objects（仅 Apple / MoltenVK；非 Apple 平台为 null，调用方自检）
            _exportMetalObjectsEXT = (delegate* unmanaged[Stdcall]<Device, ExportMetalObjectsInfoEXT*, void>)vkGetDeviceProcAddr(h, "vkExportMetalObjectsEXT");
            // VK_ANDROID_external_memory_android_hardware_buffer + VK_KHR_sampler_ycbcr_conversion
            // Android AHB 零拷贝导入（MediaCodec 硬解帧 → Vulkan）。非 Android / 未启用扩展时为 null，调用方自检（TryImport 返回 false 回落软解）。
            // vkCreateSamplerYcbcrConversion 为 Vulkan 1.1 core（无后缀）；仅带 KHR 扩展的 1.0 设备上为 KHR 后缀——两个名字都尝试。
            _getAndroidHardwareBufferPropertiesANDROID = (delegate* unmanaged[Stdcall]<Device, nint, AndroidHardwareBufferPropertiesANDROID*, Result>)vkGetDeviceProcAddr(h, "vkGetAndroidHardwareBufferPropertiesANDROID");
            _getMemoryAndroidHardwareBufferANDROID = (delegate* unmanaged[Stdcall]<Device, MemoryGetAndroidHardwareBufferInfoANDROID*, nint*, Result>)vkGetDeviceProcAddr(h, "vkGetMemoryAndroidHardwareBufferANDROID");
            nint ycbcrCreate = vkGetDeviceProcAddr(h, "vkCreateSamplerYcbcrConversion");
            if (ycbcrCreate == nint.Zero)
                ycbcrCreate = vkGetDeviceProcAddr(h, "vkCreateSamplerYcbcrConversionKHR");
            _createSamplerYcbcrConversion = (delegate* unmanaged[Stdcall]<Device, SamplerYcbcrConversionCreateInfo*, AllocationCallbacks*, SamplerYcbcrConversion*, Result>)ycbcrCreate;
            nint ycbcrDestroy = vkGetDeviceProcAddr(h, "vkDestroySamplerYcbcrConversion");
            if (ycbcrDestroy == nint.Zero)
                ycbcrDestroy = vkGetDeviceProcAddr(h, "vkDestroySamplerYcbcrConversionKHR");
            _destroySamplerYcbcrConversion = (delegate* unmanaged[Stdcall]<Device, SamplerYcbcrConversion, AllocationCallbacks*, void>)ycbcrDestroy;
            AssertDevice();
            _deviceReady = true;
        }
    }

    private static unsafe void AssertBootstrap()
    {
        static void Check(string name, nint ptr)
        {
            if (ptr == 0)
                throw new InvalidOperationException(
                    $"VulkanNative 引导解析失败：{name}（vulkan-1 不可用，或 vkGetInstanceProcAddr(NULL, …) 返回 NULL）。请确认 Vulkan 运行时已安装。");
        }
        Check("vkCreateInstance", (nint)_createInstance);
        Check("vkEnumerateInstanceExtensionProperties", (nint)_enumerateInstanceExtensionProperties);
    }

    private static unsafe void AssertInstance()
    {
        static void Check(string name, nint ptr)
        {
            if (ptr == 0)
                throw new InvalidOperationException(
                    $"VulkanNative 实例级解析失败：{name}（请确认 VkInstance 已创建并启用了相应 WSI 扩展，且 vulkan-1 支持该函数）。");
        }
        Check("vkDestroyInstance", (nint)_destroyInstance);
        Check("vkCreateDevice", (nint)_createDevice);
        Check("vkEnumeratePhysicalDevices", (nint)_enumeratePhysicalDevices);
        Check("vkGetPhysicalDeviceProperties", (nint)_getPhysicalDeviceProperties);
        Check("vkGetPhysicalDeviceQueueFamilyProperties", (nint)_getPhysicalDeviceQueueFamilyProperties);
        Check("vkDestroySurfaceKHR", (nint)_destroySurfaceKHR);
        Check("vkGetPhysicalDeviceSurfaceCapabilitiesKHR", (nint)_getPhysicalDeviceSurfaceCapabilitiesKHR);
        Check("vkGetPhysicalDeviceSurfaceFormatsKHR", (nint)_getPhysicalDeviceSurfaceFormatsKHR);
        // WSI 表面创建函数是平台相关的：仅校验当前平台对应的那个；
        // 其余平台对应函数为 null（扩展未启用）属正常，不可硬判失败。
        if (OperatingSystem.IsWindows())
            Check("vkCreateWin32SurfaceKHR", (nint)_createWin32SurfaceKHR);
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            Check("vkCreateMetalSurfaceEXT", (nint)_createMetalSurfaceEXT);
        else if (OperatingSystem.IsAndroid())
            Check("vkCreateAndroidSurfaceKHR", (nint)_createAndroidSurfaceKHR);
        // Linux 不在 InitInstance 解析任何 Surface 创建函数，跳过该校验。
    }

    private static unsafe void AssertDevice()
    {
        static void Check(string name, nint ptr)
        {
            if (ptr == 0)
                throw new InvalidOperationException(
                    $"VulkanNative 设备级解析失败：{name}（请确认 VkDevice 已创建并启用 VK_KHR_swapchain，且 vulkan-1 支持该函数）。");
        }
        Check("vkDestroyDevice", (nint)_destroyDevice);
        Check("vkGetDeviceQueue", (nint)_getDeviceQueue);
        Check("vkDeviceWaitIdle", (nint)_deviceWaitIdle);
        Check("vkCreateCommandPool", (nint)_createCommandPool);
        Check("vkAllocateCommandBuffers", (nint)_allocateCommandBuffers);
        Check("vkBeginCommandBuffer", (nint)_beginCommandBuffer);
        Check("vkEndCommandBuffer", (nint)_endCommandBuffer);
        Check("vkQueueSubmit", (nint)_queueSubmit);
        Check("vkCreateImage", (nint)_createImage);
        Check("vkAllocateMemory", (nint)_allocateMemory);
        Check("vkCreateImageView", (nint)_createImageView);
        Check("vkCreateFramebuffer", (nint)_createFramebuffer);
        Check("vkCreateRenderPass", (nint)_createRenderPass);
        Check("vkCreateShaderModule", (nint)_createShaderModule);
        Check("vkCreateGraphicsPipelines", (nint)_createGraphicsPipelines);
        Check("vkCreateSwapchainKHR", (nint)_createSwapchainKHR);
        Check("vkAcquireNextImageKHR", (nint)_acquireNextImageKHR);
        Check("vkQueuePresentKHR", (nint)_queuePresentKHR);
        Check("vkCmdBeginRenderPass", (nint)_cmdBeginRenderPass);
        Check("vkCmdDraw", (nint)_cmdDraw);
    }
}

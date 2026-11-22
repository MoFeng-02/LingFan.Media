using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// 零反射 Vulkan 原生绑定层（替代 Silk.NET 的 <c>Vk</c> / <c>Khr*</c> 包装与 <c>SilkMarshal</c> 调用）。
/// </summary>
/// <remarks>
/// <para><b>设计目标</b>：彻底消除 Silk.NET 绑定层的反射——不调用 <c>SilkMarshal.DelegateToPtr</c>（IL3050 的来源）、
/// 不调用 <c>Silk.NET.Core.Loader</c>（IL3000/IL3002）、不使用 SharpGen 运行时 vtable 包装（IL2067/IL2072）。
/// NativeAOT 下零 IL2xxx。</para>
/// <para><b>机制</b>：仅两处顶层 <c>[LibraryImport("vulkan-1")]</c> 取得引导符号
/// <c>vkGetInstanceProcAddr</c> / <c>vkGetDeviceProcAddr</c>。各平台 Vulkan loader 的原生库名不同，
/// 经 <c>NativeLibrary.SetDllImportResolver</c> 把顶层 <c>vulkan-1</c> 重定向到平台实际 loader：
/// Windows=<c>vulkan-1.dll</c>（官方名，交回默认解析）；Linux=<c>libvulkan.so.1</c>（回退 <c>libvulkan.so</c>）；
/// Android=<c>libvulkan.so</c>（NDK 随系统提供）；macOS/iOS（MoltenVK）=<c>libvulkan.1.dylib</c> / <c>libMoltenVK.dylib</c>。
/// 解析全部函数指针的其余逻辑仍分三阶段——
/// <see cref="InitBootstrap"/> 经 <c>vkGetInstanceProcAddr(NULL, …)</c> 解析引导子集
/// （<c>vkCreateInstance</c> 等）；<see cref="InitInstance(Instance)"/> 经实例句柄解析实例级函数与 KHR 实例扩展；
/// <see cref="InitDevice(Device)"/> 经设备句柄解析设备级函数与 VK_KHR_swapchain 设备扩展。
/// 函数指针统一 <c>delegate* unmanaged[Stdcall]</c>（Vulkan 的 <c>VKAPI_PTR</c> 在 Windows 即 <c>__stdcall</c>，
/// 在 macOS/iOS（MoltenVK）单一 ABI 下 .NET 同样映射为平台默认调用约定，可正确互操作）。
/// 结构体 / 枚举 / 句柄类型复用 Silk.NET 的纯数据定义（<c>[StructLayout]</c> 值类型 + <c>enum</c>，本身零反射、ABI 精确）。</para>
/// <para><b>为何不能一次全局派发解析全部</b>：Vulkan 规范要求 <c>vkGetInstanceProcAddr(NULL, …)</c> 只保证返回
/// 引导子集；其余函数（含 <c>vkDestroyInstance</c> / <c>vkCreateDevice</c>）经 NULL 派发会静默返回 <c>NULL</c>。
/// 这是跨 loader 兼容的强制要求，Silk.NET / volk 均按此分阶段解析。</para>
/// <para>调用约定：Vulkan 函数指针统一 <c>unmanaged[Stdcall]</c>；z 调用方传参与 Silk.NET 的 <c>Vk</c>/<c>Khr*</c>
/// 方法签名保持一致（ref/out/数组/指针），故现有调用点只需改名。</para>
/// </remarks>
public static unsafe partial class VulkanNative
{
    // 平台库名重定向：macOS/iOS（MoltenVK）上 Vulkan loader 原生库名非 vulkan-1
    static VulkanNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(VulkanNative).Assembly, ResolveVulkanLoader);
    }

    /// <summary>
    /// 把顶层 <c>vulkan-1</c> 引导符号重定向到各平台实际的 Vulkan loader 库（AOT 兼容）。
    /// <list type="bullet">
    /// <item>Windows：<c>vulkan-1.dll</c> 即官方 loader 名，交回默认解析。</item>
    /// <item>Linux：官方 loader 为 <c>libvulkan.so.1</c>（<c>libvulkan.so</c> 多为 -dev 包符号链接，回退尝试）。</item>
    /// <item>Android：Vulkan loader 即 <c>libvulkan.so</c>（NDK 随系统提供），<c>vulkan-1</c> 不可解析。</item>
    /// <item>macOS/iOS：由 MoltenVK 提供 loader，优先 <c>libvulkan.1.dylib</c>、兜底 <c>libMoltenVK.dylib</c>。</item>
    /// </list>
    /// </summary>
    private static nint ResolveVulkanLoader(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // 仅处理 vulkan-1；其余名字交回默认解析（保持 Windows 现状）。
        if (!string.Equals(libraryName, "vulkan-1", StringComparison.Ordinal))
            return nint.Zero;

        // Windows：vulkan-1.dll 为官方 loader 名，默认解析即可。
        if (OperatingSystem.IsWindows())
            return nint.Zero;

        // 单一 h 复用：方法级作用域声明一次，避免各平台块内重复声明同名局部变量（CS0136）。
        nint h;

        // Linux：官方 loader 为 libvulkan.so.1；libvulkan.so 多为 -dev 包符号链接，未必存在，作回退。
        // A2 的 Xlib/Wayland surface 绑定经 vkGetInstanceProcAddr 解析，也依赖此引导符号可用。
        if (OperatingSystem.IsLinux())
        {
            if (NativeLibrary.TryLoad("libvulkan.so.1", assembly, searchPath, out h))
                return h;
            if (NativeLibrary.TryLoad("libvulkan.so", assembly, searchPath, out h))
                return h;
            return nint.Zero;
        }

        // Android：Vulkan loader 即 libvulkan.so（系统随 NDK 提供），vulkan-1 不可解析。
        // A3 在此落地——Android ANativeWindow surface 绑定同样依赖此引导符号。
        if (OperatingSystem.IsAndroid())
        {
            if (NativeLibrary.TryLoad("libvulkan.so", assembly, searchPath, out h))
                return h;
            return nint.Zero;
        }

        // macOS / iOS：Vulkan loader 由 MoltenVK 提供，原生库名非 vulkan-1。
        // 优先标准 loader 命名 libvulkan.1.dylib，兜底 SDK 直打包的 libMoltenVK.dylib。
        if (NativeLibrary.TryLoad("libvulkan.1.dylib", assembly, searchPath, out h))
            return h;
        if (NativeLibrary.TryLoad("libMoltenVK.dylib", assembly, searchPath, out h))
            return h;
        return nint.Zero;
    }

    // 引导符号：仅此两处顶层 P/Invoke（vkGetInstanceProcAddr / vkGetDeviceProcAddr）
    // 其余函数指针全部经这两个引导符号，分别用「实例句柄」/「设备句柄」解析。
    [LibraryImport("vulkan-1", EntryPoint = "vkGetInstanceProcAddr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint vkGetInstanceProcAddrRaw(nint instance, string name);

    [LibraryImport("vulkan-1", EntryPoint = "vkGetDeviceProcAddr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint vkGetDeviceProcAddrRaw(nint device, string name);

    // 1.1/1.2/1.3 核心提升函数的 core→KHR 别名表。部分 Android loader/驱动（如较新 Mali）
    // 对提升函数拒绝按核心名派发（vkGetDeviceProcAddr 返回 NULL，实例 apiVersion 声明也无效），
    // 而来源扩展的 KHR 别名是驱动必须支持的常规派发通道；按规范，别名与核心函数是同一入口点，
    // 来源扩展启用时经别名取得的指针与核心名取得者等价。所有解析点统一经下方两个包装器。
    private static readonly Dictionary<string, string> CoreToKhrAlias = new()
    {
        // 1.1（源自 KHR 扩展）
        ["vkTrimCommandPool"] = "vkTrimCommandPoolKHR",
        ["vkBindBufferMemory2"] = "vkBindBufferMemory2KHR",
        ["vkBindImageMemory2"] = "vkBindImageMemory2KHR",
        ["vkGetImageMemoryRequirements2"] = "vkGetImageMemoryRequirements2KHR",
        ["vkGetBufferMemoryRequirements2"] = "vkGetBufferMemoryRequirements2KHR",
        ["vkGetImageSparseMemoryRequirements2"] = "vkGetImageSparseMemoryRequirements2KHR",
        ["vkCreateSamplerYcbcrConversion"] = "vkCreateSamplerYcbcrConversionKHR",
        ["vkDestroySamplerYcbcrConversion"] = "vkDestroySamplerYcbcrConversionKHR",
        ["vkGetDescriptorSetLayoutSupport"] = "vkGetDescriptorSetLayoutSupportKHR",
        ["vkGetDeviceGroupPeerMemoryFeatures"] = "vkGetDeviceGroupPeerMemoryFeaturesKHR",
        ["vkCmdSetDeviceMask"] = "vkCmdSetDeviceMaskKHR",
        ["vkCmdDispatchBase"] = "vkCmdDispatchBaseKHR",
        ["vkGetPhysicalDeviceFeatures2"] = "vkGetPhysicalDeviceFeatures2KHR",
        ["vkGetPhysicalDeviceProperties2"] = "vkGetPhysicalDeviceProperties2KHR",
        ["vkGetPhysicalDeviceImageFormatProperties2"] = "vkGetPhysicalDeviceImageFormatProperties2KHR",
        ["vkGetPhysicalDeviceQueueFamilyProperties2"] = "vkGetPhysicalDeviceQueueFamilyProperties2KHR",
        ["vkGetPhysicalDeviceMemoryProperties2"] = "vkGetPhysicalDeviceMemoryProperties2KHR",
        ["vkGetPhysicalDeviceSparseImageFormatProperties2"] = "vkGetPhysicalDeviceSparseImageFormatProperties2KHR",
        ["vkGetPhysicalDeviceExternalBufferProperties"] = "vkGetPhysicalDeviceExternalBufferPropertiesKHR",
        ["vkGetPhysicalDeviceExternalSemaphoreProperties"] = "vkGetPhysicalDeviceExternalSemaphorePropertiesKHR",
        ["vkGetPhysicalDeviceExternalFenceProperties"] = "vkGetPhysicalDeviceExternalFencePropertiesKHR",
        ["vkEnumeratePhysicalDeviceGroups"] = "vkEnumeratePhysicalDeviceGroupsKHR",
        // 1.2
        ["vkCmdDrawIndirectCount"] = "vkCmdDrawIndirectCountKHR",
        ["vkCmdDrawIndexedIndirectCount"] = "vkCmdDrawIndexedIndirectCountKHR",
        ["vkCreateRenderPass2"] = "vkCreateRenderPass2KHR",
        ["vkCmdBeginRenderPass2"] = "vkCmdBeginRenderPass2KHR",
        ["vkCmdNextSubpass2"] = "vkCmdNextSubpass2KHR",
        ["vkCmdEndRenderPass2"] = "vkCmdEndRenderPass2KHR",
        ["vkGetSemaphoreCounterValue"] = "vkGetSemaphoreCounterValueKHR",
        ["vkWaitSemaphores"] = "vkWaitSemaphoresKHR",
        ["vkSignalSemaphore"] = "vkSignalSemaphoreKHR",
        ["vkGetBufferDeviceAddress"] = "vkGetBufferDeviceAddressKHR",
        ["vkGetBufferOpaqueCaptureAddress"] = "vkGetBufferOpaqueCaptureAddressKHR",
        ["vkGetDeviceMemoryOpaqueCaptureAddress"] = "vkGetDeviceMemoryOpaqueCaptureAddressKHR",
        // 1.3
        ["vkCmdSetEvent2"] = "vkCmdSetEvent2KHR",
        ["vkCmdResetEvent2"] = "vkCmdResetEvent2KHR",
        ["vkCmdWaitEvents2"] = "vkCmdWaitEvents2KHR",
        ["vkCmdPipelineBarrier2"] = "vkCmdPipelineBarrier2KHR",
        ["vkCmdWriteTimestamp2"] = "vkCmdWriteTimestamp2KHR",
        ["vkQueueSubmit2"] = "vkQueueSubmit2KHR",
        ["vkCmdCopyBuffer2"] = "vkCmdCopyBuffer2KHR",
        ["vkCmdCopyImage2"] = "vkCmdCopyImage2KHR",
        ["vkCmdCopyBufferToImage2"] = "vkCmdCopyBufferToImage2KHR",
        ["vkCmdCopyImageToBuffer2"] = "vkCmdCopyImageToBuffer2KHR",
        ["vkCmdBlitImage2"] = "vkCmdBlitImage2KHR",
        ["vkCmdResolveImage2"] = "vkCmdResolveImage2KHR",
        ["vkGetDeviceBufferMemoryRequirements"] = "vkGetDeviceBufferMemoryRequirementsKHR",
        ["vkGetDeviceImageMemoryRequirements"] = "vkGetDeviceImageMemoryRequirementsKHR",
        ["vkGetDeviceImageSparseMemoryRequirements"] = "vkGetDeviceImageSparseMemoryRequirementsKHR",
    };

    // 派发自适配（参照 FFmpeg 自绑定范式：候选探测→策略切换→绑定记录→幂等加锁）
    // 解析结果按（句柄, 函数名）记忆化；首次发现核心名提升函数被 loader/驱动拒绝而别名可解析时，
    // 切换为「别名优先」策略并记录绑定决策（此后跳过注定失败的核心名查询，一并消除 loader 的
    // invalid call 日志噪音）。
    private static readonly object _procCacheLock = new();
    private static readonly Dictionary<(nint Handle, string Name), nint> _instanceProcCache = new();
    private static readonly Dictionary<(nint Handle, string Name), nint> _deviceProcCache = new();
    private static bool _instanceAliasPreferred;
    private static bool _deviceAliasPreferred;

    private static nint vkGetInstanceProcAddr(nint instance, string name)
    {
        var key = (instance, name);
        lock (_procCacheLock)
        {
            if (_instanceProcCache.TryGetValue(key, out var cached))
                return cached;
            var addr = ResolveInstanceProc(instance, name);
            _instanceProcCache[key] = addr;
            return addr;
        }
    }

    private static nint ResolveInstanceProc(nint instance, string name)
    {
        bool hasAlias = CoreToKhrAlias.ContainsKey(name);
        if (hasAlias && _instanceAliasPreferred)
        {
            var viaAliasFirst = vkGetInstanceProcAddrRaw(instance, CoreToKhrAlias[name]);
            if (viaAliasFirst != 0) return viaAliasFirst;
        }
        var addr = vkGetInstanceProcAddrRaw(instance, name);
        if (addr == 0 && hasAlias)
        {
            var viaAlias = vkGetInstanceProcAddrRaw(instance, CoreToKhrAlias[name]);
            if (viaAlias != 0)
            {
                if (!_instanceAliasPreferred)
                {
                    _instanceAliasPreferred = true;
                    Console.WriteLine("[VULKAN-BIND] 实例级提升函数核心名被当前 loader 拒绝派发，已自适配为 KHR 别名优先" +
                        "（别名与核心函数为同一入口点，来源扩展见 PromotedKhrInstanceExtensions，须启用）。");
                }
                return viaAlias;
            }
        }
        return addr;
    }

    private static nint vkGetDeviceProcAddr(nint device, string name)
    {
        var key = (device, name);
        lock (_procCacheLock)
        {
            if (_deviceProcCache.TryGetValue(key, out var cached))
                return cached;
            var addr = ResolveDeviceProc(device, name);
            _deviceProcCache[key] = addr;
            return addr;
        }
    }

    private static nint ResolveDeviceProc(nint device, string name)
    {
        bool hasAlias = CoreToKhrAlias.ContainsKey(name);
        if (hasAlias && _deviceAliasPreferred)
        {
            var viaAliasFirst = vkGetDeviceProcAddrRaw(device, CoreToKhrAlias[name]);
            if (viaAliasFirst != 0) return viaAliasFirst;
        }
        var addr = vkGetDeviceProcAddrRaw(device, name);
        if (addr == 0 && hasAlias)
        {
            var viaAlias = vkGetDeviceProcAddrRaw(device, CoreToKhrAlias[name]);
            if (viaAlias != 0)
            {
                if (!_deviceAliasPreferred)
                {
                    _deviceAliasPreferred = true;
                    Console.WriteLine("[VULKAN-BIND] 设备级提升函数核心名被当前 loader/驱动拒绝派发，已自适配为 KHR 别名优先" +
                        "（别名与核心函数为同一入口点，来源扩展见 PromotedKhrDeviceExtensions，须启用）。");
                }
                return viaAlias;
            }
        }
        return addr;
    }

    /// <summary>公开 proc-addr 解析（<c>IVulkanInstance</c> 适配器转发用；含核心名→KHR 别名回退）。</summary>
    public static unsafe nint GetInstanceProcAddress(nint instance, string name) => vkGetInstanceProcAddr(instance, name);

    /// <summary>公开 proc-addr 解析（<c>IVulkanInstance</c> 适配器转发用；含核心名→KHR 别名回退）。</summary>
    public static unsafe nint GetDeviceProcAddress(nint device, string name) => vkGetDeviceProcAddr(device, name);

    /// <summary>
    /// 1.1/1.2/1.3 提升函数的 KHR 来源扩展（设备级）。严格 loader/驱动上提升函数经 KHR 别名解析时，
    /// 须启用对应来源扩展。设备创建处按物理设备支持条件启用本清单。
    /// </summary>
    public static readonly string[] PromotedKhrDeviceExtensions =
    [
        "VK_KHR_maintenance1",
        "VK_KHR_bind_memory2",
        "VK_KHR_get_memory_requirements2",
        "VK_KHR_sampler_ycbcr_conversion",
        "VK_KHR_maintenance3",
        "VK_KHR_device_group",
        "VK_KHR_draw_indirect_count",
        "VK_KHR_create_renderpass2",
        "VK_KHR_timeline_semaphore",
        "VK_KHR_buffer_device_address",
        "VK_KHR_synchronization2",
        "VK_KHR_copy_commands2",
        "VK_KHR_maintenance4",
        "VK_KHR_deferred_host_operations",
    ];

    /// <summary>提升函数的 KHR 来源扩展（实例级）：物理设备级 v2 查询函数的别名来源。</summary>
    public static readonly string[] PromotedKhrInstanceExtensions =
    [
        "VK_KHR_get_physical_device_properties2",
        "VK_KHR_device_group_creation",
        "VK_KHR_external_memory_capabilities",
        "VK_KHR_external_semaphore_capabilities",
        "VK_KHR_external_fence_capabilities",
    ];

    private static bool _bootstrapped;
    private static bool _instanceReady;
    private static bool _deviceReady;
    private static readonly object _loadLock = new();

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

    // 公开包装方法按功能域拆分至 VulkanNative.*.cs（部分类，编译期等价）；视频解码扩展见 VulkanNative.Video.cs。
}

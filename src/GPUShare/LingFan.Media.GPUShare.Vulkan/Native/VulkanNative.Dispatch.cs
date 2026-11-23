namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的派发解析段（部分类，由 VulkanNative.cs 按职责拆分，编译期等价）。
// core→KHR 别名自适配、proc 记忆化缓存与提升函数来源扩展清单。
public static unsafe partial class VulkanNative
{
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
}

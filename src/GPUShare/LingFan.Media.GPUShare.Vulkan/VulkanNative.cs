using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// 零反射 Vulkan 原生绑定层（替代 Silk.NET 的 <c>Vk</c> / <c>Khr*</c> 包装与 <c>SilkMarshal</c> 调用）。
/// </summary>
/// <remarks>
/// <para><b>设计目标</b>：彻底消除 Silk.NET 绑定层的反射——不调用 <c>SilkMarshal.DelegateToPtr</c>（IL3050 根因）、
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
    // ── 平台库名重定向：macOS/iOS（MoltenVK）上 Vulkan loader 原生库名非 vulkan-1 ──
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

    // ── 引导符号：仅此两处顶层 P/Invoke（vkGetInstanceProcAddr / vkGetDeviceProcAddr）──
    // 其余函数指针全部经这两个引导符号，分别用「实例句柄」/「设备句柄」解析。
    [LibraryImport("vulkan-1", EntryPoint = "vkGetInstanceProcAddr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint vkGetInstanceProcAddr(nint instance, string name);

    [LibraryImport("vulkan-1", EntryPoint = "vkGetDeviceProcAddr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint vkGetDeviceProcAddr(nint device, string name);

    private static bool _bootstrapped;
    private static bool _instanceReady;
    private static bool _deviceReady;
    private static readonly object _loadLock = new();

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
            // ── KHR WSI 实例扩展（须实例已启用对应扩展）──
            _createWin32SurfaceKHR = (delegate* unmanaged[Stdcall]<Instance, Win32SurfaceCreateInfoKHR*, AllocationCallbacks*, SurfaceKHR*, Result>)vkGetInstanceProcAddr(h, "vkCreateWin32SurfaceKHR");
            _createAndroidSurfaceKHR = (delegate* unmanaged[Stdcall]<Instance, AndroidSurfaceCreateInfoKHR*, AllocationCallbacks*, SurfaceKHR*, Result>)vkGetInstanceProcAddr(h, "vkCreateAndroidSurfaceKHR");
            // ── VK_EXT_metal_surface（Apple / MoltenVK；Silk.NET 静态包装不含 vkCreateMetalSurfaceEXT，须运行时经 vkGetInstanceProcAddr 解析）──
            _createMetalSurfaceEXT = (delegate* unmanaged[Stdcall]<Instance, MetalSurfaceCreateInfoEXT*, AllocationCallbacks*, SurfaceKHR*, Result>)vkGetInstanceProcAddr(h, "vkCreateMetalSurfaceEXT");
            // ── KHR WSI Linux 扩展（X11 / Wayland；须实例已启用对应扩展，未启用则为 null，调用方自检）──
            _createXlibSurfaceKHR = (delegate* unmanaged[Stdcall]<Instance, XlibSurfaceCreateInfoKHR*, AllocationCallbacks*, SurfaceKHR*, Result>)vkGetInstanceProcAddr(h, "vkCreateXlibSurfaceKHR");
            _createWaylandSurfaceKHR = (delegate* unmanaged[Stdcall]<Instance, WaylandSurfaceCreateInfoKHR*, AllocationCallbacks*, SurfaceKHR*, Result>)vkGetInstanceProcAddr(h, "vkCreateWaylandSurfaceKHR");
            _destroySurfaceKHR = (delegate* unmanaged[Stdcall]<Instance, SurfaceKHR, AllocationCallbacks*, void>)vkGetInstanceProcAddr(h, "vkDestroySurfaceKHR");
            _getPhysicalDeviceSurfaceCapabilitiesKHR = (delegate* unmanaged[Stdcall]<PhysicalDevice, SurfaceKHR, SurfaceCapabilitiesKHR*, Result>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            _getPhysicalDeviceSurfaceFormatsKHR = (delegate* unmanaged[Stdcall]<PhysicalDevice, SurfaceKHR, uint*, SurfaceFormatKHR*, Result>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceSurfaceFormatsKHR");
            // ── 设备能力查询（UUID/LUID 对齐、设备扩展枚举）──
            _getPhysicalDeviceProperties2 = (delegate* unmanaged[Stdcall]<PhysicalDevice, PhysicalDeviceProperties2*, Result>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceProperties2");
            // 实例级设备特性查询（Vulkan 1.1 core；首参 VkPhysicalDevice 须 vkGetInstanceProcAddr 解析）：
            // Android AHB 零拷贝须先经它查询 samplerYcbcrConversion 特性，仅在支持时才于设备创建处链入特性结构。
            _getPhysicalDeviceFeatures2 = (delegate* unmanaged[Stdcall]<PhysicalDevice, PhysicalDeviceFeatures2*, Result>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceFeatures2");
            _enumerateDeviceExtensionProperties = (delegate* unmanaged[Stdcall]<PhysicalDevice, byte*, uint*, ExtensionProperties*, Result>)vkGetInstanceProcAddr(h, "vkEnumerateDeviceExtensionProperties");
            // ── 物理设备级视频能力查询（VK_KHR_video_queue 实例扩展函数，须经 vkGetInstanceProcAddr）──
            // 注意：vkGetPhysicalDeviceVideoCapabilitiesKHR 首参为 VkPhysicalDevice，属实例级函数；
            // 经 vkGetDeviceProcAddr 解析必返回 NULL（设备派发表不含物理设备级函数），此坑曾导致硬解初始化崩溃。
            _getPhysicalDeviceVideoCapabilitiesKHR = (delegate* unmanaged[Stdcall]<PhysicalDevice, VideoProfileInfoKHR*, VideoCapabilitiesKHR*, Result>)vkGetInstanceProcAddr(h, "vkGetPhysicalDeviceVideoCapabilitiesKHR");
            // 实例级视频格式属性查询（VK_KHR_video_queue）：首参 VkPhysicalDevice，须 vkGetInstanceProcAddr 解析。
            // 解码 DPB / 码流图带 VIDEO_DECODE_* usage 时，其 usage/flags 必须经本查询取返回值（VUID-06811 铁律），
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
            // ── Fence（no-airspace 共享表面源的有限超时 ping-pong，与 D3D11 的 16ms keyed mutex 超时对称）──
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
            // ── KHR swapchain 设备扩展（须设备已启用 VK_KHR_swapchain）──
            _createSwapchainKHR = (delegate* unmanaged[Stdcall]<Device, SwapchainCreateInfoKHR*, AllocationCallbacks*, SwapchainKHR*, Result>)vkGetDeviceProcAddr(h, "vkCreateSwapchainKHR");
            _destroySwapchainKHR = (delegate* unmanaged[Stdcall]<Device, SwapchainKHR, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroySwapchainKHR");
            _getSwapchainImagesKHR = (delegate* unmanaged[Stdcall]<Device, SwapchainKHR, uint*, Image*, Result>)vkGetDeviceProcAddr(h, "vkGetSwapchainImagesKHR");
            _acquireNextImageKHR = (delegate* unmanaged[Stdcall]<Device, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result>)vkGetDeviceProcAddr(h, "vkAcquireNextImageKHR");
            _queuePresentKHR = (delegate* unmanaged[Stdcall]<Queue, PresentInfoKHR*, Result>)vkGetDeviceProcAddr(h, "vkQueuePresentKHR");
            // ── 外部内存/信号量导出（仅 no-airspace 共享表面源使用；未启用对应扩展时为 null，调用方自检）──
            _getMemoryWin32HandleKHR = (delegate* unmanaged[Stdcall]<Device, MemoryGetWin32HandleInfoKHR*, void*, Result>)vkGetDeviceProcAddr(h, "vkGetMemoryWin32HandleKHR");
            _getMemoryFdKHR = (delegate* unmanaged[Stdcall]<Device, MemoryGetFdInfoKHR*, int*, Result>)vkGetDeviceProcAddr(h, "vkGetMemoryFdKHR");
            _getSemaphoreWin32HandleKHR = (delegate* unmanaged[Stdcall]<Device, SemaphoreGetWin32HandleInfoKHR*, void*, Result>)vkGetDeviceProcAddr(h, "vkGetSemaphoreWin32HandleKHR");
            _getSemaphoreFdKHR = (delegate* unmanaged[Stdcall]<Device, SemaphoreGetFdInfoKHR*, int*, Result>)vkGetDeviceProcAddr(h, "vkGetSemaphoreFdKHR");
            // ── VK_EXT_metal_objects（仅 Apple / MoltenVK；非 Apple 平台为 null，调用方自检）──
            _exportMetalObjectsEXT = (delegate* unmanaged[Stdcall]<Device, ExportMetalObjectsInfoEXT*, void>)vkGetDeviceProcAddr(h, "vkExportMetalObjectsEXT");
            // ── VK_ANDROID_external_memory_android_hardware_buffer + VK_KHR_sampler_ycbcr_conversion ──
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

    // ── 函数指针字段 ──
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
    private static unsafe delegate* unmanaged[Stdcall]<Device, SemaphoreGetWin32HandleInfoKHR*, void*, Result> _getSemaphoreWin32HandleKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, SemaphoreGetFdInfoKHR*, int*, Result> _getSemaphoreFdKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, ExportMetalObjectsInfoEXT*, void> _exportMetalObjectsEXT;
    private static unsafe delegate* unmanaged[Stdcall]<Device, nint, AndroidHardwareBufferPropertiesANDROID*, Result> _getAndroidHardwareBufferPropertiesANDROID;
    private static unsafe delegate* unmanaged[Stdcall]<Device, MemoryGetAndroidHardwareBufferInfoANDROID*, nint*, Result> _getMemoryAndroidHardwareBufferANDROID;
    private static unsafe delegate* unmanaged[Stdcall]<Device, SamplerYcbcrConversionCreateInfo*, AllocationCallbacks*, SamplerYcbcrConversion*, Result> _createSamplerYcbcrConversion;
    private static unsafe delegate* unmanaged[Stdcall]<Device, SamplerYcbcrConversion, AllocationCallbacks*, void> _destroySamplerYcbcrConversion;

    // ── 包装方法（签名对齐 Silk.NET Vk / Khr*，调用点仅改名）──

    public static unsafe Result CreateInstance(ref InstanceCreateInfo pCreateInfo, AllocationCallbacks* pAllocator, out Instance pInstance)
    {
        fixed (InstanceCreateInfo* p = &pCreateInfo)
        {
            Instance tmp;
            var r = _createInstance(p, pAllocator, &tmp);
            pInstance = tmp;
            return r;
        }
    }

    public static unsafe void DestroyInstance(Instance instance, AllocationCallbacks* pAllocator) => _destroyInstance(instance, pAllocator);

    public static unsafe Result EnumeratePhysicalDevices(Instance instance, ref uint pPhysicalDeviceCount, PhysicalDevice* pPhysicalDevices)
    {
        fixed (uint* p = &pPhysicalDeviceCount)
            return _enumeratePhysicalDevices(instance, p, pPhysicalDevices);
    }

    public static unsafe void GetPhysicalDeviceProperties(PhysicalDevice physicalDevice, PhysicalDeviceProperties* pProperties)
        => _getPhysicalDeviceProperties(physicalDevice, pProperties);

    public static unsafe void GetPhysicalDeviceMemoryProperties(PhysicalDevice physicalDevice, PhysicalDeviceMemoryProperties* pMemoryProperties)
        => _getPhysicalDeviceMemoryProperties(physicalDevice, pMemoryProperties);

    public static unsafe void GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice physicalDevice, ref uint pQueueFamilyPropertyCount, QueueFamilyProperties* pQueueFamilyProperties)
    {
        fixed (uint* p = &pQueueFamilyPropertyCount)
            _getPhysicalDeviceQueueFamilyProperties(physicalDevice, p, pQueueFamilyProperties);
    }

    public static unsafe Result CreateDevice(PhysicalDevice physicalDevice, ref DeviceCreateInfo pCreateInfo, AllocationCallbacks* pAllocator, out Device pDevice)
    {
        fixed (DeviceCreateInfo* p = &pCreateInfo)
        {
            Device tmp;
            var r = _createDevice(physicalDevice, p, pAllocator, &tmp);
            pDevice = tmp;
            return r;
        }
    }

    public static unsafe void DestroyDevice(Device device, AllocationCallbacks* pAllocator) => _destroyDevice(device, pAllocator);

    public static unsafe void GetDeviceQueue(Device device, uint queueFamilyIndex, uint queueIndex, out Queue pQueue)
    {
        Queue tmp;
        _getDeviceQueue(device, queueFamilyIndex, queueIndex, &tmp);
        pQueue = tmp;
    }

    public static unsafe Result DeviceWaitIdle(Device device) => _deviceWaitIdle(device);

    public static unsafe Result EnumerateInstanceExtensionProperties(byte* pLayerName, uint* pPropertyCount, ExtensionProperties* pProperties)
        => _enumerateInstanceExtensionProperties(pLayerName, pPropertyCount, pProperties);

    public static unsafe Result CreateCommandPool(Device device, ref CommandPoolCreateInfo pCreateInfo, AllocationCallbacks* pAllocator, out CommandPool pCommandPool)
    {
        fixed (CommandPoolCreateInfo* p = &pCreateInfo)
        {
            CommandPool tmp;
            var r = _createCommandPool(device, p, pAllocator, &tmp);
            pCommandPool = tmp;
            return r;
        }
    }

    public static unsafe void DestroyCommandPool(Device device, CommandPool commandPool, AllocationCallbacks* pAllocator)
        => _destroyCommandPool(device, commandPool, pAllocator);

    public static unsafe Result AllocateCommandBuffers(Device device, ref CommandBufferAllocateInfo pAllocateInfo, CommandBuffer* pCommandBuffers)
    {
        fixed (CommandBufferAllocateInfo* p = &pAllocateInfo)
            return _allocateCommandBuffers(device, p, pCommandBuffers);
    }

    public static unsafe Result AllocateCommandBuffers(Device device, CommandBufferAllocateInfo* pAllocateInfo, CommandBuffer* pCommandBuffers)
        => _allocateCommandBuffers(device, pAllocateInfo, pCommandBuffers);

    public static unsafe Result BeginCommandBuffer(CommandBuffer commandBuffer, ref CommandBufferBeginInfo pBeginInfo)
    {
        fixed (CommandBufferBeginInfo* p = &pBeginInfo)
            return _beginCommandBuffer(commandBuffer, p);
    }

    public static unsafe Result EndCommandBuffer(CommandBuffer commandBuffer) => _endCommandBuffer(commandBuffer);

    public static unsafe Result ResetCommandBuffer(CommandBuffer commandBuffer, CommandBufferResetFlags flags)
        => _resetCommandBuffer(commandBuffer, flags);

    public static unsafe Result QueueSubmit(Queue queue, uint submitCount, SubmitInfo* pSubmits, nint fence)
        => _queueSubmit(queue, submitCount, pSubmits, fence);

    public static unsafe Result QueueWaitIdle(Queue queue) => _queueWaitIdle(queue);

    public static unsafe Result CreateSemaphore(Device device, ref SemaphoreCreateInfo pCreateInfo, AllocationCallbacks* pAllocator, out Semaphore pSemaphore)
    {
        fixed (SemaphoreCreateInfo* p = &pCreateInfo)
        {
            Semaphore tmp;
            var r = _createSemaphore(device, p, pAllocator, &tmp);
            pSemaphore = tmp;
            return r;
        }
    }

    public static unsafe void DestroySemaphore(Device device, Semaphore semaphore, AllocationCallbacks* pAllocator)
        => _destroySemaphore(device, semaphore, pAllocator);

    public static unsafe Result CreateFence(Device device, FenceCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out Fence pFence)
    {
        Fence tmp;
        var r = _createFence(device, pCreateInfo, pAllocator, &tmp);
        pFence = tmp;
        return r;
    }

    public static unsafe void DestroyFence(Device device, Fence fence, AllocationCallbacks* pAllocator)
        => _destroyFence(device, fence, pAllocator);

    public static unsafe Result WaitForFences(Device device, uint fenceCount, Fence* pFences, uint waitAll, ulong timeout)
        => _waitForFences(device, fenceCount, pFences, waitAll, timeout);

    public static unsafe Result ResetFences(Device device, uint fenceCount, Fence* pFences)
        => _resetFences(device, fenceCount, pFences);

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

    public static unsafe Result CreateImage(Device device, ImageCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out Image pImage)
    {
        Image tmp;
        var r = _createImage(device, pCreateInfo, pAllocator, &tmp);
        pImage = tmp;
        return r;
    }

    public static unsafe void DestroyImage(Device device, Image image, AllocationCallbacks* pAllocator) => _destroyImage(device, image, pAllocator);

    public static unsafe void GetImageMemoryRequirements(Device device, Image image, MemoryRequirements* pMemoryRequirements)
        => _getImageMemoryRequirements(device, image, pMemoryRequirements);

    /// <summary>
    /// 外部内存导入纹理的需求查询必须用 v2（带回 <see cref="MemoryDedicatedRequirements"/>）取权威 size / memoryTypeBits。
    /// Vulkan 1.1+ 为 core；若设备未提供（1.0 且未启用 VK_KHR_get_memory_requirements2）则为 null，
    /// 调用方应回退到 <see cref="GetImageMemoryRequirements"/>。
    /// </summary>
    public static bool HasImageMemoryRequirements2 => _getImageMemoryRequirements2 != null;

    public static unsafe void GetImageMemoryRequirements2(Device device, ImageMemoryRequirementsInfo2* pInfo, MemoryRequirements2* pMemoryRequirements)
    {
        if (_getImageMemoryRequirements2 == null)
            throw new InvalidOperationException("vkGetImageMemoryRequirements2 不可用（请确认 Vulkan 1.1+ 或已启用 VK_KHR_get_memory_requirements2）。");
        _getImageMemoryRequirements2(device, pInfo, pMemoryRequirements);
    }

    public static unsafe Result AllocateMemory(Device device, ref MemoryAllocateInfo pAllocateInfo, AllocationCallbacks* pAllocator, out DeviceMemory pMemory)
    {
        fixed (MemoryAllocateInfo* p = &pAllocateInfo)
        {
            DeviceMemory tmp;
            var r = _allocateMemory(device, p, pAllocator, &tmp);
            pMemory = tmp;
            return r;
        }
    }

    public static unsafe Result AllocateMemory(Device device, MemoryAllocateInfo* pAllocateInfo, AllocationCallbacks* pAllocator, out DeviceMemory pMemory)
    {
        DeviceMemory tmp;
        var r = _allocateMemory(device, pAllocateInfo, pAllocator, &tmp);
        pMemory = tmp;
        return r;
    }

    public static unsafe void FreeMemory(Device device, DeviceMemory memory, AllocationCallbacks* pAllocator) => _freeMemory(device, memory, pAllocator);

    public static unsafe Result BindImageMemory(Device device, Image image, DeviceMemory memory, ulong memoryOffset)
        => _bindImageMemory(device, image, memory, memoryOffset);

    public static unsafe Result CreateImageView(Device device, ImageViewCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out ImageView pView)
    {
        ImageView tmp;
        var r = _createImageView(device, pCreateInfo, pAllocator, &tmp);
        pView = tmp;
        return r;
    }

    public static unsafe void DestroyImageView(Device device, ImageView imageView, AllocationCallbacks* pAllocator)
        => _destroyImageView(device, imageView, pAllocator);

    /// <summary>
    /// 经 <c>VK_EXT_metal_objects</c> 把底层 Metal 对象（IOSurface / MTLSharedEvent 等）从 Vulkan 对象导出。
    /// <c>pMetalObjectsInfo-&gt;pNext</c> 链承载具体的导出请求结构体（<c>ExportMetalIOSurfaceInfoEXT</c> /
    /// <c>ExportMetalSharedEventInfoEXT</c>），对应输出字段由本调用填充。
    /// </summary>
    /// <remarks>仅 Apple / MoltenVK 启用 <c>VK_EXT_metal_objects</c> 后可用；其它平台该指针为 null。</remarks>
    public static unsafe void ExportMetalObjectsEXT(Device device, ExportMetalObjectsInfoEXT* pMetalObjectsInfo)
    {
        if (_exportMetalObjectsEXT == null)
            throw new InvalidOperationException("vkExportMetalObjectsEXT 不可用（VK_EXT_metal_objects 未启用或不支持）。");
        _exportMetalObjectsEXT(device, pMetalObjectsInfo);
    }

    // ── Android AHB 零拷贝导入（VK_ANDROID_external_memory_android_hardware_buffer + sampler YCbCr 转换）──

    /// <summary>是否已解析 <c>vkGetAndroidHardwareBufferPropertiesANDROID</c>（Android 且设备已启用 AHB 扩展时为真）。</summary>
    public static bool HasAndroidHardwareBufferProperties => _getAndroidHardwareBufferPropertiesANDROID != null;

    // samplerYcbcrConversion 特性是否在设备创建期启用（由渲染器工厂探测后经 InitDevice 传入）。
    // 【关键】函数指针可解析 ≠ 特性已启用：vkCreateSamplerYcbcrConversion 恒可解析（1.1 core 无后缀），
    // 但特性未启用时使用 YCbCr 采样器属规范违规 —— 驱动 UB 实测表现为 SIGBUS BUS_ADRALN（iQOO10/Adreno730）。
    private static bool _samplerYcbcrFeatureEnabled;

    /// <summary>samplerYcbcrConversion 是否可用：函数已解析<b>且</b>特性已在设备创建期启用（双判据）。</summary>
    public static bool HasSamplerYcbcrConversion => _createSamplerYcbcrConversion != null && _samplerYcbcrFeatureEnabled;

    /// <summary>
    /// 查询 AHardwareBuffer 的内存属性（allocationSize / memoryTypeBits）与格式属性（externalFormat / 采样器建议值）。
    /// <paramref name="buffer"/> 为 AHardwareBuffer 指针（借用，不取得所有权）。
    /// </summary>
    public static unsafe Result GetAndroidHardwareBufferPropertiesANDROID(Device device, nint buffer, AndroidHardwareBufferPropertiesANDROID* pProperties)
    {
        if (_getAndroidHardwareBufferPropertiesANDROID == null)
            throw new InvalidOperationException("vkGetAndroidHardwareBufferPropertiesANDROID 不可用（非 Android 或未启用 VK_ANDROID_external_memory_android_hardware_buffer）。");
        return _getAndroidHardwareBufferPropertiesANDROID(device, buffer, pProperties);
    }

    /// <summary>
    /// 把已分配（且带 <c>VK_ANDROID_external_memory_android_hardware_buffer</c> 导出标志）的 Vulkan 设备内存
    /// 导出为 <c>AHardwareBuffer</c> 指针（消费侧经 <c>ICompositionGpuInterop</c> 直接导入采样）。
    /// </summary>
    /// <param name="device">Vulkan 逻辑设备。</param>
    /// <param name="pInfo">导出信息（含目标 <see cref="DeviceMemory"/>）。</param>
    /// <param name="pBuffer">导出的 AHardwareBuffer 指针（由调用方拥有，须由 Android NDK <c>AHardwareBuffer_release</c> 释放）。</param>
    public static unsafe Result GetMemoryAndroidHardwareBufferANDROID(Device device, MemoryGetAndroidHardwareBufferInfoANDROID* pInfo, out nint pBuffer)
    {
        if (_getMemoryAndroidHardwareBufferANDROID == null)
            throw new InvalidOperationException("vkGetMemoryAndroidHardwareBufferANDROID 不可用（非 Android 或未启用 VK_ANDROID_external_memory_android_hardware_buffer）。");
        nint local;
        Result r = _getMemoryAndroidHardwareBufferANDROID(device, pInfo, &local);
        pBuffer = local;
        return r;
    }

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

    /// <summary>
    /// 查询物理设备特性（Vulkan 1.1 core 实例级函数；<paramref name="pFeatures"/> 的 pNext 可链
    /// VkPhysicalDeviceSamplerYcbcrConversionFeatures 等扩展特性结构）。
    /// </summary>
    public static unsafe Result GetPhysicalDeviceFeatures2(PhysicalDevice physicalDevice, PhysicalDeviceFeatures2* pFeatures)
    {
        if (_getPhysicalDeviceFeatures2 == null)
            throw new InvalidOperationException("vkGetPhysicalDeviceFeatures2 不可用（Vulkan 1.0 设备且无 KHR 扩展）。");
        return _getPhysicalDeviceFeatures2(physicalDevice, pFeatures);
    }

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

    public static unsafe void DestroyBuffer(Device device, Buffer buffer, AllocationCallbacks* pAllocator) => _destroyBuffer(device, buffer, pAllocator);

    public static unsafe void GetBufferMemoryRequirements(Device device, Buffer buffer, MemoryRequirements* pMemoryRequirements)
        => _getBufferMemoryRequirements(device, buffer, pMemoryRequirements);

    public static unsafe Result BindBufferMemory(Device device, Buffer buffer, DeviceMemory memory, ulong memoryOffset)
        => _bindBufferMemory(device, buffer, memory, memoryOffset);

    public static unsafe Result MapMemory(Device device, DeviceMemory memory, ulong offset, ulong size, uint flags, void** ppData)
        => _mapMemory(device, memory, offset, size, flags, ppData);

    public static unsafe void UnmapMemory(Device device, DeviceMemory memory) => _unmapMemory(device, memory);

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

    public static unsafe Result CreateSampler(Device device, SamplerCreateInfo* pCreateInfo, AllocationCallbacks* pAllocator, out Sampler pSampler)
    {
        Sampler tmp;
        var r = _createSampler(device, pCreateInfo, pAllocator, &tmp);
        pSampler = tmp;
        return r;
    }

    public static unsafe void DestroySampler(Device device, Sampler sampler, AllocationCallbacks* pAllocator)
        => _destroySampler(device, sampler, pAllocator);

    public static unsafe void CmdBeginRenderPass(CommandBuffer commandBuffer, RenderPassBeginInfo* pRenderPassBegin, SubpassContents contents)
        => _cmdBeginRenderPass(commandBuffer, pRenderPassBegin, contents);

    public static unsafe void CmdEndRenderPass(CommandBuffer commandBuffer) => _cmdEndRenderPass(commandBuffer);

    public static unsafe void CmdBindPipeline(CommandBuffer commandBuffer, PipelineBindPoint pipelineBindPoint, Pipeline pipeline)
        => _cmdBindPipeline(commandBuffer, pipelineBindPoint, pipeline);

    public static unsafe void CmdSetViewport(CommandBuffer commandBuffer, uint firstViewport, uint viewportCount, Viewport* pViewports)
        => _cmdSetViewport(commandBuffer, firstViewport, viewportCount, pViewports);

    public static unsafe void CmdSetScissor(CommandBuffer commandBuffer, uint firstScissor, uint scissorCount, Rect2D* pScissors)
        => _cmdSetScissor(commandBuffer, firstScissor, scissorCount, pScissors);

    public static unsafe void CmdPushConstants(CommandBuffer commandBuffer, PipelineLayout layout, ShaderStageFlags stageFlags, uint offset, uint size, void* pValues)
        => _cmdPushConstants(commandBuffer, layout, stageFlags, offset, size, pValues);

    public static unsafe void CmdBindDescriptorSets(CommandBuffer commandBuffer, PipelineBindPoint pipelineBindPoint, PipelineLayout layout, uint firstSet, uint descriptorSetCount, DescriptorSet* pDescriptorSets, uint dynamicOffsetCount, uint* pDynamicOffsets)
        => _cmdBindDescriptorSets(commandBuffer, pipelineBindPoint, layout, firstSet, descriptorSetCount, pDescriptorSets, dynamicOffsetCount, pDynamicOffsets);

    public static unsafe void CmdDraw(CommandBuffer commandBuffer, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
        => _cmdDraw(commandBuffer, vertexCount, instanceCount, firstVertex, firstInstance);

    public static unsafe void CmdPipelineBarrier(CommandBuffer commandBuffer, PipelineStageFlags srcStageMask, PipelineStageFlags dstStageMask, uint dependencyFlags, uint memoryBarrierCount, MemoryBarrier* pMemoryBarriers, uint bufferMemoryBarrierCount, BufferMemoryBarrier* pBufferMemoryBarriers, uint imageMemoryBarrierCount, ImageMemoryBarrier* pImageMemoryBarriers)
        => _cmdPipelineBarrier(commandBuffer, srcStageMask, dstStageMask, dependencyFlags, memoryBarrierCount, pMemoryBarriers, bufferMemoryBarrierCount, pBufferMemoryBarriers, imageMemoryBarrierCount, pImageMemoryBarriers);

    public static unsafe void CmdCopyBufferToImage(CommandBuffer commandBuffer, Buffer srcBuffer, Image dstImage, ImageLayout dstImageLayout, uint regionCount, BufferImageCopy* pRegions)
        => _cmdCopyBufferToImage(commandBuffer, srcBuffer, dstImage, dstImageLayout, regionCount, pRegions);

    public static unsafe void CmdCopyImageToBuffer(CommandBuffer commandBuffer, Image srcImage, ImageLayout srcImageLayout, Buffer dstBuffer, uint regionCount, BufferImageCopy* pRegions)
        => _cmdCopyImageToBuffer(commandBuffer, srcImage, srcImageLayout, dstBuffer, regionCount, pRegions);

    public static unsafe void CmdCopyImage(CommandBuffer commandBuffer, Image srcImage, ImageLayout srcImageLayout, Image dstImage, ImageLayout dstImageLayout, uint regionCount, ImageCopy* pRegions)
        => _cmdCopyImage(commandBuffer, srcImage, srcImageLayout, dstImage, dstImageLayout, regionCount, pRegions);

    public static unsafe void CmdBlitImage(CommandBuffer commandBuffer, Image srcImage, ImageLayout srcImageLayout, Image dstImage, ImageLayout dstImageLayout, uint regionCount, ImageBlit* pRegions, Filter filter)
        => _cmdBlitImage(commandBuffer, srcImage, srcImageLayout, dstImage, dstImageLayout, regionCount, pRegions, filter);

    public static unsafe void CmdClearColorImage(CommandBuffer commandBuffer, Image image, ImageLayout imageLayout, ClearColorValue* pColor, uint rangeCount, ImageSubresourceRange* pRanges)
        => _cmdClearColorImage(commandBuffer, image, imageLayout, pColor, rangeCount, pRanges);

    // ── KHR WSI 扩展（数组重载以对齐 Silk.NET Khr* 调用点）──

    public static unsafe Result CreateWin32SurfaceKHR(Instance instance, ref Win32SurfaceCreateInfoKHR pCreateInfo, AllocationCallbacks* pAllocator, out SurfaceKHR pSurface)
    {
        fixed (Win32SurfaceCreateInfoKHR* p = &pCreateInfo)
        {
            SurfaceKHR tmp;
            var r = _createWin32SurfaceKHR(instance, p, pAllocator, &tmp);
            pSurface = tmp;
            return r;
        }
    }

    public static unsafe Result CreateAndroidSurfaceKHR(Instance instance, ref AndroidSurfaceCreateInfoKHR pCreateInfo, AllocationCallbacks* pAllocator, out SurfaceKHR pSurface)
    {
        fixed (AndroidSurfaceCreateInfoKHR* p = &pCreateInfo)
        {
            SurfaceKHR tmp;
            var r = _createAndroidSurfaceKHR(instance, p, pAllocator, &tmp);
            pSurface = tmp;
            return r;
        }
    }

    // ── VK_EXT_metal_surface（Apple / MoltenVK）：Silk.NET 静态包装不含 vkCreateMetalSurfaceEXT，
    //    经 vkGetInstanceProcAddr 运行时解析（见 InitInstance）。PLayer 指向宿主提供的 CAMetalLayer*。 ──
    public static unsafe Result CreateMetalSurfaceEXT(Instance instance, ref MetalSurfaceCreateInfoEXT pCreateInfo, AllocationCallbacks* pAllocator, out SurfaceKHR pSurface)
    {
        if (_createMetalSurfaceEXT == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCreateMetalSurfaceEXT（请确认已在 Apple 平台启用 VK_EXT_metal_surface 扩展）。");
        fixed (MetalSurfaceCreateInfoEXT* p = &pCreateInfo)
        {
            SurfaceKHR tmp;
            var r = _createMetalSurfaceEXT(instance, p, pAllocator, &tmp);
            pSurface = tmp;
            return r;
        }
    }

    // ── KHR WSI Linux 扩展（X11 / Wayland）──

    public static unsafe Result CreateXlibSurfaceKHR(Instance instance, ref XlibSurfaceCreateInfoKHR pCreateInfo, AllocationCallbacks* pAllocator, out SurfaceKHR pSurface)
    {
        if (_createXlibSurfaceKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCreateXlibSurfaceKHR（请确认已启用 VK_KHR_xlib_surface 扩展）。");
        fixed (XlibSurfaceCreateInfoKHR* p = &pCreateInfo)
        {
            SurfaceKHR tmp;
            var r = _createXlibSurfaceKHR(instance, p, pAllocator, &tmp);
            pSurface = tmp;
            return r;
        }
    }

    public static unsafe Result CreateWaylandSurfaceKHR(Instance instance, ref WaylandSurfaceCreateInfoKHR pCreateInfo, AllocationCallbacks* pAllocator, out SurfaceKHR pSurface)
    {
        if (_createWaylandSurfaceKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCreateWaylandSurfaceKHR（请确认已启用 VK_KHR_wayland_surface 扩展）。");
        fixed (WaylandSurfaceCreateInfoKHR* p = &pCreateInfo)
        {
            SurfaceKHR tmp;
            var r = _createWaylandSurfaceKHR(instance, p, pAllocator, &tmp);
            pSurface = tmp;
            return r;
        }
    }

    public static unsafe void DestroySurfaceKHR(Instance instance, SurfaceKHR surface, AllocationCallbacks* pAllocator)
        => _destroySurfaceKHR(instance, surface, pAllocator);

    public static unsafe Result GetPhysicalDeviceSurfaceCapabilitiesKHR(PhysicalDevice physicalDevice, SurfaceKHR surface, SurfaceCapabilitiesKHR[] pSurfaceCapabilities)
    {
        fixed (SurfaceCapabilitiesKHR* p = pSurfaceCapabilities)
            return _getPhysicalDeviceSurfaceCapabilitiesKHR(physicalDevice, surface, p);
    }

    public static unsafe Result GetPhysicalDeviceSurfaceFormatsKHR(PhysicalDevice physicalDevice, SurfaceKHR surface, ref uint pSurfaceFormatCount, SurfaceFormatKHR[] pSurfaceFormats)
    {
        fixed (uint* p = &pSurfaceFormatCount)
        fixed (SurfaceFormatKHR* f = pSurfaceFormats)
            return _getPhysicalDeviceSurfaceFormatsKHR(physicalDevice, surface, p, f);
    }

    public static unsafe Result GetPhysicalDeviceSurfaceFormatsKHR(PhysicalDevice physicalDevice, SurfaceKHR surface, ref uint pSurfaceFormatCount, SurfaceFormatKHR* pSurfaceFormats)
    {
        fixed (uint* p = &pSurfaceFormatCount)
            return _getPhysicalDeviceSurfaceFormatsKHR(physicalDevice, surface, p, pSurfaceFormats);
    }

    public static unsafe Result CreateSwapchainKHR(Device device, ref SwapchainCreateInfoKHR pCreateInfo, AllocationCallbacks* pAllocator, out SwapchainKHR pSwapchain)
    {
        fixed (SwapchainCreateInfoKHR* p = &pCreateInfo)
        {
            SwapchainKHR tmp;
            var r = _createSwapchainKHR(device, p, pAllocator, &tmp);
            pSwapchain = tmp;
            return r;
        }
    }

    public static unsafe void DestroySwapchainKHR(Device device, SwapchainKHR swapchain, AllocationCallbacks* pAllocator)
        => _destroySwapchainKHR(device, swapchain, pAllocator);

    public static unsafe Result GetSwapchainImagesKHR(Device device, SwapchainKHR swapchain, ref uint pSwapchainImageCount, Image[] pSwapchainImages)
    {
        fixed (uint* p = &pSwapchainImageCount)
        fixed (Image* imgs = pSwapchainImages)
            return _getSwapchainImagesKHR(device, swapchain, p, imgs);
    }

    public static unsafe Result GetSwapchainImagesKHR(Device device, SwapchainKHR swapchain, ref uint pSwapchainImageCount, Image* pSwapchainImages)
    {
        fixed (uint* p = &pSwapchainImageCount)
            return _getSwapchainImagesKHR(device, swapchain, p, pSwapchainImages);
    }

    public static unsafe Result AcquireNextImageKHR(Device device, SwapchainKHR swapchain, ulong timeout, Semaphore semaphore, Fence fence, Span<uint> pImageIndex)
    {
        fixed (uint* p = pImageIndex)
            return _acquireNextImageKHR(device, swapchain, timeout, semaphore, fence, p);
    }

    public static unsafe Result QueuePresentKHR(Queue queue, Span<PresentInfoKHR> pPresentInfo)
    {
        fixed (PresentInfoKHR* p = pPresentInfo)
            return _queuePresentKHR(queue, p);
    }

    // ── 物理设备能力查询（UUID/LUID 对齐、设备扩展枚举）──

    public static unsafe Result GetPhysicalDeviceProperties2(PhysicalDevice physicalDevice, PhysicalDeviceProperties2* pProperties2)
    {
        if (_getPhysicalDeviceProperties2 == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetPhysicalDeviceProperties2（请确认 Vulkan 1.1+ 可用）。");
        return _getPhysicalDeviceProperties2(physicalDevice, pProperties2);
    }

    public static unsafe Result EnumerateDeviceExtensionProperties(PhysicalDevice physicalDevice, byte* pLayerName, ref uint pPropertyCount, ExtensionProperties* pProperties)
    {
        if (_enumerateDeviceExtensionProperties == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkEnumerateDeviceExtensionProperties。");
        fixed (uint* p = &pPropertyCount)
            return _enumerateDeviceExtensionProperties(physicalDevice, pLayerName, p, pProperties);
    }

    // ── 外部内存/信号量导出（no-airspace 共享表面源）──

    /// <summary>把 VkDeviceMemory 导出为 Windows HANDLE（外部内存 NT 句柄）。</summary>
    public static unsafe Result GetMemoryWin32HandleKHR(Device device, MemoryGetWin32HandleInfoKHR* pInfo, out nint handle)
    {
        if (_getMemoryWin32HandleKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetMemoryWin32HandleKHR（请确认已启用 VK_KHR_external_memory_win32）。");
        void* h;
        Result r = _getMemoryWin32HandleKHR(device, pInfo, &h);
        handle = (nint)h;
        return r;
    }

    /// <summary>把 VkDeviceMemory 导出为 POSIX 文件描述符（外部内存 fd 句柄）。</summary>
    public static unsafe Result GetMemoryFdKHR(Device device, MemoryGetFdInfoKHR* pInfo, out int fd)
    {
        if (_getMemoryFdKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetMemoryFdKHR（请确认已启用 VK_KHR_external_memory_fd）。");
        int f;
        Result r = _getMemoryFdKHR(device, pInfo, &f);
        fd = f;
        return r;
    }

    /// <summary>把 VkSemaphore 导出为 Windows HANDLE（外部信号量 NT 句柄）。</summary>
    public static unsafe Result GetSemaphoreWin32HandleKHR(Device device, SemaphoreGetWin32HandleInfoKHR* pInfo, out nint handle)
    {
        if (_getSemaphoreWin32HandleKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetSemaphoreWin32HandleKHR（请确认已启用 VK_KHR_external_semaphore_win32）。");
        void* h;
        Result r = _getSemaphoreWin32HandleKHR(device, pInfo, &h);
        handle = (nint)h;
        return r;
    }

    /// <summary>把 VkSemaphore 导出为 POSIX 文件描述符（外部信号量 fd 句柄）。</summary>
    public static unsafe Result GetSemaphoreFdKHR(Device device, SemaphoreGetFdInfoKHR* pInfo, out int fd)
    {
        if (_getSemaphoreFdKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetSemaphoreFdKHR（请确认已启用 VK_KHR_external_semaphore_fd）。");
        int f;
        Result r = _getSemaphoreFdKHR(device, pInfo, &f);
        fd = f;
        return r;
    }

    // ── UTF-8 字符串 / 字符串数组 marshalling（替代 SilkMarshal.StringToPtr / StringArrayToPtr / Free）──

    /// <summary>把单个字符串编码为 NUL 终止的 UTF-8 字节序列，返回指针（调用方须用 <see cref="FreeStringPtr"/> 释放）。</summary>
    public static unsafe byte* StringToPtr(string s)
    {
        byte[] src = Encoding.UTF8.GetBytes(s);
        byte[] withNull = new byte[src.Length + 1];
        global::System.Buffer.BlockCopy(src, 0, withNull, 0, src.Length);
        withNull[src.Length] = 0;
        byte* ptr = (byte*)NativeMemory.Alloc((nuint)withNull.Length);
        Marshal.Copy(withNull, 0, (nint)ptr, withNull.Length);
        return ptr;
    }

    /// <summary>释放 <see cref="StringToPtr"/> 分配的单个 UTF-8 字符串指针。</summary>
    public static unsafe void FreeStringPtr(byte* ptr)
    {
        if (ptr == null) return;
        NativeMemory.Free(ptr);
    }

    /// <summary>把字符串数组编码为 Vulkan 期望的「UTF-8 指针数组」（含 NUL 终止 + 计数前缀）。</summary>
    public static unsafe nint StringArrayToPtr(string[]? strings)
    {
        int count = strings?.Length ?? 0;
        nint* basePtr = (nint*)NativeMemory.Alloc((nuint)((count + 1) * nint.Size));
        basePtr[0] = count; // 计数前缀，供 Free 释放
        nint* arr = basePtr + 1;
        for (int i = 0; i < count; i++)
        {
            byte[] src = Encoding.UTF8.GetBytes(strings![i]);
            byte[] withNull = new byte[src.Length + 1];
            global::System.Buffer.BlockCopy(src, 0, withNull, 0, src.Length);
            withNull[src.Length] = 0;
            nint s = (nint)NativeMemory.Alloc((nuint)withNull.Length);
            Marshal.Copy(withNull, 0, s, withNull.Length);
            arr[i] = s;
        }
        return (nint)arr;
    }

    /// <summary>释放 <see cref="StringArrayToPtr"/> 分配的指针数组及其每个字符串。</summary>
    public static unsafe void FreeStringArrayPtr(nint ptr)
    {
        if (ptr == nint.Zero) return;
        nint* arr = (nint*)ptr;
        nint* basePtr = arr - 1;
        int count = (int)basePtr[0];
        for (int i = 0; i < count; i++)
            NativeMemory.Free((void*)arr[i]);
        NativeMemory.Free(basePtr);
    }
}

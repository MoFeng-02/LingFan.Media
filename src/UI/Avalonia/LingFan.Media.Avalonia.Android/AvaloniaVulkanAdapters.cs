using System.Runtime.Versioning;

namespace LingFan.Media.Avalonia.Android;

/// <summary>Avalonia <c>IVulkanInstance</c> 适配器：包装自建的 VkInstance。</summary>
[SupportedOSPlatform("android23.0")]
public sealed class AvaloniaVulkanInstanceAdapter : global::Avalonia.Vulkan.IVulkanInstance
{
    private readonly nint _handle;
    private readonly string[] _extensions;
    private nint _deviceFallbackHandle;

    /// <summary>包装既有 VkInstance 句柄创建适配器（不取得实例所有权）。</summary>
    /// <param name="handle">VkInstance 原生句柄。</param>
    /// <param name="extensions">创建实例时启用的扩展名列表。</param>
    public AvaloniaVulkanInstanceAdapter(nint handle, string[] extensions)
    {
        _handle = handle;
        _extensions = extensions;
    }

    /// <summary>被包装的 VkInstance 句柄（所有权归创建方，适配器不销毁）。</summary>
    public nint Handle => _handle;

    /// <summary>创建实例时启用的扩展名列表。</summary>
    public IEnumerable<string> EnabledExtensions => _extensions;

    /// <summary>注册共享 device，供 NULL 实例解析兜底（VulkanDeviceFactory 在设备创建后调用）。</summary>
    public void SetDeviceFallback(nint deviceHandle) => _deviceFallbackHandle = deviceHandle;

    /// <summary>解析实例级函数地址；NULL 实例解析未命中时用共享 device 二次解析兜底。</summary>
    /// <remarks>全局派发（instance=NULL）按 Vulkan 规范只保证返回引导子集；Avalonia 的 Skia getProc
    /// 在 device/instance 两路都未命中时以 NULL 实例解析设备级函数，Android loader 对此返回
    /// NULL（并打 invalid call 日志）→ GrContext 创建失败。本应用仅一个共享 device，此处
    /// 以其二次解析。核心名提升函数的 KHR 别名回退统一在 VulkanNative 解析层实现
    ///（实例/设备两路共用，媒体管线同享此兼容）。</remarks>
    public nint GetInstanceProcAddress(nint instance, string name)
    {
        var addr = LingFan.Media.GPUShare.Vulkan.VulkanNative.GetInstanceProcAddress(instance, name);
        if (addr == 0 && _deviceFallbackHandle != 0)
            addr = LingFan.Media.GPUShare.Vulkan.VulkanNative.GetDeviceProcAddress(_deviceFallbackHandle, name);
        return addr;
    }

    /// <summary>解析设备级函数地址（直通 VulkanNative）。</summary>
    public nint GetDeviceProcAddress(nint device, string name) =>
        LingFan.Media.GPUShare.Vulkan.VulkanNative.GetDeviceProcAddress(device, name);

    /// <summary>空操作：实例生命周期与 App 进程一致；实例销毁须在 device 销毁之后，进程退出统一回收。</summary>
    public void Dispose()
    {
    }

    /// <summary>不提供任何可选特性，恒返回 null。</summary>
    public object? TryGetFeature(Type featureType) => null;
}

/// <summary>Avalonia <c>IVulkanDevice</c> 适配器：包装自建的 VkDevice / 主队列。</summary>
[SupportedOSPlatform("android23.0")]
public sealed class AvaloniaVulkanDeviceAdapter : global::Avalonia.Vulkan.IVulkanDevice
{
    private readonly object _sync = new();

    /// <summary>包装既有 VkDevice / 主队列创建适配器（不取得设备所有权）。</summary>
    /// <param name="deviceHandle">VkDevice 原生句柄。</param>
    /// <param name="physicalDeviceHandle">对应的 VkPhysicalDevice 句柄。</param>
    /// <param name="mainQueueHandle">主队列（图形队列）句柄。</param>
    /// <param name="graphicsQueueFamilyIndex">图形队列族索引。</param>
    /// <param name="instance">所属实例适配器。</param>
    /// <param name="extensions">创建设备时启用的扩展名列表。</param>
    public AvaloniaVulkanDeviceAdapter(
        nint deviceHandle,
        nint physicalDeviceHandle,
        nint mainQueueHandle,
        uint graphicsQueueFamilyIndex,
        global::Avalonia.Vulkan.IVulkanInstance instance,
        string[] extensions)
    {
        Handle = deviceHandle;
        PhysicalDeviceHandle = physicalDeviceHandle;
        MainQueueHandle = mainQueueHandle;
        GraphicsQueueFamilyIndex = graphicsQueueFamilyIndex;
        Instance = instance;
        EnabledExtensions = extensions;
    }

    /// <summary>被包装的 VkDevice 句柄（所有权归创建方，适配器不销毁）。</summary>
    public nint Handle { get; }

    /// <summary>对应的 VkPhysicalDevice 句柄。</summary>
    public nint PhysicalDeviceHandle { get; }

    /// <summary>主队列（图形队列）句柄。</summary>
    public nint MainQueueHandle { get; }

    /// <summary>图形队列族索引。</summary>
    public uint GraphicsQueueFamilyIndex { get; }

    /// <summary>所属实例适配器。</summary>
    public global::Avalonia.Vulkan.IVulkanInstance Instance { get; }

    /// <summary>恒为 false：未实现 device lost 恢复（进程级生命周期）。</summary>
    public bool IsLost => false;

    /// <summary>创建设备时启用的扩展名列表。</summary>
    public IEnumerable<string> EnabledExtensions { get; }

    /// <summary>设备级锁：Avalonia 渲染线程与本库管线线程经此串行化 VkDevice 访问。</summary>
    public IDisposable Lock()
    {
        System.Threading.Monitor.Enter(_sync);
        return new LockScope(_sync);
    }

    /// <summary>空操作：设备生命周期与 App 进程一致，进程退出统一回收。</summary>
    public void Dispose()
    {
    }

    /// <summary>不提供任何可选特性，恒返回 null。</summary>
    public object? TryGetFeature(Type featureType) => null;

    private sealed class LockScope(object sync) : IDisposable
    {
        public void Dispose() => System.Threading.Monitor.Exit(sync);
    }
}

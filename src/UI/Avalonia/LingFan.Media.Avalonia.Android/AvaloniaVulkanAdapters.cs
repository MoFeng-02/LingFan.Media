using System.Runtime.Versioning;

namespace LingFan.Media.Avalonia.Android;

/// <summary>Avalonia <c>IVulkanInstance</c> 适配器：包装自建的 VkInstance。</summary>
[SupportedOSPlatform("android23.0")]
public sealed class AvaloniaVulkanInstanceAdapter : global::Avalonia.Vulkan.IVulkanInstance
{
    private readonly nint _handle;
    private readonly string[] _extensions;
    private nint _deviceFallbackHandle;

    public AvaloniaVulkanInstanceAdapter(nint handle, string[] extensions)
    {
        _handle = handle;
        _extensions = extensions;
    }

    public nint Handle => _handle;

    public IEnumerable<string> EnabledExtensions => _extensions;

    /// <summary>注册共享 device，供 NULL 实例解析兜底（VulkanDeviceFactory 在设备创建后调用）。</summary>
    public void SetDeviceFallback(nint deviceHandle) => _deviceFallbackHandle = deviceHandle;

    // 全局派发（instance=NULL）按 Vulkan 规范只保证返回引导子集；Avalonia 的 Skia getProc
    // 在 device/instance 两路都未命中时以 NULL 实例解析设备级函数，Android loader 对此返回
    // NULL（并打 invalid call 日志）→ GrContext 创建失败。本应用仅一个共享 device，此处
    // 以其二次解析。核心名提升函数的 KHR 别名回退统一在 VulkanNative 解析层实现
    //（实例/设备两路共用，媒体管线同享此兼容）。
    public nint GetInstanceProcAddress(nint instance, string name)
    {
        var addr = LingFan.Media.GPUShare.Vulkan.VulkanNative.GetInstanceProcAddress(instance, name);
        if (addr == 0 && _deviceFallbackHandle != 0)
            addr = LingFan.Media.GPUShare.Vulkan.VulkanNative.GetDeviceProcAddress(_deviceFallbackHandle, name);
        return addr;
    }

    public nint GetDeviceProcAddress(nint device, string name) =>
        LingFan.Media.GPUShare.Vulkan.VulkanNative.GetDeviceProcAddress(device, name);

    public void Dispose()
    {
        // 生命周期与 App 进程一致；实例销毁须在 device 销毁之后，进程退出统一回收。
    }

    public object? TryGetFeature(Type featureType) => null;
}

/// <summary>Avalonia <c>IVulkanDevice</c> 适配器：包装自建的 VkDevice / 主队列。</summary>
[SupportedOSPlatform("android23.0")]
public sealed class AvaloniaVulkanDeviceAdapter : global::Avalonia.Vulkan.IVulkanDevice
{
    private readonly object _sync = new();

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

    public nint Handle { get; }

    public nint PhysicalDeviceHandle { get; }

    public nint MainQueueHandle { get; }

    public uint GraphicsQueueFamilyIndex { get; }

    public global::Avalonia.Vulkan.IVulkanInstance Instance { get; }

    public bool IsLost => false; // 未实现 device lost 恢复（进程级生命周期）

    public IEnumerable<string> EnabledExtensions { get; }

    /// <summary>设备级锁：Avalonia 渲染线程与本库管线线程经此串行化 VkDevice 访问。</summary>
    public IDisposable Lock()
    {
        System.Threading.Monitor.Enter(_sync);
        return new LockScope(_sync);
    }

    public void Dispose()
    {
        // 生命周期与 App 进程一致，进程退出统一回收。
    }

    public object? TryGetFeature(Type featureType) => null;

    private sealed class LockScope(object sync) : IDisposable
    {
        public void Dispose() => System.Threading.Monitor.Exit(sync);
    }
}

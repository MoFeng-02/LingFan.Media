using System.Runtime.Versioning;

namespace LingFan.Media.Avalonia.Android;

/// <summary>Avalonia <c>IVulkanInstance</c> 适配器：包装自建的 VkInstance。</summary>
[SupportedOSPlatform("android23.0")]
public sealed class AvaloniaVulkanInstanceAdapter : global::Avalonia.Vulkan.IVulkanInstance
{
    private readonly nint _handle;
    private readonly string[] _extensions;

    public AvaloniaVulkanInstanceAdapter(nint handle, string[] extensions)
    {
        _handle = handle;
        _extensions = extensions;
    }

    public nint Handle => _handle;

    public IEnumerable<string> EnabledExtensions => _extensions;

    // 直接转发 VulkanNative 的 proc-addr 封装（与 Avalonia 内部语义一致：实例级/设备级分派）。
    public nint GetInstanceProcAddress(nint instance, string name) =>
        LingFan.Media.GPUShare.Vulkan.VulkanNative.GetInstanceProcAddress(instance, name);

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

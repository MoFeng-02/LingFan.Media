using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan.Tests;

/// <summary>
/// Headless Vulkan 初始化辅助（无窗口 surface）。供 VK-ZERO 等测试真实驱动 Vulkan 设备，
/// 验证零拷贝 blit/copy 路径。设备选择策略（独显 &gt; 集显）与 <c>VulkanRendererFactory</c> 一致。
/// </summary>
/// <remarks>
/// 仅创建实例 / 物理设备 / 逻辑设备 / 队列 / 命令池 / 命令缓冲，不创建 Surface 与 SwapChain——
/// 因为 VK-ZERO 的 <c>BlitVulkanImageResource</c> 只依赖命令缓冲与两张图像，不需要窗口。
/// </remarks>
public sealed unsafe class VulkanTestContext : IDisposable
{
    public Vk Vk { get; }
    public Instance Instance { get; }
    public PhysicalDevice PhysicalDevice { get; }
    public Device Device { get; }
    public Queue Queue { get; }
    public uint QueueFamilyIndex { get; }

    public VulkanTestContext()
    {
        Vk = Vk.GetApi();

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            ApiVersion = MakeVersion(1, 3, 0),
        };
        var instInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = 0,
            PpEnabledExtensionNames = null,
        };
        if (Vk.CreateInstance(ref instInfo, null, out var instance) != Result.Success)
            throw new InvalidOperationException("vkCreateInstance 失败。");
        Instance = instance;

        uint physCount = 0;
        if (Vk.EnumeratePhysicalDevices(Instance, ref physCount, null) != Result.Success || physCount == 0)
            throw new InvalidOperationException("未找到 Vulkan 物理设备。");
        var physDevices = new PhysicalDevice[physCount];
        fixed (PhysicalDevice* pDevices = physDevices)
            Vk.EnumeratePhysicalDevices(Instance, ref physCount, pDevices);

        // 选择评分最高的图形队列族设备（独显 > 集显 > 虚拟 GPU）
        PhysicalDevice = default;
        QueueFamilyIndex = uint.MaxValue;
        int bestScore = -1;
        foreach (var cand in physDevices)
        {
            uint fam = FindGraphicsQueueFamily(cand);
            if (fam == uint.MaxValue) continue;
            PhysicalDeviceProperties props;
            Vk.GetPhysicalDeviceProperties(cand, &props);
            int score = props.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 3,
                PhysicalDeviceType.IntegratedGpu => 2,
                PhysicalDeviceType.VirtualGpu => 1,
                _ => 0,
            };
            if (score > bestScore)
            {
                bestScore = score;
                PhysicalDevice = cand;
                QueueFamilyIndex = fam;
            }
        }
        if (QueueFamilyIndex == uint.MaxValue)
            throw new InvalidOperationException("无图形队列族。");

        float priority = 1.0f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamilyIndex,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        var devInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            EnabledExtensionCount = 0,
        };
        if (Vk.CreateDevice(PhysicalDevice, ref devInfo, null, out var device) != Result.Success)
            throw new InvalidOperationException("vkCreateDevice 失败。");
        Device = device;

        Vk.GetDeviceQueue(Device, QueueFamilyIndex, 0, out var queue);
        Queue = queue;
        // 注：命令池/命令缓冲由被测 VulkanRenderer.CreateCommandPoolAndBuffer 自建，
        // 此处不分配（避免无人使用的死分配、保持资源所有权单一）。
    }

    private uint FindGraphicsQueueFamily(PhysicalDevice pd)
    {
        uint count = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(pd, ref count, null);
        var props = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = props)
            Vk.GetPhysicalDeviceQueueFamilyProperties(pd, ref count, p);
        for (uint i = 0; i < count; i++)
            if ((props[i].QueueFlags & QueueFlags.GraphicsBit) != 0) return i;
        return uint.MaxValue;
    }

    private static uint MakeVersion(uint major, uint minor, uint patch) => (major << 22) | (minor << 12) | patch;

    public void Dispose()
    {
        if (Device.Handle != 0)
            Vk.DestroyDevice(Device, null);
        if (Instance.Handle != 0)
            Vk.DestroyInstance(Instance, null);
    }
}

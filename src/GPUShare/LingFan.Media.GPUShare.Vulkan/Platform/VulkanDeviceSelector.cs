using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// Vulkan 物理设备选择与身份查询（GPU 胶水：选卡策略唯一实现）。
/// </summary>
/// <remarks>
/// <para><b>归属</b>：GPUShare.Vulkan——物理设备枚举/评分/队列族查找/设备身份（UUID/LUID）查询
/// 在此统一实现，渲染器工厂与共享设备创建等消费方共用，禁止在其他工程复刻选卡策略。</para>
/// <para><b>选卡策略</b>：硬条件 = 具备图形队列族；偏好序 = 独显 &gt; 集显 &gt; 虚拟 GPU &gt; 其他；
/// Linux 上 Intel（vendorID 0x8086）提权（VAAPI 解码 GPU 同设备对齐——跨厂商 dma_buf 导入不可行，实测实证）；
/// 指定首选适配器 LUID 时大幅提权（跨 API 共享纹理导入须与解码侧同 GPU，跨 GPU/厂商导入会被驱动拒绝）。</para>
/// <para><b>AOT</b>：全部经 <see cref="VulkanNative"/> 零反射绑定，无运行时反射。</para>
/// </remarks>
public static unsafe class VulkanDeviceSelector
{
    /// <summary>
    /// 枚举并选择物理设备。失败（枚举错误 / 无设备 / 无图形队列族）抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <param name="instance">已创建并完成实例级函数解析的 VkInstance。</param>
    /// <param name="preferredAdapterLuid">可选首选适配器 LUID（如 D3D11 默认适配器，8 字节）；命中候选时大幅提权。</param>
    /// <param name="logger">诊断日志（可空）。</param>
    /// <returns>选中的物理设备与其图形队列族索引。</returns>
    public static (PhysicalDevice Device, uint GraphicsQueueFamilyIndex) SelectPhysicalDevice(
        Instance instance, byte[]? preferredAdapterLuid, ILogger? logger = null)
    {
        uint physCount = 0;
        Result enumResult = VulkanNative.EnumeratePhysicalDevices(instance, ref physCount, null);
        if (enumResult != Result.Success)
            throw new InvalidOperationException($"vkEnumeratePhysicalDevices 失败: {enumResult}");
        if (physCount == 0)
            throw new InvalidOperationException("未找到 Vulkan 物理设备。");

        var physDevices = new PhysicalDevice[physCount];
        fixed (PhysicalDevice* pDevices = physDevices)
        {
            enumResult = VulkanNative.EnumeratePhysicalDevices(instance, ref physCount, pDevices);
            if (enumResult != Result.Success)
                throw new InvalidOperationException($"vkEnumeratePhysicalDevices (第二次) 失败: {enumResult}");
        }

        // 不盲取 physDevices[0]：以图形队列族为硬条件逐候选评分，取最高分。
        // Present 能力查询需要 Surface，而共享设备在无 Surface 阶段创建，
        // 故以图形队列族为硬条件；实际 Present 兼容性由 CreateSurface 后的
        // SwapChain 创建路径校验（失败会抛明确异常）。
        PhysicalDevice best = default;
        uint bestFamily = uint.MaxValue;
        int bestScore = -1;
        foreach (var candidate in physDevices)
        {
            uint famIdx = FindGraphicsQueueFamily(candidate);
            if (famIdx == uint.MaxValue)
                continue;

            PhysicalDeviceProperties candProps;
            VulkanNative.GetPhysicalDeviceProperties(candidate, &candProps);
            int score = candProps.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 3,
                PhysicalDeviceType.IntegratedGpu => 2,
                PhysicalDeviceType.VirtualGpu => 1,
                _ => 0,
            };

            // Linux VAAPI 零拷贝同设备对齐：解码 GPU 为 Intel（iHD），跨厂商 dma_buf 导入不可行
            // （Intel tiling modifier 对方无法按其布局采样 → 马赛克花屏，实测实证）。
            // vendor 0x8086 提权压过独显优先启发式；Windows 的 D3D11VA LUID 对齐不受影响。
            if (OperatingSystem.IsLinux() && candProps.VendorID == 0x8086)
                score += 10;

            // 零拷贝跨 API 导入对齐：若指定了首选适配器 LUID（D3D11 默认适配器），
            // 命中则大幅提权，压过独显优先启发式——跨 GPU/厂商导入 D3D11 共享纹理会被驱动拒绝。
            if (preferredAdapterLuid is { } wantLuid)
            {
                PhysicalDeviceIDProperties candIdProps = new()
                {
                    SType = StructureType.PhysicalDeviceIDProperties,
                };
                PhysicalDeviceProperties2 candProps2 = new()
                {
                    SType = StructureType.PhysicalDeviceProperties2,
                    PNext = &candIdProps,
                };
                VulkanNative.GetPhysicalDeviceProperties2(candidate, &candProps2);
                // 不校验 DeviceLuidValid：本 Silk.NET 版本无该字段；无效 LUID 恒为 0，
                // 与真实 D3D11 适配器 LUID（非 0）比较必不命中，安全回落独显优先。
                if (LuidEquals(candIdProps.DeviceLuid, wantLuid))
                {
                    score += 100;
                    logger?.LogDebug("Vulkan 物理设备选择：候选命中首选适配器 LUID，提权对齐零拷贝导入（{Name}）",
                        GetDeviceNameSafe(candProps.DeviceName));
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
                bestFamily = famIdx;
            }
        }

        if (bestFamily == uint.MaxValue)
            throw new InvalidOperationException("未找到具备图形队列族的 Vulkan 物理设备。");

        return (best, bestFamily);
    }

    /// <summary>查找第一个具备图形队列（VK_QUEUE_GRAPHICS_BIT）的队列族；无则返回 <see cref="uint.MaxValue"/>。</summary>
    public static unsafe uint FindGraphicsQueueFamily(PhysicalDevice device)
    {
        uint familyCount = 0;
        VulkanNative.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, null);
        if (familyCount == 0)
            return uint.MaxValue;

        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* pFamilies = families)
        {
            VulkanNative.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, pFamilies);
        }

        for (uint i = 0; i < familyCount; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                return i;
        }
        return uint.MaxValue;
    }

    /// <summary>
    /// 查找第一个具备 video-decode 队列（VK_QUEUE_VIDEO_DECODE_BIT_KHR = 0x00000020）的队列族；
    /// 无则返回 <see cref="uint.MaxValue"/>（自定义绑定未单独特化该枚举值时按原始位比对；供 Vulkan Video 硬解复用）。
    /// </summary>
    public static unsafe uint FindVideoDecodeQueueFamily(PhysicalDevice device)
    {
        uint familyCount = 0;
        VulkanNative.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, null);
        if (familyCount == 0)
            return uint.MaxValue;

        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* pFamilies = families)
        {
            VulkanNative.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, pFamilies);
        }

        const uint VideoDecodeQueueBit = 0x00000020;
        for (uint i = 0; i < familyCount; i++)
        {
            if (((uint)families[i].QueueFlags & VideoDecodeQueueBit) != 0)
                return i;
        }
        return uint.MaxValue;
    }

    /// <summary>
    /// 查询物理设备身份（DeviceUuid 16 字节 / DeviceLuid 8 字节，PhysicalDeviceIDProperties）。
    /// 供跨 API 导入对齐校验（如「同 GPU」UUID 比对）使用。
    /// </summary>
    public static (byte[] DeviceUuid, byte[] DeviceLuid) GetPhysicalDeviceIdentity(PhysicalDevice physicalDevice)
    {
        PhysicalDeviceIDProperties idProps = new()
        {
            SType = StructureType.PhysicalDeviceIDProperties,
        };
        PhysicalDeviceProperties2 props2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &idProps,
        };
        VulkanNative.GetPhysicalDeviceProperties2(physicalDevice, &props2);
        var uuid = new byte[16];
        var luid = new byte[8];
        fixed (byte* pUuid = uuid, pLuid = luid)
        {
            // DeviceUuid/DeviceLuid 是固定缓冲字段，不可再 fixed；
            // 固定缓冲在 unsafe 上下文中可直接隐式转为 byte*。
            byte* sUuid = idProps.DeviceUuid;
            byte* sLuid = idProps.DeviceLuid;
            for (int i = 0; i < 16; i++) pUuid[i] = sUuid[i];
            for (int i = 0; i < 8; i++) pLuid[i] = sLuid[i];
        }
        return (uuid, luid);
    }

    /// <summary>从 Vulkan 固定 256 字节设备名缓冲安全取 UTF-8 字符串（诊断用）。</summary>
    public static unsafe string GetDeviceNameSafe(byte* name)
    {
        if (name is null) return "(unknown)";
        ReadOnlySpan<byte> span = new(name, 256);
        int nul = span.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(nul >= 0 ? span[..nul] : span);
    }

    /// <summary>比较 Vulkan 设备 LUID（8 字节固定缓冲）与目标 LUID 字节数组（8 字节）是否一致。</summary>
    private static unsafe bool LuidEquals(byte* a, byte[] b)
    {
        if (a is null || b is null || b.Length < 8) return false;
        for (int i = 0; i < 8; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

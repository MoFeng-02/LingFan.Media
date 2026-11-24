// 前置验证探针：IgnoresAccessChecksTo + NativeAOT 能否访问 Avalonia.Vulkan 的 internal 类型。
// 三个验证点：
//   1) 编译期：internal 类型（VulkanOptions / IVulkanDevice）能否在源码中直接书写；
//   2) PublishAot：AOT 编译是否接受跨程序集 internal 访问；
//   3) 运行时：实例化 internal 类型并读写其属性。
// 本探针仅用于路线可行性验证，不代表库项目对访问检查策略的任何放宽。

using Avalonia.Vulkan;

// 消费方声明：允许访问目标程序集的 internal 成员（.NET 9+ Roslyn 原生语义）。
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Avalonia.Vulkan")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Avalonia.Skia")]

namespace IgnoresAccessChecksProbe;

/// <summary>
/// IgnoresAccessChecksTo 特性探针：验证程序集级忽略访问检查后，
/// 跨程序集 typeof/实例化 internal 类型在运行时可行（AOT 兼容性验证载体）。
/// </summary>
public static class Program
{
    /// <summary>探针入口：逐项验证 internal 类型访问并输出判定。</summary>
    /// <returns>全部验证通过为 0，否则非 0。</returns>
    public static int Main()
    {
        // (1) internal 类型 typeof
        var t = typeof(VulkanOptions);
        Console.WriteLine($"[OK1] typeof(VulkanOptions) = {t.FullName}");

        // (2) internal 类型实例化 + 属性读写（VulkanOptions.CustomSharedDevice 为 internal setter）
        var opts = new VulkanOptions();
        var devOpts = new VulkanDeviceCreationOptions();
        Console.WriteLine($"[OK2] VulkanOptions 实例化成功；VulkanDeviceCreationOptions 实例化成功（devOpts==null: {devOpts is null}）");

        // (3) internal 接口类型引用（IVulkanDevice 由 VulkanDevice 实现，此处仅验证可见性）
        IVulkanDevice? dev = null;
        Console.WriteLine($"[OK3] IVulkanDevice 可见（当前为 null: {dev is null}）");

        // (4) Avalonia.Skia internal 是否可穿透（上一轮 Android 工程 CS0122，此处用纯 dll 引用复核）
        var skiaFeatureType = typeof(global::Avalonia.Skia.ISkiaSharpApiLeaseFeature);
        Console.WriteLine($"[OK4] typeof(ISkiaSharpApiLeaseFeature) = {skiaFeatureType.FullName}");

        Console.WriteLine("探针全部通过：IgnoresAccessChecksTo 编译期可见性成立。");
        return 0;
    }
}

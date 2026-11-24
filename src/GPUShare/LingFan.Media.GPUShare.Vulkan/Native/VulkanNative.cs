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
/// <see cref="InitDevice(Device, bool)"/> 经设备句柄解析设备级函数与 VK_KHR_swapchain 设备扩展。
/// 函数指针统一 <c>delegate* unmanaged[Stdcall]</c>（Vulkan 的 <c>VKAPI_PTR</c> 在 Windows 即 <c>__stdcall</c>，
/// 在 macOS/iOS（MoltenVK）单一 ABI 下 .NET 同样映射为平台默认调用约定，可正确互操作）。
/// 结构体 / 枚举 / 句柄类型复用 Silk.NET 的纯数据定义（<c>[StructLayout]</c> 值类型 + <c>enum</c>，本身零反射、ABI 精确）。</para>
/// <para><b>为何不能一次全局派发解析全部</b>：Vulkan 规范要求 <c>vkGetInstanceProcAddr(NULL, …)</c> 只保证返回
/// 引导子集；其余函数（含 <c>vkDestroyInstance</c> / <c>vkCreateDevice</c>）经 NULL 派发会静默返回 <c>NULL</c>。
/// 这是跨 loader 兼容的强制要求，Silk.NET / volk 均按此分阶段解析。</para>
/// <para>调用约定：Vulkan 函数指针统一 <c>unmanaged[Stdcall]</c>；调用方传参与 Silk.NET 的 <c>Vk</c>/<c>Khr*</c>
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
        // Linux 的 Xlib/Wayland surface 绑定经 vkGetInstanceProcAddr 解析，也依赖此引导符号可用。
        if (OperatingSystem.IsLinux())
        {
            if (NativeLibrary.TryLoad("libvulkan.so.1", assembly, searchPath, out h))
                return h;
            if (NativeLibrary.TryLoad("libvulkan.so", assembly, searchPath, out h))
                return h;
            return nint.Zero;
        }

        // Android：Vulkan loader 即 libvulkan.so（系统随 NDK 提供），vulkan-1 不可解析。
        // Android ANativeWindow surface 绑定同样依赖此引导符号。
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

    private static bool _bootstrapped;
    private static bool _instanceReady;
    private static bool _deviceReady;
    private static readonly object _loadLock = new();

    // 公开包装方法按功能域拆分至 VulkanNative.*.cs（部分类，编译期等价）；视频解码扩展见 VulkanNative.Video.cs。
}

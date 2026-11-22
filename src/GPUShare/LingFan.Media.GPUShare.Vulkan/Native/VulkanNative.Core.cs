using System.Runtime.InteropServices;
using System.Text;

namespace LingFan.Media.GPUShare.Vulkan;

// VulkanNative 的 Core 功能域包装方法（部分类，由 VulkanNative.cs 按域拆分，编译期等价）。
// 函数指针字段与三阶段初始化在 VulkanNative.cs；签名对齐 Silk.NET Vk / Khr*，调用点仅改名。
public static unsafe partial class VulkanNative
{
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

    public static unsafe Result EnumerateInstanceExtensionProperties(byte* pLayerName, uint* pPropertyCount, ExtensionProperties* pProperties)
        => _enumerateInstanceExtensionProperties(pLayerName, pPropertyCount, pProperties);

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

    // Android AHB 零拷贝导入（VK_ANDROID_external_memory_android_hardware_buffer + sampler YCbCr 转换）

    /// <summary>是否已解析 <c>vkGetAndroidHardwareBufferPropertiesANDROID</c>（Android 且设备已启用 AHB 扩展时为真）。</summary>
    public static bool HasAndroidHardwareBufferProperties => _getAndroidHardwareBufferPropertiesANDROID != null;

    // samplerYcbcrConversion 特性是否在设备创建期启用（由渲染器工厂探测后经 InitDevice 传入）。
    // 【关键】函数指针可解析 ≠ 特性已启用：vkCreateSamplerYcbcrConversion 经核心名或 KHR 别名可解析（1.1 core），
    // 但特性未启用时使用 YCbCr 采样器属规范违规 —— 驱动未定义行为可致进程级原生崩溃（对齐错误）。
    private static bool _samplerYcbcrFeatureEnabled;

    /// <summary>samplerYcbcrConversion 是否可用：函数已解析<b>且</b>特性已在设备创建期启用（双判据）。</summary>
    public static bool HasSamplerYcbcrConversion => _createSamplerYcbcrConversion != null && _samplerYcbcrFeatureEnabled;

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

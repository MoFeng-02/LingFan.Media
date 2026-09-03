namespace LingFan.Media.Abstractions;

/// <summary>
/// 共享 GPU 图像句柄类型（中立枚举，跨 GPU API）。
/// </summary>
/// <remarks>
/// <para>与宿主 UI 框架的「外部图像句柄类型」一一对应（如 Avalonia 的
/// <c>KnownPlatformGraphicsExternalImageHandleTypes</c>），但本枚举<b>不引用任何 UI 框架或 GPU 库</b>——
/// 映射由消费侧（UI 层）用一次 switch 完成，从而让 GPU 适配器与渲染器层彻底解耦。</para>
/// <para><b>扩展方式</b>：新增 GPU 后端（Vulkan/Metal/OpenGL）时在此追加成员，
/// 适配器实现 <see cref="ISharedGpuSurfaceSource"/> 即可插入，渲染器层无需改动。</para>
/// </remarks>
public enum SharedGpuHandleKind
{
    /// <summary>D3D11 纹理的全局共享句柄（<c>IDXGIResource.GetSharedHandle</c>）。</summary>
    D3D11TextureGlobalSharedHandle = 0,

    /// <summary>D3D11 纹理的 NT 句柄（<c>IDXGIResource1.CreateSharedHandle</c>）。</summary>
    D3D11TextureNtHandle = 1,

    /// <summary>Vulkan 外部内存 NT 句柄（Windows）。</summary>
    VulkanOpaqueNtHandle = 2,

    /// <summary>Vulkan 外部内存 POSIX 文件描述符（Linux/Android）。</summary>
    VulkanOpaquePosixFileDescriptor = 3,

    /// <summary>Apple IOSurface 引用（macOS/iOS）。</summary>
    IOSurfaceRef = 4,

    /// <summary>Android <c>AHardwareBuffer</c> 引用（经 <c>VK_ANDROID_external_memory_android_hardware_buffer</c> 从 Vulkan 离屏图像导出）。</summary>
    /// <remarks>消费侧（Avalonia Android 合成器）经 <c>ICompositionGpuInterop</c> 直接导入采样，实现无空域零拷贝上屏。</remarks>
    AndroidHardwareBuffer = 5,

    /// <summary>Vulkan 原生 <c>VkImage</c> 句柄（<b>同 device 直接采样</b>，不跨设备导出/导入）。</summary>
    /// <remarks>
    /// <para>适用于宿主 UI 框架与共享表面源<b>共用同一 VkDevice</b> 的场景（如宿主注入自建 device）：
    /// 消费方（UI 层 Skia GPU 上下文）直接把该 VkImage 包装为采样纹理绘制上屏，全程零外部内存
    /// 导出/导入、零 fd、零 dedicated 分配。</para>
    /// <para>前置条件由生产者保证：交付时图像已处于 <c>ShaderReadOnlyOptimal</c> 布局且写入已对
    /// 后续采样可见；生产与消费<b>共用同一 VkQueue</b>（同队列隐式按提交序串行）或另有同步约定。
    /// 图像/内存生命周期归生产者（<see cref="ISharedGpuSurfaceSource"/>），消费方只借用不释放。</para>
    /// <para>对应的原生参数（内存句柄/布局/格式/队列族等）经
    /// <see cref="SharedGpuSurfaceDescriptor"/> 的 Native* 可选字段传递。</para>
    /// </remarks>
    VulkanNativeImage = 6,
}

/// <summary>
/// 共享 GPU 表面的像素格式（中立枚举）。
/// </summary>
/// <remarks>共享表面统一为 32 位 RGBA/BGRA——YUV→RGB 转换由 GPU 适配器在写入前完成，
/// 消费侧（UI 合成器）只面对一种可直接采样的格式。</remarks>
public enum SharedGpuSurfaceFormat
{
    /// <summary>每通道 8 位，BGRA 顺序，无符号归一化。</summary>
    B8G8R8A8UNorm = 0,

    /// <summary>每通道 8 位，RGBA 顺序，无符号归一化。</summary>
    R8G8B8A8UNorm = 1,
}

/// <summary>
/// 共享 GPU 表面描述符（中立值对象）。描述一块可被宿主合成器导入的跨设备共享纹理。
/// </summary>
/// <param name="Handle">平台共享句柄（含义由 <paramref name="Kind"/> 决定）。</param>
/// <param name="Kind">句柄类型。</param>
/// <param name="Width">表面宽度（像素）。</param>
/// <param name="Height">表面高度（像素）。</param>
/// <param name="Format">表面像素格式。</param>
/// <param name="Version">
/// 表面版本号。适配器每次<b>重建</b>底层纹理（尺寸变化等）时递增；
/// 消费方据此判断需要丢弃已缓存的导入图像并重新导入。同一纹理连续出帧时版本号保持不变。
/// </param>
/// <param name="SyncMode">
/// 本表面使用的跨设备同步模型（与产出它的 <see cref="ISharedGpuSurfaceSource.SyncMode"/> 一致）。
/// 消费方据此选择对应的提交方式（keyed mutex / 信号量），各后端互不跨界。
/// </param>
/// <param name="MemorySize">
/// 承载本表面的外部内存<b>分配字节数</b>（生产者侧 <c>vkGetImageMemoryRequirements().size</c>）。
/// <para><b>宿主合成器会拿它做严格相等校验</b>：Avalonia 的
/// <c>VulkanExternalObjectsFeature.ImportedImage.CreateMemory</c> 要求
/// <c>MemoryOffset == 0 &amp;&amp; MemorySize == 它自身算出的 size</c>，否则直接抛
/// <c>Invalid memory size</c>。故凡经 POSIX fd（OPAQUE_FD）导入的后端<b>必须</b>如实填写，
/// 留 0 必然导入失败。</para>
/// <para>该校验对 D3D11 纹理句柄分支不适用（Avalonia 走 D3D11 专用导入路径、不比对此值），
/// 这些后端可保持 0。</para>
/// </param>
/// <param name="MemoryOffset">
/// 本表面在外部内存中的字节偏移。宿主合成器要求恒为 0（与 <paramref name="MemorySize"/> 同处一次校验）。
/// <para><see cref="SharedGpuHandleKind.VulkanNativeImage"/> 下作为原生内存偏移交消费方包装（本源恒 0）。</para>
/// </param>
/// <param name="NativeImage">原生 <c>VkImage</c> 句柄（仅 <see cref="SharedGpuHandleKind.VulkanNativeImage"/> 有效，其余为 0）。</param>
/// <param name="NativeDeviceMemory">原生 <c>VkDeviceMemory</c> 句柄（同上；配合 <paramref name="MemorySize"/>/<paramref name="MemoryOffset"/> 描述绑定）。</param>
/// <param name="NativeImageLayout">图像当前布局（<c>VkImageLayout</c> 数值；交付约定 <c>ShaderReadOnlyOptimal</c>）。</param>
/// <param name="NativeVkFormat">图像 <c>VkFormat</c> 数值。</param>
/// <param name="NativeQueueFamilyIndex">图像当前所属队列族索引（Exclusive 模式；须与消费采样队列同族）。</param>
/// <param name="NativeImageUsage">图像 <c>VkImageUsageFlags</c> 数值（须含 Sampled 位）。</param>
/// <param name="NativeImageTiling">图像 <c>VkImageTiling</c> 数值（Optimal）。</param>
/// <remarks>
/// <para><b>纯数据</b>：不持有所有权，不可释放——底层纹理生命周期归产出它的
/// <see cref="ISharedGpuSurfaceSource"/> 管理。</para>
/// <para><b>AOT 兼容</b>：readonly record struct，无反射、无装箱路径。</para>
/// </remarks>
public readonly record struct SharedGpuSurfaceDescriptor(
    IntPtr Handle,
    SharedGpuHandleKind Kind,
    int Width,
    int Height,
    SharedGpuSurfaceFormat Format,
    ulong Version,
    SharedGpuSyncMode SyncMode,
    ulong MemorySize = 0,
    ulong MemoryOffset = 0,
    IntPtr NativeImage = default,
    IntPtr NativeDeviceMemory = default,
    uint NativeImageLayout = 0,
    uint NativeVkFormat = 0,
    uint NativeQueueFamilyIndex = 0,
    uint NativeImageUsage = 0,
    uint NativeImageTiling = 0)
{
    /// <summary>句柄是否有效（非空且尺寸为正）。</summary>
    public bool IsValid => Handle != IntPtr.Zero && Width > 0 && Height > 0;

    /// <summary>是否携带同 device 直采样所需的全套原生参数（VkImage + VkDeviceMemory + 格式 + 布局 + 队列族）。</summary>
    public bool HasNativeImage =>
        NativeImage != IntPtr.Zero
        && NativeDeviceMemory != IntPtr.Zero
        && NativeVkFormat != 0
        && NativeImageLayout != 0;
}

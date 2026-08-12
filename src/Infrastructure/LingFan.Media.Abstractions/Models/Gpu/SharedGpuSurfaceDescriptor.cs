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
    SharedGpuSyncMode SyncMode)
{
    /// <summary>句柄是否有效（非空且尺寸为正）。</summary>
    public bool IsValid => Handle != IntPtr.Zero && Width > 0 && Height > 0;
}

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
/// <para><b>每个成员该填哪些描述符字段</b>（Handle / NativeImage / NativeDeviceMemory / MemorySize /
/// MemoryOffset）见 <see cref="SharedGpuSurfaceDescriptor"/> 的字段语义表——该表是本枚举与描述符之间的
/// 唯一约定，缺了它各实现只能自行猜测填法（同一字段被赋成两种含义是已发生过的事故来源）。</para>
/// </remarks>
public enum SharedGpuHandleKind
{
    /// <summary>D3D11 纹理的全局共享句柄（<c>IDXGIResource.GetSharedHandle</c>）。</summary>
    D3D11TextureGlobalSharedHandle = 0,

    /// <summary>D3D11 纹理的 NT 句柄（<c>IDXGIResource1.CreateSharedHandle</c>）。</summary>
    /// <remarks>
    /// <para><b>实现状态</b>：消费侧（UI 层句柄映射）已接线，<b>生产侧尚无实现产出该类型</b>——
    /// 现有 D3D11 共享表面源一律交付 <see cref="D3D11TextureGlobalSharedHandle"/>。
    /// 消费方须把本值视为「宿主可识别、当前无生产者」的保留值，不得据此假定链路可用。</para>
    /// </remarks>
    D3D11TextureNtHandle = 1,

    /// <summary>Vulkan 外部内存 NT 句柄（Windows）。</summary>
    VulkanOpaqueNtHandle = 2,

    /// <summary>Vulkan 外部内存 POSIX 文件描述符（Linux/Android）。</summary>
    VulkanOpaquePosixFileDescriptor = 3,

    /// <summary>Apple IOSurface 引用（macOS/iOS）。</summary>
    IOSurfaceRef = 4,

    /// <summary>Android <c>AHardwareBuffer</c> 引用（解码侧产出，经 <c>VK_ANDROID_external_memory_android_hardware_buffer</c> <b>导入</b>为 Vulkan 图像后采样）。</summary>
    /// <remarks>
    /// <para><b>方向</b>：AHB 由解码侧（MediaCodec 输出面）产出，Vulkan/GL 侧是<b>导入方</b>。
    /// 本值描述「交付给消费方的句柄是什么」，<b>不是</b>「由 Vulkan 离屏图像导出 AHB」——
    /// 反向理解会写出永不成立的导入逻辑。</para>
    /// <para><b>实现状态</b>：消费侧（Avalonia Android 合成器经 <c>ICompositionGpuInterop</c> 直接导入采样）
    /// 已接线，<b>生产侧尚无实现以该类型交付描述符</b>。</para>
    /// </remarks>
    AndroidHardwareBuffer = 5,

    /// <summary>Vulkan 原生 <c>VkImage</c> 句柄（<b>同 device 直接采样</b>，不跨设备导出/导入）。</summary>
    /// <remarks>
    /// <para>适用于宿主 UI 框架与共享表面源<b>共用同一 VkDevice</b> 的场景（如宿主注入自建 device）：
    /// 消费方（UI 层 Skia GPU 上下文）直接把该 VkImage 包装为采样纹理绘制上屏，全程零外部内存
    /// 导出/导入、零 fd、零 dedicated 分配。</para>
    /// <para>前置条件由生产者保证：交付时写入已对后续采样可见，且<b>描述符声明的
    /// <c>NativeImageLayout</c> 必须等于图像实际布局</b>——声明值与实际值不一致会让包装通过而采样黑屏。
    /// 实际布局按产出路径分两种情形：</para>
    /// <list type="bullet">
    /// <item><b>转换后共享图</b>（生产者自建图像、自行做布局过渡）：交付 <c>ShaderReadOnlyOptimal</c>。</item>
    /// <item><b>外部内存直采</b>（如 Android AHB 导入、全程无屏障无过渡）：交付 <c>General</c>——
    /// 按外部内存惯例其实际布局即 General，声明成只读最优属虚假声明。</item>
    /// </list>
    /// <para>生产与消费<b>共用同一 VkQueue</b>（同队列隐式按提交序串行）或另有同步约定。
    /// 图像/内存生命周期归生产者（<see cref="ISharedGpuSurfaceSource"/>），消费方只借用不释放。</para>
    /// <para>对应的原生参数（内存句柄/布局/格式/队列族等）经
    /// <see cref="SharedGpuSurfaceDescriptor"/> 的 Native* 可选字段传递。</para>
    /// </remarks>
    VulkanNativeImage = 6,

    /// <summary>OpenGL / GL ES 的 <c>EGLImageKHR</c>（<b>display 级共享对象</b>，不是 GL 纹理对象名）。</summary>
    /// <remarks>
    /// <para><b>字段语义已钉死</b>：<see cref="SharedGpuSurfaceDescriptor.NativeImage"/> 承载的是
    /// <c>EGLImageKHR</c> 本身，<b>不是</b> GL 纹理对象名（<c>GLuint</c>）。消费方把它传给
    /// <c>glEGLImageTargetTexture2DOES</c> 包装为采样纹理。两者同为整型句柄、沿链路传递时都不报错，
    /// 因此契约在此显式区分：<b>生产者不得交付普通纹理名，消费方不得把该值当纹理名解引用</b>
    /// （当作纹理名解引用会读到未定义内容或直接崩溃）。</para>
    /// <para>适用于宿主 UI 框架为 OpenGL(EGL) 后端、且生产者与宿主采样上下文位于同一
    /// <c>EGLDisplay</c>（通常同 <c>EGLContext</c> 共享组）的场景：消费方（UI 层 Skia GPU 上下文）
    /// 直接包装该 EGLImage 绘制上屏，全程零跨 API（GL 世界内部共享）。</para>
    /// <para>内部格式（RGBA8）经 <c>NativeVkFormat</c> 字段传递；EGLImage 生命周期归生产者，
    /// 消费方只借用、绝不销毁。</para>
    /// </remarks>
    GlTexture = 7,
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
/// <param name="NativeImage">
/// 原生图像句柄，<b>含义由 <see cref="Kind"/> 唯一决定</b>：<see cref="SharedGpuHandleKind.VulkanNativeImage"/>
/// 下是原生 <c>VkImage</c>，<see cref="SharedGpuHandleKind.GlTexture"/> 下是 <c>EGLImageKHR</c>（display 级共享对象，
/// <b>不是</b> GL 纹理对象名），其余 Kind 必须为 0。消费方必须先看 <see cref="Kind"/> 再解引用。
/// </param>
/// <param name="NativeDeviceMemory">原生 <c>VkDeviceMemory</c> 句柄（仅 <see cref="SharedGpuHandleKind.VulkanNativeImage"/> 有效，其余为 0；配合 <paramref name="MemorySize"/>/<paramref name="MemoryOffset"/> 描述绑定）。</param>
/// <param name="NativeImageLayout">
/// 图像<b>实际</b>布局（<c>VkImageLayout</c> 数值），必须与图像真实布局一致，仅
/// <see cref="SharedGpuHandleKind.VulkanNativeImage"/> 有效。两种情形：转换后共享图交付
/// <c>ShaderReadOnlyOptimal</c>；外部内存直采（如 Android AHB 导入，无布局过渡）交付 <c>General</c>。
/// 声明值与实际不符会导致消费方包装通过而采样黑屏。
/// </param>
/// <param name="NativeVkFormat">图像 <c>VkFormat</c> 数值。</param>
/// <param name="NativeQueueFamilyIndex">图像当前所属队列族索引（Exclusive 模式；须与消费采样队列同族）。</param>
/// <param name="NativeImageUsage">图像 <c>VkImageUsageFlags</c> 数值（须含 Sampled 位）。</param>
/// <param name="NativeImageTiling">图像 <c>VkImageTiling</c> 数值（Optimal）。</param>
/// <remarks>
/// <para><b>纯数据</b>：不持有所有权，不可释放——底层纹理/内存生命周期归产出它的
/// <see cref="ISharedGpuSurfaceSource"/> 管理。</para>
/// <para><b>字段语义表（按 <see cref="Kind"/> 分派，唯一约定）</b>：</para>
/// <code>
/// Kind                              Handle                     NativeImage        NativeDeviceMemory  MemorySize            MemoryOffset
/// D3D11TextureGlobalSharedHandle    D3D11 全局共享句柄          0                  0                   可为 0（不做校验）      0
/// D3D11TextureNtHandle              D3D11 NT 句柄（暂无生产者）  0                  0                   可为 0                 0
/// VulkanOpaqueNtHandle              外部内存 Win32 句柄         0                  0                   必填（严格相等校验）    0
/// VulkanOpaquePosixFileDescriptor   外部内存 fd                 0                  0                   必填（严格相等校验）    0
/// IOSurfaceRef                      IOSurfaceRef 引用           0                  0                   按导入路径需要时填写    0
/// AndroidHardwareBuffer             AHB 引用（溯源/诊断）        0                  0                   分配字节数             0
/// VulkanNativeImage                 VkImage（同 NativeImage）    VkImage            VkDeviceMemory      分配字节数             0
/// GlTexture                         源 AHB 句柄（溯源/诊断）     EGLImageKHR        0                   0                     0
/// </code>
/// <para><b>填表规则</b>：表中未列出的原生字段必须保持 0——它们对该 Kind 无意义，
/// 非 0 会让消费方（或 <see cref="HasNativeImage"/> 这类守卫）误判路径可用。
/// 同一字段在不同 Kind 下含义不同，消费方必须先看 <see cref="Kind"/> 再解引用句柄。</para>
/// <para><b>句柄存活期</b>：句柄只在「生产者持有」的这段时间内有效——表内的原生句柄
/// （尤其是 <see cref="SharedGpuHandleKind.VulkanOpaquePosixFileDescriptor"/> 的 fd 与
/// <see cref="SharedGpuHandleKind.IOSurfaceRef"/> 的引用）由生产者持有至下一次
/// <see cref="ISharedGpuSurfaceSource.TryWriteFrame"/> 或该源 <c>Dispose</c> 为止。
/// 消费方若需跨调用持有，必须自行 dup / CFRetain 并承担对应释放责任；只在本帧内使用则无需复制。</para>
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
    uint NativeImageTiling = 0,
    int RotationDegrees = 0)
{
    /// <summary>句柄是否有效（非空且尺寸为正）。</summary>
    /// <remarks>
    /// <para><b>校验范围</b>：只校验 <see cref="Handle"/> 非空与宽高为正，<b>不</b>按 <see cref="Kind"/>
    /// 校验该类型必填的原生字段是否齐备。</para>
    /// <para><b>按 Kind 的完整判据</b>见 <see cref="SharedGpuSurfaceDescriptor"/> 的字段语义表：
    /// 消费方在真正导入前必须自行对照该表检查本 Kind 的必填字段（如
    /// <see cref="SharedGpuHandleKind.VulkanNativeImage"/> 须同时具备 NativeImage / NativeDeviceMemory /
    /// NativeVkFormat / NativeImageLayout / 队列族与 MemorySize；<see cref="SharedGpuHandleKind.GlTexture"/>
    /// 须具备 NativeImage 与 NativeVkFormat）。只凭本属性判定会放过「句柄非空但原生参数不全」的描述符。</para>
    /// </remarks>
    public bool IsValid => Handle != IntPtr.Zero && Width > 0 && Height > 0;

    /// <summary>是否携带同 device 直采样所需的全套原生参数（VkImage + VkDeviceMemory + 格式 + 布局）。</summary>
    /// <remarks>
    /// <para><b>用途</b>：供消费方在导入前做守卫——为 <see langword="false"/> 表示描述符不足以走
    /// <see cref="SharedGpuHandleKind.VulkanNativeImage"/> 直采样路径，应回退到导出/导入路径。
    /// 当前消费侧仍按 <see cref="Kind"/> 分派，暂无调用方；它是契约约定的守卫，不属于可删死码。</para>
    /// <para><b>判据只看上述四个字段、不看 <see cref="Kind"/> </b>：按字段语义表，非
    /// <see cref="SharedGpuHandleKind.VulkanNativeImage"/> 的 Kind 不应填这些字段，故结果自然为
    /// <see langword="false"/>。</para>
    /// </remarks>
    public bool HasNativeImage =>
        NativeImage != IntPtr.Zero
        && NativeDeviceMemory != IntPtr.Zero
        && NativeVkFormat != 0
        && NativeImageLayout != 0;
}

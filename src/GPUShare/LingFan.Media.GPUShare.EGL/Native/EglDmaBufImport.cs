namespace LingFan.Media.GPUShare.EGL;

/// <summary>
/// EGL_EXT_image_dma_buf_import 零拷贝导入互操作（Linux 路径：dmabuf → EGLImage → GL 纹理）。
/// </summary>
/// <remarks>
/// <para><b>归属</b>：GPU 胶水层（GPUShare.EGL）——dma_buf 导入常量与扩展符号唯一真源，
/// 渲染器（Renderers.OpenGL 等）作为消费方引用；禁止在其他工程复刻本常量表
/// （历史教训：同一套常量两处维护曾整块错位，是 dma_buf 导入恒 <c>EGL_BAD_PARAMETER</c> 的直接温床）。</para>
/// <para><b>签名铁律</b>：<c>eglCreateImageKHR</c> 必须五参（dpy, ctx, target, clientBuffer, attrib_list）——
/// 缺 clientBuffer 会使 x64 实参整体错位一格（attribs 指针落进 buffer 槽、attrib_list 读栈上垃圾），
/// 实现层恒报 <c>EGL_BAD_PARAMETER</c>，与显示/参数无关。</para>
/// <para><b>调用约定</b>：EGL/GL 扩展为平台默认 ABI，函数指针统一 <c>delegate* unmanaged</c>（零反射）。</para>
/// </remarks>
public static unsafe class EglDmaBufImport
{
    // EGL_EXT_image_dma_buf_import 常量（EGL/eglext.h 官方值。
    // 历史教训：本表曾整块错位一格且混入不存在的"PLANE_COUNT"键，致 dma_buf 导入恒 EGL_BAD_PARAMETER。）
    public const int EglImageTarget = 0x30D1;          // EGL_IMAGE_TARGET (OES 目标枚举)
    public const int EglLinuxDmaBufExt = 0x3270;       // EGL_LINUX_DMA_BUF_EXT
    public const int EglWidth = 0x3057;                // EGL_WIDTH
    public const int EglHeight = 0x3056;              // EGL_HEIGHT
    public const int EglDmaBufPlane0FdExt = 0x3272;    // EGL_DMA_BUF_PLANE0_FD_EXT
    public const int EglDmaBufPlane0OffsetExt = 0x3273; // EGL_DMA_BUF_PLANE0_OFFSET_EXT
    public const int EglDmaBufPlane0PitchExt = 0x3274; // EGL_DMA_BUF_PLANE0_PITCH_EXT
    public const int EglDmaBufPlane0ModifierLoExt = 0x3275; // EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT
    public const int EglDmaBufPlane0ModifierHiExt = 0x3276; // EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT
    public const int EglLinuxDrmFourccExt = 0x3271;    // EGL_LINUX_DRM_FOURCC_EXT
    public const int EglNone = 0x3038;                 // EGL_NONE
    // 注：EGL 无 "PLANE_COUNT" 键（0x3278/0x3279 实为 PLANE1_MODIFIER_LO/HI）——平面数由出现的
    // 最高平面号属性推断；多平面导入须用 PLANE1_* 属性对，不得使用虚构计数键。

    // EGL dma_buf / OES 函数指针（Linux 调用，平台默认 ABI）
    // 签名必须五参（dpy, ctx, target, clientBuffer, attrib_list）——缺 clientBuffer 会使调用方
    // 实参整体错位一格，Mesa 恒报 EGL_BAD_PARAMETER，与显示/参数无关。
    private static delegate* unmanaged<nint, nint, uint, nint, int*, nint> _eglCreateImageKHR;
    private static delegate* unmanaged<nint, nint, int> _eglDestroyImageKHR;
    private static delegate* unmanaged<uint, nint, void> _glEGLImageTargetTexture2DOES;
    // modifier 列表查询（EGL_EXT_image_dma_buf_import_modifiers）——诊断显示侧导入支持面。
    private static delegate* unmanaged<nint, int, int, ulong*, int*, int*, int> _eglQueryDmaBufModifiersEXT;

    private static bool _interopResolved;

    /// <summary>运行时解析互扩展函数指针（幂等；EGL 上下文须已建立）。</summary>
    private static void ResolveInterop()
    {
        if (_interopResolved) return;

        _eglCreateImageKHR = (delegate* unmanaged<nint, nint, uint, nint, int*, nint>)EglNative.ResolveProc("eglCreateImageKHR");
        _eglDestroyImageKHR = (delegate* unmanaged<nint, nint, int>)EglNative.ResolveProc("eglDestroyImageKHR");
        _glEGLImageTargetTexture2DOES = (delegate* unmanaged<uint, nint, void>)EglNative.ResolveProc("glEGLImageTargetTexture2DOES");
        _eglQueryDmaBufModifiersEXT = (delegate* unmanaged<nint, int, int, ulong*, int*, int*, int>)EglNative.ResolveProc("eglQueryDmaBufModifiersEXT");

        // 仅当确有指针解析成功才置"已解析"：eglGetProcAddress 在无当前 EGL 上下文时静默返 null。
        // 若不缓存此负结果，下次（上下文已 current）可重试解析，避免零拷贝路径被一次性误判永久禁用。
        _interopResolved = _eglCreateImageKHR != null;
    }

    /// <summary>EGL_EXT_image_dma_buf_import + glEGLImageTargetTexture2DOES 是否可用（EGL 上下文须已建立）。</summary>
    public static bool IsEglDmaBufImportAvailable()
    {
        ResolveInterop();
        return _eglCreateImageKHR != null && _glEGLImageTargetTexture2DOES != null;
    }

    public static unsafe nint EglCreateImageKHR(nint dpy, nint ctx, uint target, nint clientBuffer, int* attribList)
        => _eglCreateImageKHR != null ? _eglCreateImageKHR(dpy, ctx, target, clientBuffer, attribList) : nint.Zero;

    /// <summary>
    /// 查询显示侧指定 fourcc 支持的 dma_buf modifier 列表（EGL_EXT_image_dma_buf_import_modifiers）。
    /// 两段式调用：先 max=0 取数量，再取列表。externalOnly 可为 null（不需要逐 modifier 的
    /// external-only 标志）。函数未解析/查询失败返回 false。
    /// </summary>
    public static unsafe bool TryQueryDmaBufModifiers(nint display, int drmFourcc, ulong* modifiers, int* externalOnly, int maxModifiers, int* numModifiers)
        => _eglQueryDmaBufModifiersEXT != null
            && _eglQueryDmaBufModifiersEXT(display, drmFourcc, maxModifiers, modifiers, externalOnly, numModifiers) != 0;

    public static unsafe int EglDestroyImageKHR(nint dpy, nint image)
        => _eglDestroyImageKHR != null ? _eglDestroyImageKHR(dpy, image) : 0;

    public static unsafe void GlEGLImageTargetTexture2DOES(uint target, nint image)
    {
        if (_glEGLImageTargetTexture2DOES != null)
            _glEGLImageTargetTexture2DOES(target, image);
    }
}

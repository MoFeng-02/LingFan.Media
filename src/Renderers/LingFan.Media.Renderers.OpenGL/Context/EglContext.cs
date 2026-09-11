using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.OpenGL.Context;

/// <summary>
/// Linux EGL 桌面 GL 上下文（libEGL，X11 Window）。
/// </summary>
/// <remarks>
/// <para>创建流程：<c>eglBindAPI(EGL_OPENGL_API)</c> → <c>eglGetDisplay</c> → <c>eglInitialize</c> →
/// <c>eglChooseConfig</c>（RGBA8 + 深度 24 + 模板 8，桌面 GL 可渲染）→
/// <c>eglCreateContext</c>（客户端版本 3）→ <c>eglCreateWindowSurface</c> → <c>eglMakeCurrent</c> →
/// <see cref="GLNative.LoadModern"/>。</para>
/// <para>Display 由调用方经 <see cref="X11WindowHandle"/> 提供（本类型不引入 X11 绑定）；
/// 仅在 <see cref="OperatingSystem.IsLinux"/> 下被构造，Windows 上永不被实例化。</para>
/// </remarks>
internal sealed unsafe class EglContext : IGlContext
{
    private const uint EglSurfaceType = 0x3033;
    private const uint EglWindowBit = 0x0004;
    private const uint EglPbufferBit = 0x0001;
    private const uint EglRenderableType = 0x3040;
    private const uint EglOpenglBit = 0x0008;
    private const uint EglRedSize = 0x3024;
    private const uint EglGreenSize = 0x3023;
    private const uint EglBlueSize = 0x3022;
    private const uint EglAlphaSize = 0x3021;
    private const uint EglDepthSize = 0x3025;
    private const uint EglStencilSize = 0x3026;
    private const uint EglNone = 0x3038;
    private const uint EglWidth = 0x3057;
    private const uint EglHeight = 0x3056;
    private const uint EglContextClientVersion = 0x3098;
    private const uint EglOpenglApi = 0x30A0;
    private const uint EglOpenglEsApi = 0x3080;
    private const uint EglOpenglEsBit = 0x0040; // EGL_OPENGL_ES3_BIT
    private const uint EglPlatformDeviceExt = 0x313F;   // EGL_PLATFORM_DEVICE_EXT
    private const int EglDeviceExtensions = 0x3055;     // EGL_EXTENSIONS

    private nint _display;
    private nint _surface;
    private nint _context;
    private readonly ILogger? _logger;
    // 共享显示路径下为 false：EGLDisplay 由离屏共享组所有者持有生命周期，本实例仅复用、不参与 eglInitialize/eglTerminate。
    private bool _ownsDisplay;

    public int GlMajor { get; private set; } = 3;
    public int GlMinor { get; private set; } = 3;

    /// <summary>GL 上下文句柄（EGLContext）。作为 <see cref="IGpuDeviceContext"/> 的 DeviceHandle / 共享组句柄。</summary>
    public nint ContextHandle => _context;

    /// <summary>平台显示句柄（EGLDisplay）。作为 <see cref="IGpuDeviceContext"/> 的 ContextHandle（解码侧 interop 用）。</summary>
    public nint PlatformDisplay => _display;

    public EglContext(nint display, nint window, ILogger? logger = null, nint shareContext = default)
    {
        if (display == nint.Zero)
            throw new ArgumentNullException(nameof(display));
        if (window == nint.Zero)
            throw new ArgumentNullException(nameof(window));
        _logger = logger;
        _display = EglNative.eglGetDisplay(display);
        if (_display == nint.Zero)
            throw new InvalidOperationException("EGL：eglGetDisplay 失败（Display 无效）。");

        int major = 0, minor = 0;
        if (EglNative.eglInitialize(_display, &major, &minor) == 0)
            throw new InvalidOperationException($"EGL：eglInitialize 失败（0x{EglNative.eglGetError():X8}）。");
        _ownsDisplay = true;

        CreateOnDisplay(window, shareContext, major, minor);
    }

    /// <summary>内部构造：直接持有已建好的 display/surface/context（离屏 pbuffer 路径复用）。离屏为共享组所有者，拥有 EGLDisplay 生命周期。</summary>
    private EglContext(nint display, nint surface, nint context, ILogger? logger)
    {
        _display = display;
        _surface = surface;
        _context = context;
        _logger = logger;
        _ownsDisplay = true;
        GLNative.LoadModern();
        GlVersionQuery.Query(out int vMajor, out int vMinor);
        if (vMajor != 0) GlMajor = vMajor;
        if (vMinor != 0) GlMinor = vMinor;
        EglNative.eglMakeCurrent(_display, nint.Zero, nint.Zero, nint.Zero);
    }

    /// <summary>私有构造：在已初始化的共享 EGLDisplay 上建上下文（不拥有生命周期）。</summary>
    private EglContext(nint display, ILogger? logger, bool ownsDisplay)
    {
        _display = display;
        _logger = logger;
        _ownsDisplay = ownsDisplay;
    }

    /// <summary>
    /// 在离屏共享组所有者的<b>已初始化</b> EGLDisplay 上创建上屏上下文（不重复 eglGetDisplay/eglInitialize、
    /// 不拥有显示生命周期）。保证与离屏上下文处于<b>同一 EGLDisplay</b>——EGL 共享组仅在同 EGLDisplay 内有效，
    /// 跨显示连接创建的共享上下文会被驱动拒绝（<c>eglCreateContext</c> 返回 NULL）。
    /// </summary>
    internal static EglContext CreateOnSharedDisplay(nint sharedDisplay, nint window, ILogger? logger, nint shareContext)
    {
        if (sharedDisplay == nint.Zero)
            throw new ArgumentNullException(nameof(sharedDisplay));
        if (window == nint.Zero)
            throw new ArgumentNullException(nameof(window));
        var ctx = new EglContext(sharedDisplay, logger, ownsDisplay: false);
        ctx.CreateOnDisplay(window, shareContext, 0, 0);
        return ctx;
    }

    /// <summary>
    /// 选择 EGL config：桌面平台首选 WINDOW|PBUFFER 位；设备显示（EGL_EXT_platform_device）无窗口系统，
    /// WINDOW_BIT config 不存在且 eglChooseConfig 以"0 个匹配 + 无错误"返回——此时降级 PBUFFER-only
    /// （窗口呈现经 CreateOnDisplay 的 pbuffer 回退路径）。
    /// </summary>
    private static unsafe nint ChooseConfigWithWindowPbufferFallback(nint display, uint renderableType, ILogger? logger)
    {
        nint config = nint.Zero;
        int numConfig = 0;

        int[] windowAttribs =
        {
            (int)EglSurfaceType, (int)(EglWindowBit | EglPbufferBit),
            (int)EglRenderableType, (int)renderableType,
            (int)EglRedSize, 8,
            (int)EglGreenSize, 8,
            (int)EglBlueSize, 8,
            (int)EglAlphaSize, 8,
            (int)EglDepthSize, 24,
            (int)EglStencilSize, 8,
            (int)EglNone,
        };
        fixed (int* a = windowAttribs)
        {
            if (EglNative.eglChooseConfig(display, a, &config, 1, &numConfig) != 0 && numConfig > 0)
                return config;
        }

        int[] pbufferAttribs =
        {
            (int)EglSurfaceType, (int)EglPbufferBit,
            (int)EglRenderableType, (int)renderableType,
            (int)EglRedSize, 8,
            (int)EglGreenSize, 8,
            (int)EglBlueSize, 8,
            (int)EglAlphaSize, 8,
            (int)EglDepthSize, 24,
            (int)EglStencilSize, 8,
            (int)EglNone,
        };
        fixed (int* a = pbufferAttribs)
        {
            if (EglNative.eglChooseConfig(display, a, &config, 1, &numConfig) != 0 && numConfig > 0)
            {
                logger?.LogInformation("EGL：无 WINDOW_BIT config（设备显示无窗口系统），降级 PBUFFER-only config。");
                return config;
            }
        }
        return nint.Zero;
    }

    private void CreateOnDisplay(nint window, nint shareContext, int major, int minor)
    {
        if (EglNative.eglBindAPI(EglOpenglApi) == 0)
            throw new InvalidOperationException("EGL：eglBindAPI(EGL_OPENGL_API) 失败（无法绑定桌面 GL）。");

        nint config = ChooseConfigWithWindowPbufferFallback(_display, EglOpenglBit, _logger);
        if (config == nint.Zero)
            throw new InvalidOperationException($"EGL：eglChooseConfig 失败（0x{EglNative.eglGetError():X8}）。");

        int[] ctxAttribs =
        {
            (int)EglContextClientVersion, 3,
            (int)EglNone,
        };
        fixed (int* c = ctxAttribs)
            _context = EglNative.eglCreateContext(_display, config, shareContext, c);
        if (_context == nint.Zero)
            throw new InvalidOperationException($"EGL：eglCreateContext 失败（0x{EglNative.eglGetError():X8}）。");

        int[] surfAttribs = { (int)EglNone };
        fixed (int* s = surfAttribs)
            _surface = EglNative.eglCreateWindowSurface(_display, config, window, s);
        if (_surface == nint.Zero)
        {
            // 设备显示（EGL_EXT_platform_device）无窗口系统：X11 原生窗口在其上非法（EGL_BAD_NATIVE_WINDOW）。
            // 回落 1×1 pbuffer 维持呈现闭环——零拷贝的验证值在帧导入/采样，不在像素出窗；桌面 X11 会话不受影响。
            int surfErr = EglNative.eglGetError();
            int[] pbAttribs = { (int)EglWidth, 1, (int)EglHeight, 1, (int)EglNone };
            fixed (int* p = pbAttribs)
                _surface = EglNative.eglCreatePbufferSurface(_display, config, p);
            if (_surface == nint.Zero)
                throw new InvalidOperationException(
                    $"EGL：eglCreateWindowSurface（0x{surfErr:X8}）与 pbuffer 回落（0x{EglNative.eglGetError():X8}）均失败。");
            _logger?.LogWarning(
                "EGL：eglCreateWindowSurface 失败（0x{Err:X8}），已回落 1×1 pbuffer 呈现（设备显示无窗口系统）。",
                surfErr);
        }

        if (EglNative.eglMakeCurrent(_display, _surface, _surface, _context) == 0)
            throw new InvalidOperationException($"EGL：eglMakeCurrent 失败（0x{EglNative.eglGetError():X8}）。");

        GLNative.LoadModern();
        GlVersionQuery.Query(out int vMajor, out int vMinor);
        if (vMajor != 0) GlMajor = vMajor;
        if (vMinor != 0) GlMinor = vMinor;
        _logger?.LogInformation("EGL：GL 上下文建立成功（EGL {EMajor}.{EMinor}，GL {GMajor}.{GMinor}）。",
            major, minor, GlMajor, GlMinor);

        // 释放：EGL 上下文具线程亲和性。创建于 Attach 线程，渲染在管线线程 Present 中发生，
        // 需在此解绑，使渲染线程可经 MakeCurrent 重新绑定（否则同 WGL 会因已有线程占用而失败）。
        EglNative.eglMakeCurrent(_display, nint.Zero, nint.Zero, nint.Zero);
    }

    /// <summary>
    /// 创建工厂级离屏 GL 上下文（EGL surfaceless 显示 + pbuffer 表面 + 桌面 GL 3.3），作为共享组所有者。
    /// 供 <see cref="OpenGLOffscreenDeviceContext"/> 在解码器初始化前建立，使解码侧产出的 GL 纹理
    /// 经共享组对渲染器 on-screen 上下文可见（零拷贝路径的治本基础）。
    /// <para><b>跨平台一致性</b>：使用 <c>eglGetDisplay(EGL_DEFAULT_DISPLAY)</c> 获取默认显示，
    /// 与渲染器 on-screen 上下文经 <c>X11WindowHandle.Display</c>（同一 X 服务器默认显示）获取的 EGLDisplay 为同一实例，
    /// 故二者可经 shareContext 共享同一共享组（同 D3D11 单适配器前提）。</para>
    /// </summary>
    public static EglContext CreateOffscreen(ILogger? logger = null)
    {
        // Android 仅支持 OpenGL ES：API 绑定与 renderable type 必须用 ES 族——
        // 桌面 GL 位（EGL_OPENGL_API / EGL_OPENGL_BIT）在 Android 平台 EGL 上不被支持，
        // eglChooseConfig 会以 EGL_BAD_ATTRIBUTE 失败。桌面 GL（Windows/Linux WGL/EGL）不受影响。
        bool isGles = OperatingSystem.IsAndroid();
        uint api = isGles ? EglOpenglEsApi : EglOpenglApi;
        uint renderableType = isGles ? EglOpenglEsBit : EglOpenglBit;

        // Linux 同设备零拷贝（LINGFAN_EGL_DEVICE_DRM=1 启用）：经 EGL_EXT_platform_device 选择
        // 硬件 EGL 设备（跳过 EGL_MESA_device_software 软件设备），使离屏上下文与 VAAPI 解码
        // 同设备（Intel iGPU）——dma_buf 导入即可用。默认显示在 Xvfb 下落到 llvmpipe，
        // 无法导入 GPU dma_buf（eglCreateImageKHR 每帧失败 → 逐帧 CPU 回落 → 帧卡）。
        // 任一环节失败回落默认显示（行为与未启用一致）。
        nint display = OperatingSystem.IsLinux()
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LINGFAN_EGL_DEVICE_DRM"))
            ? TryOpenHardwareDeviceDisplay(logger) : nint.Zero;
        if (display == nint.Zero)
        {
            logger?.LogInformation("[EGL-DEVICE] 离屏显示 = 默认（EGL_DEFAULT_DISPLAY；Xvfb 下通常为软件栈，无法导入 GPU dma_buf）。");
            display = EglNative.eglGetDisplay(nint.Zero); // EGL_DEFAULT_DISPLAY
        }
        else
        {
            logger?.LogInformation("[EGL-DEVICE] 离屏显示 = EGL 硬件设备（EGL_EXT_platform_device，与 VAAPI 解码同设备）。");
        }
        if (display == nint.Zero)
            throw new InvalidOperationException("EGL：eglGetDisplay(DEFAULT) 失败（无可用 EGL 显示）。");

        int major = 0, minor = 0;
        if (EglNative.eglInitialize(display, &major, &minor) == 0)
            throw new InvalidOperationException($"EGL：eglInitialize 失败（0x{EglNative.eglGetError():X8}）。");

        // 显示级扩展自报：dma_buf 导入能力（EGL_EXT_image_dma_buf_import[_modifiers]）是零拷贝导入前置。
        // 函数指针可解析 ≠ 本显示支持——Mesa 对不支持导入的显示直接以 EGL_BAD_PARAMETER 拒绝 eglCreateImageKHR。
        nint dispExtsPtr = EglNative.eglQueryString(display, EglDeviceExtensions);
        string dispExts = dispExtsPtr != nint.Zero
            ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(dispExtsPtr) ?? string.Empty
            : string.Empty;
        logger?.LogInformation(
            "[EGL-DEVICE] 离屏显示扩展：dma_buf_import={Import} import_modifiers={Modifiers}",
            dispExts.Contains("EGL_EXT_image_dma_buf_import", StringComparison.Ordinal),
            dispExts.Contains("EGL_EXT_image_dma_buf_import_modifiers", StringComparison.Ordinal));

        if (isGles)
        {
            // Android：ES 是 EGL 默认绑定 API，且实测部分线程上下文下显式 eglBindAPI 会以
            // EGL_BAD_DISPLAY 拒绝——直接跳过（默认绑定即所需 ES），避免无意义的平台差异。
        }
        else if (EglNative.eglBindAPI(api) == 0)
            throw new InvalidOperationException($"EGL：eglBindAPI(0x{api:X4}) 失败（0x{EglNative.eglGetError():X8}，无法绑定所需 GL API）。");

        nint config = ChooseConfigWithWindowPbufferFallback(display, renderableType, logger);
        if (config == nint.Zero)
            throw new InvalidOperationException($"EGL：离屏 eglChooseConfig 失败（0x{EglNative.eglGetError():X8}）。");

        int[] ctxAttribs =
        {
            (int)EglContextClientVersion, 3,
            (int)EglNone,
        };
        nint context;
        fixed (int* c = ctxAttribs)
            context = EglNative.eglCreateContext(display, config, nint.Zero, c);
        if (context == nint.Zero)
            throw new InvalidOperationException($"EGL：离屏 eglCreateContext 失败（0x{EglNative.eglGetError():X8}）。");

        // pbuffer 表面（1×1）：离屏上下文经此 MakeCurrent，无需可见窗口。
        int[] pbAttribs =
        {
            (int)EglWidth, 1,
            (int)EglHeight, 1,
            (int)EglNone,
        };
        nint surface;
        fixed (int* p = pbAttribs)
            surface = EglNative.eglCreatePbufferSurface(display, config, p);
        if (surface == nint.Zero)
            throw new InvalidOperationException($"EGL：离屏 eglCreatePbufferSurface 失败（0x{EglNative.eglGetError():X8}）。");

        if (EglNative.eglMakeCurrent(display, surface, surface, context) == 0)
            throw new InvalidOperationException($"EGL：离屏 eglMakeCurrent 失败（0x{EglNative.eglGetError():X8}）。");

        return new EglContext(display, surface, context, logger);
    }

    /// <summary>EGL 设备的 DRM 渲染节点路径（EGL_DRM_RENDER_NODE_FILE_EXT = 0x3234，需 EGL_EXT_device_drm_render_node）；不支持返回 null。</summary>
    private static unsafe string? QueryDrmRenderNode(nint dev)
    {
        const int EglDrmRenderNodeFileExt = 0x3234;
        nint nodePtr = EglNative.eglQueryDeviceStringEXT(dev, EglDrmRenderNodeFileExt);
        return nodePtr != nint.Zero
            ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(nodePtr)
            : null;
    }

    /// <summary>读 DRM 节点的 sysfs PCI vendor（/sys/class/drm/&lt;node&gt;/device/vendor，0x8086=Intel / 0x10DE=NVIDIA / 0x1002=AMD）；不可读返回 null。</summary>
    private static string? QuerySysfsVendor(string nodePath)
    {
        try
        {
            string name = global::System.IO.Path.GetFileName(nodePath);
            return global::System.IO.File.ReadAllText($"/sys/class/drm/{name}/device/vendor").Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Linux：经 EGL_EXT_platform_device 选择硬件 EGL 设备（跳过 EGL_MESA_device_software 软件设备），
    /// 返回已 Initialize 的 EGLDisplay。任一环节失败返回 Zero（调用方回落默认显示）。
    /// 用途：同设备零拷贝——离屏上下文与 VAAPI 解码（Intel iHD）同 GPU 时 dma_buf 导入才可用；
    /// 默认显示在 Xvfb 下落到 llvmpipe 软件栈，无法导入 GPU dma_buf。
    /// <para>全链路逐步打点（设备数/客户端扩展/逐设备扩展/平台显示/初始化）——设备选择属静默失败
    /// 高发区，无打点即不可诊断。</para>
    /// </summary>
    private static unsafe nint TryOpenHardwareDeviceDisplay(ILogger? logger)
    {
        try
        {
            // 客户端扩展自报（EGL_NO_DISPLAY 查询）：设备枚举/平台设备能力缺一即无选择资格。
            nint clientExts = EglNative.eglQueryString(nint.Zero, EglDeviceExtensions);
            string clientExtStr = clientExts != nint.Zero
                ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(clientExts) ?? string.Empty
                : string.Empty;
            bool hasEnum = clientExtStr.Contains("EGL_EXT_device_enumeration", StringComparison.Ordinal);
            bool hasPlatform = clientExtStr.Contains("EGL_EXT_platform_device", StringComparison.Ordinal);
            logger?.LogInformation(
                "[EGL-DEVICE] 客户端扩展：device_enumeration={Enum} platform_device={Platform}",
                hasEnum, hasPlatform);
            if (!hasEnum || !hasPlatform)
            {
                logger?.LogWarning("[EGL-DEVICE] 客户端不支持 EGL 设备枚举/平台设备，回落默认显示。");
                return nint.Zero;
            }

            int count = 0;
            if (EglNative.eglQueryDevicesEXT(0, null, &count) == 0 || count == 0)
            {
                logger?.LogWarning(
                    "[EGL-DEVICE] eglQueryDevicesEXT 计数查询失败（设备数={Count}，eglErr=0x{Err:X8}），回落默认显示。",
                    count, EglNative.eglGetError());
                return nint.Zero;
            }

            var devices = new nint[count];
            fixed (nint* d = devices)
            {
                if (EglNative.eglQueryDevicesEXT(count, d, &count) == 0)
                {
                    logger?.LogWarning(
                        "[EGL-DEVICE] eglQueryDevicesEXT 枚举失败（eglErr=0x{Err:X8}），回落默认显示。",
                        EglNative.eglGetError());
                    return nint.Zero;
                }
            }

            // 设备选择判据（治原生崩溃）：VAAPI 解码 GPU（Intel iHD）与 EGL 显示必须同设备——跨厂商
            // dma_buf 导入已证不可行，且 NVIDIA 驱动对跨厂商导入直接原生崩溃（复测 3 实证）。
            // GLVND 枚举混排多厂商 EGL 设备（NVIDIA 专有设备可排第一），"首个非软件设备"启发式不可用。
            // 判据链：EGL 设备 → DRM 渲染节点（EGL_EXT_device_drm_render_node）→ sysfs vendor
            // （/sys/class/drm/<node>/device/vendor，0x8086=Intel）；自动模式优先 Intel，
            // LINGFAN_EGL_DEVICE_DRM=renderD129 可显式指定节点（语义自"1"=自动扩展）。
            string envVal = Environment.GetEnvironmentVariable("LINGFAN_EGL_DEVICE_DRM") ?? "1";
            bool explicitNode = envVal != "1" && envVal.Length > 0;

            for (int i = 0; i < devices.Length; i++)
            {
                nint dev = devices[i];
                if (dev == nint.Zero) continue;
                nint extPtr = EglNative.eglQueryDeviceStringEXT(dev, EglDeviceExtensions);
                string exts = extPtr != nint.Zero
                    ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(extPtr) ?? string.Empty
                    : string.Empty;
                string? node = QueryDrmRenderNode(dev);
                string? vendor = node is null ? null : QuerySysfsVendor(node);
                logger?.LogInformation(
                    "[EGL-DEVICE] 设备#{Idx} node={Node} vendor={Vendor} extensions={Exts}",
                    i, node ?? "无", vendor ?? "未知", exts.Length > 120 ? exts[..120] + "…" : exts);

                if (exts.Contains("EGL_MESA_device_software", StringComparison.Ordinal))
                    continue;   // 软件设备（llvmpipe）：无法导入 GPU dma_buf，跳过

                // NVIDIA 专有 EGL 设备（EGL_NV_device_cuda）：跨厂商导入不可行且存在原生崩溃风险，跳过。
                if (exts.Contains("EGL_NV_device_cuda", StringComparison.Ordinal))
                {
                    logger?.LogInformation("[EGL-DEVICE] 设备#{Idx} 为 NVIDIA 专有 EGL 设备，跳过（跨厂商导入不可行）。", i);
                    continue;
                }

                // 显式节点匹配（LINGFAN_EGL_DEVICE_DRM=renderD129）：只取指定节点。
                if (explicitNode && (node is null || !node.EndsWith(envVal, StringComparison.Ordinal)))
                {
                    logger?.LogInformation("[EGL-DEVICE] 设备#{Idx} 非指定节点（要求 {Want}），跳过。", i, envVal);
                    continue;
                }

                // 自动模式优先 Intel（0x8086）；vendor 可读且非 Intel 的设备跳过。
                if (!explicitNode && vendor is not null && vendor != "0x8086")
                {
                    logger?.LogInformation(
                        "[EGL-DEVICE] 设备#{Idx} vendor={Vendor} 非 Intel，跳过（跨厂商导入不可行）。", i, vendor);
                    continue;
                }

                nint disp = EglNative.eglGetPlatformDisplayEXT(EglPlatformDeviceExt, dev, null);
                if (disp == nint.Zero)
                {
                    logger?.LogWarning(
                        "[EGL-DEVICE] 设备#{Idx} eglGetPlatformDisplayEXT 失败（eglErr=0x{Err:X8}），尝试下一设备。",
                        i, EglNative.eglGetError());
                    continue;
                }

                int major = 0, minor = 0;
                if (EglNative.eglInitialize(disp, &major, &minor) != 0)
                {
                    logger?.LogInformation(
                        "[EGL-DEVICE] 已选择硬件设备 #{Idx}（EGL {Major}.{Minor}）。", i, major, minor);
                    return disp;
                }
                logger?.LogWarning(
                    "[EGL-DEVICE] 设备#{Idx} eglInitialize 失败（eglErr=0x{Err:X8}），尝试下一设备。",
                    i, EglNative.eglGetError());
                EglNative.eglTerminate(disp);   // 初始化失败：换下一个设备
            }

            logger?.LogWarning("[EGL-DEVICE] 无可用硬件 EGL 设备（全部跳过/失败），回落默认显示。");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[EGL-DEVICE] 设备选择异常，回落默认显示。");
        }
        return nint.Zero;
    }

    public void MakeCurrent()
    {
        if (_context != nint.Zero)
            EglNative.eglMakeCurrent(_display, _surface, _surface, _context);
    }

    public void ReleaseCurrent() => EglNative.eglMakeCurrent(_display, nint.Zero, nint.Zero, nint.Zero);

    public void SwapBuffers() => EglNative.eglSwapBuffers(_display, _surface);

    public void Dispose()
    {
        if (_display != nint.Zero)
        {
            EglNative.eglMakeCurrent(_display, nint.Zero, nint.Zero, nint.Zero);
            if (_surface != nint.Zero) EglNative.eglDestroySurface(_display, _surface);
            if (_context != nint.Zero) EglNative.eglDestroyContext(_display, _context);
            // 共享显示路径（上屏复用离屏所有者 EGLDisplay）不在此终止显示——生命周期由离屏所有者持有。
            if (_ownsDisplay) EglNative.eglTerminate(_display);
        }
        _surface = nint.Zero;
        _context = nint.Zero;
        _display = nint.Zero;
    }
}

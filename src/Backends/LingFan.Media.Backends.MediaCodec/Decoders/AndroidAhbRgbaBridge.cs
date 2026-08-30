using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Android.Graphics;       // SurfaceTexture / Surface
using Android.Views;
using Microsoft.Extensions.Logging;
using LingFan.Media.Abstractions;
using LingFan.Media.GPUShare.Vulkan; // AndroidHardwareBufferFrameResource（跨工程平台帧 DTO）

namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// Android GLES/EGL 桥接：把 MediaCodec 经 <see cref="SurfaceTexture"/> 产出的 OES 外部纹理
/// （驱动已在 GPU 内完成 YUV→RGB 色彩转换）渲染进 <b>RGBA AHardwareBuffer</b>，再交 Vulkan 渲染器
/// 以「普通 RGBA 纹理」采样上屏（<see cref="VulkanRgbaToRgbaConverter"/> 路径，Adreno 兼容、不崩）。
/// </summary>
/// <remarks>
/// <para><b>为何绕开 YCbCr 采样</b>：Adreno 650（Android 12 实测）对「MediaCodec 产出的 YUV AHB + Vulkan
/// <c>VkSamplerYcbcrConversion</c> 采样」报 <c>formatFeatures=0x8FF081</c>（缺
/// <c>VK_FORMAT_FEATURE_SAMPLED_IMAGE_YCBCR_CONVERSION_BIT</c>），驱动走未定义行为 → <c>SIGSEGV fault addr 0x0</c>。
/// 业界零拷贝范式（ExoPlayer / Chromium）从不让 Vulkan 直接 YCbCr 采样 AHB，而是让 GL 在 GPU 内做
/// YUV→RGB 再产 RGBA AHB；本桥接即此范式：MediaCodec→SurfaceTexture(OES 外部纹理)→GL 渲染进 RGBA AHB→Vulkan。</para>
/// <para><b>零 CPU 像素拷贝</b>：从解码到上屏全程 GPU；AHB 为唯一跨 API 媒介（解码侧 GL 写、渲染侧 Vulkan 读）。</para>
/// <para><b>EGL 上下文归属（治根T · 本类最关键契约）</b>：EGL 规范要求一个上下文同一时刻只被一个线程持有。
/// 本类自第十一轮实证起改为 <b>专用常驻 GL 线程</b>独占该 EGL 上下文：<see cref="Initialize"/> 在 GL 线程上
/// 建上下文并<b>永不释放</b>；<see cref="ConvertLatest"/> 仅把「闩帧+渲染进 AHB」工作经 GL 线程串行化执行、
/// 调用方线程阻塞等结果。上下文永不离其 owner 线程 → 彻底消除此前「调用线程随 .NET 续体迁移导致上下文跨线程
/// make/break 竞态、Adreno 每线程 GL 状态未就绪即原生空指针」的崩溃（首帧崩/二帧成的非确定性 SIGSEGV）。</para>
/// <para><b>绑定来源</b>：EGL/GLES 经 <c>[LibraryImport]</c> 直连 <c>libEGL.so</c> / <c>libGLESv2.so</c>（Android 裸库名，
/// 与 Renderers.OpenGLES 的 <c>GlesNative</c> 同范式）；AHardwareBuffer 经 <c>libandroid.so</c>。属图形底层原语
/// （非媒体 API），AOT 源生成、零反射；符合 2026-08-22 架构裁定（Android 后端媒体 API 走托管绑定，
/// 仅图形原语例外，与解码器既有 <c>AHardwareBuffer_fromHardwareBuffer</c> carve-out 一致）。</para>
/// <para><b>DIP</b>：本类仅依赖 Abstractions + GPUShare.Vulkan（平台帧 DTO），不反向引用任何 Renderer，依赖倒置合规。</para>
/// </remarks>
internal sealed unsafe partial class AndroidAhbRgbaBridge : IDisposable
{
    // ── EGL 常量 ──
    private const nint EglNoContext = 0;
    private const nint EglDefaultDisplay = 0;
    private const int EglOpenglEsApi = 0x30A0;
    private const int EglContextClientVersion = 0x3098;
    private const int EglSurfaceType = 0x3033;
    private const int EglPbufferBit = 0x0001;
    private const int EglRenderableType = 0x3040;
    private const int EglOpenglEs3Bit = 0x00000040;
    private const int EglOpenglEs2Bit = 0x00000004;
    private const int EglRedSize = 0x3024;
    private const int EglGreenSize = 0x3023;
    private const int EglBlueSize = 0x3022;
    private const int EglAlphaSize = 0x3021;
    private const int EglNone = 0x3038;
    private const int EglWidth = 0x3057;
    private const int EglHeight = 0x3056;
    private const int EglNativeBufferAndroid = 0x3140;       // EGL_NATIVE_BUFFER_ANDROID
    private const int EglImagePreservedKhr = 0x30D2;

    // ── GLES 常量 ──
    private const uint GlTextureExternalOes = 0x8D65;
    private const uint GlFramebuffer = 0x8D40;
    private const uint GlColorAttachment0 = 0x8CE0;
    private const uint GlFramebufferComplete = 0x8CD5;
    private const uint GlTriangles = 0x0004;
    private const uint GlTexture2D = 0x0DE1;
    private const uint GlTextureMinFilter = 0x2801;
    private const uint GlTextureMagFilter = 0x2800;
    private const uint GlTextureWrapS = 0x2802;
    private const uint GlTextureWrapT = 0x2803;
    private const uint GlClampToEdge = 0x812F;
    private const uint GlLinear = 0x2601;
    private const uint GlVertexShader = 0x8B31;
    private const uint GlFragmentShader = 0x8B30;
    private const uint GlCompileStatus = 0x8B81;
    private const uint GlLinkStatus = 0x8B82;
    private const uint GlTexture0 = 0x84C0;
    private const uint GlRgba = 0x1908;
    private const uint GlArrayBuffer = 0x8892;
    private const uint GlStaticDraw = 0x88E4;
    private const uint GlFloat = 0x1406;
    private const uint GlTriangleStrip = 0x0005;

    // ── AHardwareBuffer 常量 ──
    private const uint AhbFormatR8G8B8A8Unorm = 1; // AHARDWAREBUFFER_FORMAT_R8G8B8A8_UNORM
    private const ulong AhbUsageGpuSampledImage = 1UL << 8;
    private const ulong AhbUsageGpuFramebuffer = 1UL << 9;

    private readonly ILogger? _logger;
    private readonly int _width;
    private readonly int _height;

    // EGL/GLES 对象全部在专用 GL 线程上创建并使用，永不在其他线程触碰（治根T）。
    private nint _eglDisplay;
    private nint _eglContext;
    private nint _eglSurface;
    private volatile bool _initialized;

    private uint _oesTex;          // SurfaceTexture 绑定的 OES 外部纹理
    private SurfaceTexture? _surfaceTexture;
    private Surface? _surface;

    private uint _program;
    private int _uTexTransform;
    private int _uTex;
    private uint _vbo;            // 全屏 quad 顶点缓冲（pos.xy + uv.xy 交错）
    private int _aPosLoc;        // 属性 aPos 位置
    private int _aUvLoc;         // 属性 aUV 位置

    private uint _scratchTex;      // 每帧重绑 EGLImage（AHB）的 GL 纹理
    private uint _fbo;            // FBO（附着 scratchTex）

    // 扩展函数（运行时经 eglGetProcAddress 解析，AOT 友好 delegate*）。
    private delegate* unmanaged[Cdecl]<nint, nint> _eglGetNativeClientBufferAndroid = null;
    private delegate* unmanaged[Cdecl]<nint, nint, uint, nint, nint, nint> _eglCreateImageKhr = null;
    private delegate* unmanaged[Cdecl]<nint, nint, uint> _eglDestroyImageKhr = null;
    private delegate* unmanaged[Cdecl]<uint, nint, void> _glEglImageTargetTexture2Does = null;

    // ── 治根T：专用 GL 线程与跨线程产帧队列 ──
    private Thread? _glThread;
    private readonly ManualResetEvent _initDone = new(false);   // GL 线程建上下文完成后置位
    private readonly ManualResetEvent _workSignal = new(false); // 有产帧/停止请求时唤醒 GL 线程
    private readonly ConcurrentQueue<FrameRequest> _requestQueue = new();
    private Exception? _initError;
    private bool _disposed;

    /// <summary>跨线程产帧请求：GL 线程消费后通过 <see cref="FrameRequest.Tcs"/> 回传 AHB 指针。</summary>
    private sealed class FrameRequest
    {
        public nint Result;
        public bool IsStop;
        public readonly System.Threading.Tasks.TaskCompletionSource<nint> Tcs = new();
    }

    public AndroidAhbRgbaBridge(int width, int height, ILogger? logger)
    {
        _width = width > 0 ? width : 1920;
        _height = height > 0 ? height : 1080;
        _logger = logger;
    }

    /// <summary>桥接产出 Surface（供 MediaCodec 配置为输出目标）。</summary>
    public Surface? OutputSurface => _surface;

    /// <summary>桥接配置的帧尺寸（AHB 渲染目标尺寸）。</summary>
    public int FrameWidth => _width;
    public int FrameHeight => _height;

    /// <summary>
    /// 启动专用 GL 线程并在其上建立 EGL/GLES 上下文、OES 纹理、SurfaceTexture 与渲染管线；失败抛
    /// <see cref="NotSupportedException"/>（调用方据此回退 ByteBuffer CPU 路径）。
    /// 关键：GL 线程建完上下文后<b>常驻持有、永不释放</b>（治根T），所有 GL 工作仅在该线程执行。
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;

        _glThread = new Thread(GlThreadEntry)
        {
            IsBackground = true,
            Name = "LFM-AHB-GL",
        };
        _glThread.Start();
        _initDone.WaitOne();

        if (_initError is not null)
            throw new NotSupportedException($"[ANDROID-AHB] GL 线程初始化失败：{_initError.Message}");

        _logger?.LogInformation(
            "[ANDROID-AHB] GLES/EGL 桥接就绪（专用 GL 线程 #{Tid}）：EGLDisplay=0x{Disp} OES纹理={Oes} SurfaceTexture+Surface 已建，{W}x{H} RGBA AHB 输出路径。",
            _glThread.ManagedThreadId, (ulong)_eglDisplay, _oesTex, _width, _height);
    }

    /// <summary>GL 线程入口：建上下文 → 常驻运行循环（串行消费产帧/停止请求）。</summary>
    private void GlThreadEntry()
    {
        try
        {
            SetupGlOnThisThread();   // 在 GL 线程上建 EGL 上下文、纹理、SurfaceTexture、管线（上下文保持 current）
            _initialized = true;
            _initDone.Set();
            RunLoop();               // 常驻：串行化产帧，永不在帧间释放上下文
        }
        catch (Exception ex)
        {
            _initError = ex;
            _initDone.Set();         // 解锁 Initialize（即使失败也要置位，避免死等）
            _logger?.LogWarning(ex, "[ANDROID-AHB] GL 线程初始化/运行异常，回退 CPU 路径。");
        }
    }

    /// <summary>GL 线程主循环：等待请求，串行消费（产帧或停止）。</summary>
    private void RunLoop()
    {
        while (true)
        {
            _workSignal.WaitOne();
            _workSignal.Reset();
            while (_requestQueue.TryDequeue(out var req))
            {
                if (req.IsStop)
                {
                    TeardownGlOnThisThread();
                    return;
                }
                try
                {
                    req.Result = ProduceFrameOnThisThread();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[ANDROID-AHB] 产帧异常，返回 0（丢弃该帧）。");
                    req.Result = nint.Zero;
                }
                req.Tcs.TrySetResult(req.Result);
            }
        }
    }

    /// <summary>
    /// 闩住最新一帧并渲染进新建的 RGBA AHardwareBuffer。调用方线程经 GL 线程串行化执行——
    /// 本方法只在调用线程入队请求并阻塞等待结果，真正的 GL 工作在 GL 线程（上下文常驻）上完成。
    /// 返回 AHardwareBuffer*（引用所有权移交调用方；帧资源 Dispose 时释放）；失败返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    public nint ConvertLatest()
    {
        if (!_initialized) return nint.Zero;

        var req = new FrameRequest();
        _requestQueue.Enqueue(req);
        _workSignal.Set();
        // 阻塞等 GL 线程产帧结果（调用线程为解码读循环后台线程，阻塞无死锁风险：GL 线程不回调调用方）。
        return req.Tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// 在 GL 线程上建 EGL 上下文与全部 GL/SurfaceTexture 资源；末尾保持上下文 current（不释放）。
    /// 任何一步失败抛异常 → 由 <see cref="GlThreadEntry"/> 捕获并回退。
    /// </summary>
    private void SetupGlOnThisThread()
    {
        _eglDisplay = EglGetDisplay(EglDefaultDisplay);
        if (_eglDisplay == nint.Zero)
            throw new NotSupportedException("[ANDROID-AHB] eglGetDisplay 失败。");
        if (EglInitialize(_eglDisplay, null, null) == 0)
            throw new NotSupportedException($"[ANDROID-AHB] eglInitialize 失败 0x{EglGetError():X8}。");

        ResolveExtensions();

        int[] cfgAttrs =
        {
            EglSurfaceType, EglPbufferBit,
            EglRenderableType, EglOpenglEs2Bit,
            EglRedSize, 8, EglGreenSize, 8, EglBlueSize, 8,
            EglNone,
        };
        nint config = nint.Zero;
        int numConfigs = 0;
        fixed (int* a = cfgAttrs)
        {
            if (EglChooseConfig(_eglDisplay, a, &config, 1, &numConfigs) == 0 || numConfigs == 0)
                throw new NotSupportedException($"[ANDROID-AHB] eglChooseConfig 失败 0x{EglGetError():X8}。");
        }

        EglBindApi(EglOpenglEsApi);
        int[] ctxAttrs = { EglContextClientVersion, 2, EglNone };
        fixed (int* c = ctxAttrs)
            _eglContext = EglCreateContext(_eglDisplay, config, EglNoContext, c);
        if (_eglContext == nint.Zero)
            throw new NotSupportedException($"[ANDROID-AHB] eglCreateContext 失败 0x{EglGetError():X8}。");

        int[] pbufAttrs = { EglWidth, 1, EglHeight, 1, EglNone };
        fixed (int* p = pbufAttrs)
            _eglSurface = EglCreatePbufferSurface(_eglDisplay, config, p);
        if (_eglSurface == nint.Zero)
            throw new NotSupportedException($"[ANDROID-AHB] eglCreatePbufferSurface 失败 0x{EglGetError():X8}。");

        if (EglMakeCurrent(_eglDisplay, _eglSurface, _eglSurface, _eglContext) == 0)
            throw new NotSupportedException($"[ANDROID-AHB] eglMakeCurrent 失败 0x{EglGetError():X8}。");

        // OES 外部纹理（SurfaceTexture 将绑定到此纹理名；纹理归属本 GL 线程的上下文）。
        uint oes = 0;
        GlGenTextures(1, &oes);
        _oesTex = oes;
        GlBindTexture(GlTextureExternalOes, _oesTex);
        GlTexParameteri(GlTextureExternalOes, GlTextureMinFilter, (int)GlLinear);
        GlTexParameteri(GlTextureExternalOes, GlTextureMagFilter, (int)GlLinear);
        GlTexParameteri(GlTextureExternalOes, GlTextureWrapS, (int)GlClampToEdge);
        GlTexParameteri(GlTextureExternalOes, GlTextureWrapT, (int)GlClampToEdge);

        // SurfaceTexture 消费 MediaCodec 输出（其 GL 纹理须在本 EGL 上下文创建 —— 正是上面 _oesTex）。
        _surfaceTexture = new SurfaceTexture((int)_oesTex);
        _surfaceTexture.SetDefaultBufferSize(_width, _height);
        _surface = new Surface(_surfaceTexture);

        // 编译直通 shader（OES 外部纹理 → RGBA 输出）。
        BuildProgram();

        // 复用纹理/FBO（每帧仅重绑 EGLImage）。
        uint st = 0, fb = 0;
        GlGenTextures(1, &st);
        _scratchTex = st;
        GlGenFramebuffers(1, &fb);
        _fbo = fb;

        // 上下文保持 current：GL 线程常驻持有，不在此释放（治根T）。
    }

    /// <summary>
    /// 在 GL 线程上闩取最新帧并渲染进 RGBA AHardwareBuffer。上下文此时已 current（建上下文后从未释放），
    /// 故此处<b>不做</b> eglMakeCurrent / 不做 finally 释放上下文——仅失败路径清理本帧的 AHB / EGLImage 资源。
    /// </summary>
    private nint ProduceFrameOnThisThread()
    {
        _logger?.LogInformation("[ANDROID-AHB-TRACE] ①GL线程产帧（托管线程={Tid}）", Environment.CurrentManagedThreadId);

        // 1) 分配 RGBA AHB（GPU 采样 + 帧缓冲写）。
        AHardwareBufferDesc desc = new()
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Layers = 1,
            Format = AhbFormatR8G8B8A8Unorm,
            Usage = AhbUsageGpuSampledImage | AhbUsageGpuFramebuffer,
        };
        nint ahb = nint.Zero;
        nint eglImage = nint.Zero;
        bool ok = false;
        try
        {
            if (AHardwareBufferAllocate(&desc, &ahb) != 0 || ahb == nint.Zero)
            {
                _logger?.LogWarning("[ANDROID-AHB] AHardwareBuffer_allocate(RGBA8) 失败。");
                return nint.Zero;
            }
            _logger?.LogInformation("[ANDROID-AHB-TRACE] ④AHB 分配成功 ahb=0x{Ahb} 托管线程={Tid}", (ulong)ahb, Environment.CurrentManagedThreadId);

            // 2) AHB → EGLClientBuffer → EGLImage。
            nint clientBuf = _eglGetNativeClientBufferAndroid(ahb);
            if (clientBuf == nint.Zero)
            {
                _logger?.LogWarning("[ANDROID-AHB] eglGetNativeClientBufferANDROID 失败。");
                return nint.Zero;
            }
            _logger?.LogInformation("[ANDROID-AHB-TRACE] ⑤eglGetNativeClientBuffer 成功 clientBuf=0x{Cb}", (ulong)clientBuf);
            int[] imgAttrs = { EglImagePreservedKhr, 1, EglNone };
            fixed (int* ia = imgAttrs)
                eglImage = _eglCreateImageKhr(_eglDisplay, EglNoContext, (uint)EglNativeBufferAndroid, clientBuf, (nint)ia);
            if (eglImage == nint.Zero)
            {
                _logger?.LogWarning("[ANDROID-AHB] eglCreateImageKHR(AHB) 失败 0x{EglErr:X8}。", (ulong)EglGetError());
                return nint.Zero;
            }
            _logger?.LogInformation("[ANDROID-AHB-TRACE] ⑥eglCreateImageKHR 成功 eglImage=0x{Img}", (ulong)eglImage);

            // 3) 把 EGLImage 绑到复用纹理，再附到 FBO。
            GlBindTexture(GlTexture2D, _scratchTex);
            _glEglImageTargetTexture2Does(GlTexture2D, eglImage);
            GlTexParameteri(GlTexture2D, GlTextureMinFilter, (int)GlLinear);
            GlTexParameteri(GlTexture2D, GlTextureMagFilter, (int)GlLinear);
            GlTexParameteri(GlTexture2D, GlTextureWrapS, (int)GlClampToEdge);
            GlTexParameteri(GlTexture2D, GlTextureWrapT, (int)GlClampToEdge);
            GlBindFramebuffer(GlFramebuffer, _fbo);
            GlFramebufferTexture2D(GlFramebuffer, GlColorAttachment0, GlTexture2D, _scratchTex, 0);
            uint glErrTex = GlGetError();
            _logger?.LogInformation("[ANDROID-AHB-TRACE] ⑦纹理/FBO 绑定完成 glErr=0x{GlErr:X8}", glErrTex);
            if (GlCheckFramebufferStatus(GlFramebuffer) != GlFramebufferComplete)
            {
                _logger?.LogWarning("[ANDROID-AHB] FBO 不完整（AHB→GL 纹理绑定失败），GL 错误=0x{GlErr:X8}。", (uint)GlGetError());
                return nint.Zero;
            }
            _logger?.LogInformation("[ANDROID-AHB-TRACE] ⑧FBO 完整");

            // 4) 闩帧：updateTexImage 阻塞等解码 fence，把最新帧写入 _oesTex（OES 外部纹理，驱动已完成 YUV→RGB）。
            // 契约：拥有该纹理的 EGL 上下文（本 GL 线程）必须 current —— 治根T 保证恒满足，无跨线程问题。
            try
            {
                _surfaceTexture!.UpdateTexImage();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ANDROID-AHB] SurfaceTexture.updateTexImage 失败。");
                return nint.Zero;
            }
            _logger?.LogInformation("[ANDROID-AHB-TRACE] ⑨updateTexImage 成功");
            float[] mtx = new float[16];
            _surfaceTexture.GetTransformMatrix(mtx);

            // 5) 渲染 OES→AHB FBO（直出 RGBA）。
            GlViewport(0, 0, _width, _height);
            GlUseProgram(_program);
            fixed (float* pMtx = mtx)
                GlUniformMatrix4Fv(_uTexTransform, 1, 0, pMtx);
            GlUniform1I(_uTex, 0);
            GlActiveTexture(GlTexture0);
            GlBindTexture(GlTextureExternalOes, _oesTex);

            // 绑定全屏 quad VBO 并设置顶点属性（pos.xy + uv.xy 交错，stride=16 字节）。
            GlBindBuffer(GlArrayBuffer, _vbo);
            GlEnableVertexAttribArray((uint)_aPosLoc);
            GlVertexAttribPointer((uint)_aPosLoc, 2, GlFloat, 0, 16, (nint)0);
            GlEnableVertexAttribArray((uint)_aUvLoc);
            GlVertexAttribPointer((uint)_aUvLoc, 2, GlFloat, 0, 16, (nint)8);
            GlDrawArrays(GlTriangleStrip, 0, 4);
            uint glErrDraw = GlGetError();
            _logger?.LogInformation("[ANDROID-AHB-TRACE] ⑩GlDrawArrays 完成 glErr=0x{GlErr:X8}", glErrDraw);
            GlBindBuffer(GlArrayBuffer, 0);
            GlFinish(); // 保证 AHB 写入对后续 Vulkan 导入可见（跨 API 同步）。
            _logger?.LogInformation("[ANDROID-AHB-TRACE] ⑪glFinish 完成");

            // 6) EGLImage 仅渲染期需要，销毁（AHB 内容已落盘，引用仍由帧资源持有）。
            _eglDestroyImageKhr(_eglDisplay, eglImage);
            eglImage = nint.Zero;
            _logger?.LogInformation("[ANDROID-AHB-TRACE] ⑫eglDestroyImageKHR 完成，准备返回 ahb=0x{Ahb}", (ulong)ahb);

            _logger?.LogInformation("[ANDROID-AHB] 帧渲染进 RGBA AHB 完成 {W}x{H}", _width, _height);
            ok = true;
            return ahb; // 引用所有权移交调用方
        }
        finally
        {
            // 仅失败时清理本帧资源；成功路径 ahb 已移交调用方、eglImage 已销毁（置零）。
            if (!ok)
            {
                if (eglImage != nint.Zero) _eglDestroyImageKhr(_eglDisplay, eglImage);
                if (ahb != nint.Zero) AHardwareBufferRelease(ahb);
            }
        }
    }

    // ── 私有：shader 管线 ──
    private void BuildProgram()
    {
        uint vs = CompileShader(GlVertexShader, VertexSource);
        uint fs = CompileShader(GlFragmentShader, FragmentSource);
        uint prog = GlCreateProgram();
        GlAttachShader(prog, vs);
        GlAttachShader(prog, fs);
        GlLinkProgram(prog);
        int linked = 0;
        GlGetProgramIv(prog, GlLinkStatus, &linked);
        if (linked == 0)
        {
            _logger?.LogWarning("[ANDROID-AHB] 着色器链接失败。");
            throw new NotSupportedException("[ANDROID-AHB] GL 程序链接失败。");
        }
        GlDeleteShader(vs);
        GlDeleteShader(fs);
        _program = prog;
        _uTexTransform = GlGetUniformLocation(prog, "uTexTransform");
        _uTex = GlGetUniformLocation(prog, "uTex");

        // 全屏 quad VBO（pos.xy + uv.xy 交错，TRIANGLE_STRIP 4 顶点）。
        // -1,-1 / 1,-1 / -1,1 / 1,1（裁剪空间全覆盖），uv 0..1 经 uTexTransform 映射。
        float[] quad =
        {
            -1f, -1f, 0f, 0f,
             1f, -1f, 1f, 0f,
            -1f,  1f, 0f, 1f,
             1f,  1f, 1f, 1f,
        };
        uint vbo = 0;
        GlGenBuffers(1, &vbo);
        _vbo = vbo;
        GlBindBuffer(GlArrayBuffer, _vbo);
        fixed (float* q = quad)
            GlBufferData(GlArrayBuffer, quad.Length * sizeof(float), q, GlStaticDraw);
        GlBindBuffer(GlArrayBuffer, 0);
        _aPosLoc = GlGetAttribLocation(prog, "aPos");
        _aUvLoc = GlGetAttribLocation(prog, "aUV");
    }

    private uint CompileShader(uint type, string src)
    {
        uint sh = GlCreateShader(type);
        byte[] bytes = Encoding.UTF8.GetBytes(src + "\0");
        fixed (byte* p = bytes)
        {
            nint pStr = (nint)p;
            int len = -1;
            GlShaderSource(sh, 1, &pStr, &len);
        }
        GlCompileShader(sh);
        int ok = 0;
        GlGetShaderIv(sh, GlCompileStatus, &ok);
        if (ok == 0)
            _logger?.LogWarning("[ANDROID-AHB] 着色器编译失败（type=0x{Type:X}）。", type);
        return sh;
    }

    private void ResolveExtensions()
    {
        nint p;
        p = EglGetProcAddress("eglGetNativeClientBufferANDROID");
        if (p == nint.Zero) throw new NotSupportedException("[ANDROID-AHB] 缺失 eglGetNativeClientBufferANDROID。");
        _eglGetNativeClientBufferAndroid = (delegate* unmanaged[Cdecl]<nint, nint>)p;

        p = EglGetProcAddress("eglCreateImageKHR");
        if (p == nint.Zero) throw new NotSupportedException("[ANDROID-AHB] 缺失 eglCreateImageKHR。");
        _eglCreateImageKhr = (delegate* unmanaged[Cdecl]<nint, nint, uint, nint, nint, nint>)p;

        p = EglGetProcAddress("eglDestroyImageKHR");
        if (p == nint.Zero) throw new NotSupportedException("[ANDROID-AHB] 缺失 eglDestroyImageKHR。");
        _eglDestroyImageKhr = (delegate* unmanaged[Cdecl]<nint, nint, uint>)p;

        p = EglGetProcAddress("glEGLImageTargetTexture2DOES");
        if (p == nint.Zero) throw new NotSupportedException("[ANDROID-AHB] 缺失 glEGLImageTargetTexture2DOES。");
        _glEglImageTargetTexture2Does = (delegate* unmanaged[Cdecl]<uint, nint, void>)p;
    }

    // ⚠️ GLSL 铁律：#version 必须是源码绝对第一行（列 0、无前导空白），否则驱动忽略→默认按 ES 1.00 编译，
    // 导致 gl_VertexIndex undeclared、samplerExternalOES 扩展名错配。故用字符串拼接而非原始字面量（后者会残留缩进空格）。
    // 采用 Android 最稳的 GLES 2.0 / ES 1.00 范式（ExoPlayer/Grafika 同款）：全屏 quad 走顶点属性，不依赖 gl_VertexIndex（GLES3-only）。
    // 省略 #version 即默认 ES 1.00；samplerExternalOES 在 ES 1.00 下用 GL_OES_EGL_image_external 扩展（非 _essl3）。
    private const string VertexSource =
        "attribute vec2 aPos;\n"
      + "attribute vec2 aUV;\n"
      + "uniform mat4 uTexTransform;\n"
      + "varying vec2 vUV;\n"
      + "void main() {\n"
      + "    vUV = (uTexTransform * vec4(aUV, 0.0, 1.0)).xy;\n"
      + "    gl_Position = vec4(aPos * 2.0 - 1.0, 0.0, 1.0);\n"
      + "}\n";

    private const string FragmentSource =
        "#extension GL_OES_EGL_image_external : require\n"
      + "precision mediump float;\n"
      + "uniform samplerExternalOES uTex;\n"
      + "varying vec2 vUV;\n"
      + "void main() {\n"
      + "    gl_FragColor = texture2D(uTex, vUV);\n"
      + "}\n";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 先通知 GL 线程停止并等其完成销毁（销毁在 GL 线程、上下文 current 下进行，确保 GL 对象正确释放）。
        if (_initialized && _glThread is { IsAlive: true })
        {
            _initialized = false; // 抢占：阻止任何新 ConvertLatest 入队（已在等待的请求由 GL 线程照常处理）
            _requestQueue.Enqueue(new FrameRequest { IsStop = true });
            _workSignal.Set();
            _glThread.Join();
        }
        _glThread = null;

        // Java 层 Surface/SurfaceTexture 不依赖 GL 上下文，停止后于任意线程释放即可。
        _surface?.Dispose();
        _surfaceTexture?.Dispose();
        _surface = null;
        _surfaceTexture = null;
    }

    /// <summary>在 GL 线程上销毁全部 GL 对象并终止 EGL（上下文此时 current）。</summary>
    private void TeardownGlOnThisThread()
    {
        if (_program != 0) GlDeleteProgram(_program);
        if (_vbo != 0) { uint vb = _vbo; GlDeleteBuffers(1, &vb); }
        // 取字段副本入局部再取地址（跨环境安全写法：本机 Roslyn 对 &字段 报 CS0212，须走局部变量）。
        if (_scratchTex != 0) { uint sTex = _scratchTex; GlDeleteTextures(1, &sTex); }
        if (_oesTex != 0) { uint oTex = _oesTex; GlDeleteTextures(1, &oTex); }
        if (_fbo != 0) { uint fb = _fbo; GlDeleteFramebuffers(1, &fb); }

        if (_eglDisplay != nint.Zero)
        {
            // 线程即将退出，上下文随之消亡；显式解绑再销毁，符合 EGL 规范。
            EglMakeCurrent(_eglDisplay, nint.Zero, nint.Zero, nint.Zero);
            if (_eglContext != nint.Zero) EglDestroyContext(_eglDisplay, _eglContext);
            if (_eglSurface != nint.Zero) EglDestroySurface(_eglDisplay, _eglSurface);
            EglTerminate(_eglDisplay);
        }
        _logger?.LogInformation("[ANDROID-AHB] GL 线程已销毁上下文并退出。");
    }

    // ── AHardwareBuffer 描述结构（与 NDK AHardwareBuffer_Desc 二进制兼容）──
    [StructLayout(LayoutKind.Sequential)]
    private struct AHardwareBufferDesc
    {
        public uint Width;
        public uint Height;
        public uint Layers;
        public uint Format;
        public ulong Usage;
        public uint Stride;
        public uint Rfu0;
        public uint Rfu1;
    }

    // ── EGL core（libEGL.so，Android 裸库）──
    [LibraryImport("libEGL.so", EntryPoint = "eglGetDisplay")]
    private static partial nint EglGetDisplay(nint displayId);

    [LibraryImport("libEGL.so", EntryPoint = "eglInitialize")]
    private static partial int EglInitialize(nint display, int* major, int* minor);

    [LibraryImport("libEGL.so", EntryPoint = "eglTerminate")]
    private static partial int EglTerminate(nint display);

    [LibraryImport("libEGL.so", EntryPoint = "eglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint EglGetProcAddress(string procname);

    [LibraryImport("libEGL.so", EntryPoint = "eglChooseConfig")]
    private static partial int EglChooseConfig(nint display, int* attribList, nint* configs, int configSize, int* numConfig);

    [LibraryImport("libEGL.so", EntryPoint = "eglBindAPI")]
    private static partial int EglBindApi(nint api);

    [LibraryImport("libEGL.so", EntryPoint = "eglCreateContext")]
    private static partial nint EglCreateContext(nint display, nint config, nint shareContext, int* attribList);

    [LibraryImport("libEGL.so", EntryPoint = "eglCreatePbufferSurface")]
    private static partial nint EglCreatePbufferSurface(nint display, nint config, int* attribList);

    [LibraryImport("libEGL.so", EntryPoint = "eglMakeCurrent")]
    private static partial int EglMakeCurrent(nint display, nint draw, nint read, nint context);

    [LibraryImport("libEGL.so", EntryPoint = "eglGetCurrentContext")]
    private static partial nint EglGetCurrentContext();

    [LibraryImport("libEGL.so", EntryPoint = "eglDestroyContext")]
    private static partial int EglDestroyContext(nint display, nint context);

    [LibraryImport("libEGL.so", EntryPoint = "eglDestroySurface")]
    private static partial int EglDestroySurface(nint display, nint surface);

    [LibraryImport("libEGL.so", EntryPoint = "eglGetError")]
    private static partial uint EglGetError();

    // ── GLES core（libGLESv2.so）──
    [LibraryImport("libGLESv2.so", EntryPoint = "glGetError")]
    private static partial uint GlGetError();

    [LibraryImport("libGLESv2.so", EntryPoint = "glGenTextures")]
    private static partial void GlGenTextures(int n, uint* textures);

    [LibraryImport("libGLESv2.so", EntryPoint = "glDeleteTextures")]
    private static partial void GlDeleteTextures(int n, uint* textures);

    [LibraryImport("libGLESv2.so", EntryPoint = "glGenBuffers")]
    private static partial void GlGenBuffers(int n, uint* buffers);

    [LibraryImport("libGLESv2.so", EntryPoint = "glDeleteBuffers")]
    private static partial void GlDeleteBuffers(int n, uint* buffers);

    [LibraryImport("libGLESv2.so", EntryPoint = "glBindBuffer")]
    private static partial void GlBindBuffer(uint target, uint buffer);

    [LibraryImport("libGLESv2.so", EntryPoint = "glBufferData")]
    private static partial void GlBufferData(uint target, nint size, float* data, uint usage);

    [LibraryImport("libGLESv2.so", EntryPoint = "glEnableVertexAttribArray")]
    private static partial void GlEnableVertexAttribArray(uint index);

    [LibraryImport("libGLESv2.so", EntryPoint = "glVertexAttribPointer")]
    private static partial void GlVertexAttribPointer(uint index, int size, uint type, byte normalized, int stride, nint pointer);

    [LibraryImport("libGLESv2.so", EntryPoint = "glGetAttribLocation", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int GlGetAttribLocation(uint program, string name);

    [LibraryImport("libGLESv2.so", EntryPoint = "glBindTexture")]
    private static partial void GlBindTexture(uint target, uint texture);

    [LibraryImport("libGLESv2.so", EntryPoint = "glTexParameteri")]
    private static partial void GlTexParameteri(uint target, uint pname, int param);

    [LibraryImport("libGLESv2.so", EntryPoint = "glActiveTexture")]
    private static partial void GlActiveTexture(uint texture);

    [LibraryImport("libGLESv2.so", EntryPoint = "glGenFramebuffers")]
    private static partial void GlGenFramebuffers(int n, uint* framebuffers);

    [LibraryImport("libGLESv2.so", EntryPoint = "glDeleteFramebuffers")]
    private static partial void GlDeleteFramebuffers(int n, uint* framebuffers);

    [LibraryImport("libGLESv2.so", EntryPoint = "glBindFramebuffer")]
    private static partial void GlBindFramebuffer(uint target, uint framebuffer);

    [LibraryImport("libGLESv2.so", EntryPoint = "glFramebufferTexture2D")]
    private static partial void GlFramebufferTexture2D(uint target, uint attachment, uint textarget, uint texture, int level);

    [LibraryImport("libGLESv2.so", EntryPoint = "glCheckFramebufferStatus")]
    private static partial uint GlCheckFramebufferStatus(uint target);

    [LibraryImport("libGLESv2.so", EntryPoint = "glViewport")]
    private static partial void GlViewport(int x, int y, int width, int height);

    [LibraryImport("libGLESv2.so", EntryPoint = "glDrawArrays")]
    private static partial void GlDrawArrays(uint mode, int first, int count);

    [LibraryImport("libGLESv2.so", EntryPoint = "glFinish")]
    private static partial void GlFinish();

    [LibraryImport("libGLESv2.so", EntryPoint = "glUseProgram")]
    private static partial void GlUseProgram(uint program);

    [LibraryImport("libGLESv2.so", EntryPoint = "glUniformMatrix4fv")]
    private static partial void GlUniformMatrix4Fv(int location, int count, byte transpose, float* value);

    [LibraryImport("libGLESv2.so", EntryPoint = "glUniform1i")]
    private static partial void GlUniform1I(int location, int v0);

    [LibraryImport("libGLESv2.so", EntryPoint = "glCreateShader")]
    private static partial uint GlCreateShader(uint type);

    [LibraryImport("libGLESv2.so", EntryPoint = "glShaderSource")]
    private static partial void GlShaderSource(uint shader, int count, nint* @string, int* length);

    [LibraryImport("libGLESv2.so", EntryPoint = "glCompileShader")]
    private static partial void GlCompileShader(uint shader);

    [LibraryImport("libGLESv2.so", EntryPoint = "glGetShaderiv")]
    private static partial void GlGetShaderIv(uint shader, uint pname, int* parameters);

    [LibraryImport("libGLESv2.so", EntryPoint = "glDeleteShader")]
    private static partial void GlDeleteShader(uint shader);

    [LibraryImport("libGLESv2.so", EntryPoint = "glCreateProgram")]
    private static partial uint GlCreateProgram();

    [LibraryImport("libGLESv2.so", EntryPoint = "glAttachShader")]
    private static partial void GlAttachShader(uint program, uint shader);

    [LibraryImport("libGLESv2.so", EntryPoint = "glLinkProgram")]
    private static partial void GlLinkProgram(uint program);

    [LibraryImport("libGLESv2.so", EntryPoint = "glGetProgramiv")]
    private static partial void GlGetProgramIv(uint program, uint pname, int* parameters);

    [LibraryImport("libGLESv2.so", EntryPoint = "glGetUniformLocation", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int GlGetUniformLocation(uint program, string name);

    [LibraryImport("libGLESv2.so", EntryPoint = "glDeleteProgram")]
    private static partial void GlDeleteProgram(uint program);

    // ── AHardwareBuffer（libandroid.so）──
    [LibraryImport("libandroid.so", EntryPoint = "AHardwareBuffer_allocate")]
    private static partial int AHardwareBufferAllocate(AHardwareBufferDesc* desc, nint* outBuffer);

    [LibraryImport("libandroid.so", EntryPoint = "AHardwareBuffer_release")]
    private static partial void AHardwareBufferRelease(nint buffer);
}

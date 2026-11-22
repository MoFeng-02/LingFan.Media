using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Android.Graphics;       // SurfaceTexture / Surface
using Android.Views;
using LingFan.Media.GPUShare.Android.Egl; // EglImageInterop.EglImagePreservedKhr（EGL_KHR_image_base 唯一真源）

namespace LingFan.Media.GPUShare.Android.Bridge;

// AndroidAhbRgbaBridge 的 GlSetup 职责段（部分类，由 AndroidAhbRgbaBridge.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/RunLoop/ConvertLatest/ProduceFrameOnThisThread/Dispose）在主文件；
// 本段仅 GL 线程环境搭建/扩展解析/着色器编译/ teardown，全部在 GL 线程执行。
public sealed unsafe partial class AndroidAhbRgbaBridge
{
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

        // 上下文保持 current：GL 线程常驻持有，不在此释放。
    }

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
        // pos.y 相对常规 GL 全屏 quad 翻转：GL 帧缓冲内存行序底左（y 向上），而 AHB 的后续
        // 消费方（Vulkan 导入/Skia 采样）按顶左（y 向下）读内存行——不翻转则画面上下镜像
        // （权威：GL 与 Vulkan 帧缓冲附件的 Y 轴约定相反，Veldrid "API-specific rendering differences"）。
        float[] quad =
        {
            -1f,  1f, 0f, 0f,
             1f,  1f, 1f, 0f,
            -1f, -1f, 0f, 1f,
             1f, -1f, 1f, 1f,
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

    /// <summary>在 GL 线程上销毁全部 GL 对象并终止 EGL（上下文此时 current）。</summary>
    /// <remarks><b>幂等</b>：每项销毁后立即清零字段——异常路径可能二次进入（stop 分支清理中抛异常 →
    /// GlThreadEntry catch 再次调用），重入时不得重复 EglTerminate（display 引用计数会多减）。</remarks>
    private void TeardownGlOnThisThread()
    {
        if (_program != 0) { GlDeleteProgram(_program); _program = 0; }
        if (_vbo != 0) { uint vb = _vbo; GlDeleteBuffers(1, &vb); _vbo = 0; }
        // 取字段副本入局部再取地址（跨环境安全写法：本机 Roslyn 对 &字段 报 CS0212，须走局部变量）。
        if (_scratchTex != 0) { uint sTex = _scratchTex; GlDeleteTextures(1, &sTex); _scratchTex = 0; }
        if (_oesTex != 0) { uint oTex = _oesTex; GlDeleteTextures(1, &oTex); _oesTex = 0; }
        if (_fbo != 0) { uint fb = _fbo; GlDeleteFramebuffers(1, &fb); _fbo = 0; }

        if (_eglDisplay != nint.Zero)
        {
            // 线程即将退出，上下文随之消亡；显式解绑再销毁，符合 EGL 规范。
            EglMakeCurrent(_eglDisplay, nint.Zero, nint.Zero, nint.Zero);
            if (_eglContext != nint.Zero) { EglDestroyContext(_eglDisplay, _eglContext); _eglContext = nint.Zero; }
            if (_eglSurface != nint.Zero) { EglDestroySurface(_eglDisplay, _eglSurface); _eglSurface = nint.Zero; }
            EglTerminate(_eglDisplay);
            _eglDisplay = nint.Zero;
        }
        _logger?.LogInformation("[ANDROID-AHB] GL 线程已销毁上下文并退出。");
    }
}

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

// AndroidAhbRgbaBridge 的产帧职责段（部分类，按职责拆分，编译期等价）。
// GL 线程上 AHB 分配→EGLImage→FBO 绑定→updateTexImage 闩帧→OES 直出 RGBA 的全链；
// 线程编排与 GL 环境在主文件及 GlSetup 段。
public sealed unsafe partial class AndroidAhbRgbaBridge
{
    /// <summary>在 GL 线程上闩取最新帧并渲染进 RGBA AHardwareBuffer。上下文此时已 current（建上下文后从未释放），
    /// 故此处<b>不做</b> eglMakeCurrent / 不做 finally 释放上下文——仅失败路径清理本帧的 AHB / EGLImage 资源。</summary>
    private nint ProduceFrameOnThisThread()
    {
        _logger?.LogTrace("[ANDROID-AHB-TRACE] S1GL线程产帧（托管线程={Tid}）", Environment.CurrentManagedThreadId);

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
            // S1b 前置打卡：若冻结日志停在 S1b 而 S4 缺失，即阻塞发生在 AHardwareBufferAllocate（gralloc）。
            _logger?.LogTrace("[ANDROID-AHB-TRACE] S1b进入 AHardwareBufferAllocate（托管线程={Tid}）", Environment.CurrentManagedThreadId);
            if (AHardwareBufferAllocate(&desc, &ahb) != 0 || ahb == nint.Zero)
            {
                _logger?.LogWarning("[ANDROID-AHB] AHardwareBuffer_allocate(RGBA8) 失败。");
                return nint.Zero;
            }
            _logger?.LogTrace("[ANDROID-AHB-TRACE] S4AHB 分配成功 ahb=0x{Ahb} 托管线程={Tid}", (ulong)ahb, Environment.CurrentManagedThreadId);

            // 2) AHB → EGLClientBuffer → EGLImage。
            nint clientBuf = _eglGetNativeClientBufferAndroid(ahb);
            if (clientBuf == nint.Zero)
            {
                _logger?.LogWarning("[ANDROID-AHB] eglGetNativeClientBufferANDROID 失败。");
                return nint.Zero;
            }
            _logger?.LogTrace("[ANDROID-AHB-TRACE] S5eglGetNativeClientBuffer 成功 clientBuf=0x{Cb}", (ulong)clientBuf);
            int[] imgAttrs = { EglImageInterop.EglImagePreservedKhr, 1, EglNone };
            fixed (int* ia = imgAttrs)
                eglImage = _eglCreateImageKhr(_eglDisplay, EglNoContext, (uint)EglNativeBufferAndroid, clientBuf, (nint)ia);
            if (eglImage == nint.Zero)
            {
                _logger?.LogWarning("[ANDROID-AHB] eglCreateImageKHR(AHB) 失败 0x{EglErr:X8}。", (ulong)EglGetError());
                return nint.Zero;
            }
            _logger?.LogTrace("[ANDROID-AHB-TRACE] S6eglCreateImageKHR 成功 eglImage=0x{Img}", (ulong)eglImage);

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
            _logger?.LogTrace("[ANDROID-AHB-TRACE] S7纹理/FBO 绑定完成 glErr=0x{GlErr:X8}", glErrTex);
            if (GlCheckFramebufferStatus(GlFramebuffer) != GlFramebufferComplete)
            {
                _logger?.LogWarning("[ANDROID-AHB] FBO 不完整（AHB→GL 纹理绑定失败），GL 错误=0x{GlErr:X8}。", (uint)GlGetError());
                return nint.Zero;
            }
            _logger?.LogTrace("[ANDROID-AHB-TRACE] S8FBO 完整");

            // 4) 闩帧：updateTexImage 阻塞等解码 fence，把最新帧写入 _oesTex（OES 外部纹理，驱动已完成 YUV→RGB）。
            // 契约：拥有该纹理的 EGL 上下文（本 GL 线程）必须 current —— 专用 GL 线程契约保证恒满足，无跨线程问题。
            try
            {
                _surfaceTexture!.UpdateTexImage();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ANDROID-AHB] SurfaceTexture.updateTexImage 失败。");
                return nint.Zero;
            }
            _logger?.LogTrace("[ANDROID-AHB-TRACE] S9updateTexImage 成功");
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
            _logger?.LogTrace("[ANDROID-AHB-TRACE] S10GlDrawArrays 完成 glErr=0x{GlErr:X8}", glErrDraw);
            GlBindBuffer(GlArrayBuffer, 0);
            GlFinish(); // 保证 AHB 写入对后续 Vulkan 导入可见（跨 API 同步）。
            _logger?.LogTrace("[ANDROID-AHB-TRACE] S11glFinish 完成");

            // 6) EGLImage 仅渲染期需要，销毁（AHB 内容已落盘，引用仍由帧资源持有）。
            _eglDestroyImageKhr(_eglDisplay, eglImage);
            eglImage = nint.Zero;
            _logger?.LogTrace("[ANDROID-AHB-TRACE] S12eglDestroyImageKHR 完成，准备返回 ahb=0x{Ahb}", (ulong)ahb);

            _logger?.LogTrace("[ANDROID-AHB] 帧渲染进 RGBA AHB 完成 {W}x{H}", _width, _height);
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
}

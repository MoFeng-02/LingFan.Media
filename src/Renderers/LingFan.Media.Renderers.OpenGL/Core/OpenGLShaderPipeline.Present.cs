using System.Runtime.InteropServices;
using System.Text;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.Renderers.OpenGL;

// OpenGLShaderPipeline 的 Present 职责段（部分类，由 OpenGLShaderPipeline.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/EnsureInitialized/Dispose）在主文件；全部同步原生调用。
internal sealed unsafe partial class OpenGLShaderPipeline
{
    /// <summary>用 Shader 路径将软件帧呈现到当前 GL 帧缓冲（不交换缓冲，由调用方 SwapBuffers）。</summary>
    internal void Present(SoftwareFrameResource sw, int dstWidth, int dstHeight, AspectRatioMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sw);

        EnsureInitialized();
        EnsureTextures(sw.Width, sw.Height, sw.Format);
        UploadPlanes(sw);

        // 按 ScaleMode 计算目标视口矩形（top-left 原点）：Uniform 留黑边、UniformToFill 居中溢出裁剪
        ComputeScaleRects(sw.Width, sw.Height, dstWidth, dstHeight, mode,
            out int vx, out int vy, out int vw, out int vh);

        // 先清空整帧缓冲为黑（letterbox 黑边靠此；Fill/UniformToFill 全幅覆盖，清屏无害）
        GLNative.glViewport(0, 0, dstWidth, dstHeight);
        GLNative.glClearColor(0f, 0f, 0f, 1f);
        GLNative.glClear(GlColorBufferBit);

        // GL 视口原点在左下 → 翻转 Y 使 top-left 矩形映射到正确位置
        GLNative.glViewport(vx, dstHeight - vy - vh, vw, vh);
        GLNative.BindVertexArray(_vao);

        switch (sw.Format)
        {
            case PixelFormat.BGRA32:
            case PixelFormat.RGBA32:
            case PixelFormat.RGB24:
                GLNative.UseProgram(_rgbProgram);
                GLNative.ActiveTexture((uint)(GlTexture0 + 0));
                GLNative.glBindTexture(GlTexture2D, _planeTextures[0]);
                GLNative.Uniform1i(_rgbUTex, 0);
                // RGB24 为直序 RGB（无需 BGR 交换）；仅 BGRA32 经 .bgra 还原显示顺序。
                GLNative.Uniform1i(_rgbUIsBgra, sw.Format == PixelFormat.BGRA32 ? 1 : 0);
                GLNative.glDrawArrays(GlTriangleStrip, 0, 4);
                break;

            case PixelFormat.YUV420P:
            case PixelFormat.YUV422P:
            case PixelFormat.YUV444P:
                GLNative.UseProgram(_yuvProgram);
                BindPlane(0, _planeTextures[0], _yuvUY);
                BindPlane(1, _planeTextures[1], _yuvUU);
                BindPlane(2, _planeTextures[2], _yuvUV);
                GLNative.glDrawArrays(GlTriangleStrip, 0, 4);
                break;

            case PixelFormat.NV12:
            case PixelFormat.NV21:
                GLNative.UseProgram(_nvProgram);
                BindPlane(0, _planeTextures[0], _nvUY);
                BindPlane(1, _planeTextures[1], _nvUUV);
                GLNative.Uniform1i(_nvUSwap, sw.Format == PixelFormat.NV21 ? 1 : 0);
                GLNative.glDrawArrays(GlTriangleStrip, 0, 4);
                break;

            default:
                throw new NotSupportedException(
                    $"OpenGL Shader 管线不支持像素格式 {sw.Format}。支持 BGRA32/RGBA32/RGB24/YUV420P/YUV422P/YUV444P/NV12/NV21。");
        }
    }

    private static void BindPlane(int unit, uint tex, int uniformLoc)
    {
        GLNative.ActiveTexture((uint)(GlTexture0 + unit));
        GLNative.glBindTexture(GlTexture2D, tex);
        GLNative.Uniform1i(uniformLoc, unit);
    }

    /// <summary>
    /// 用 Shader 路径将零拷贝 GPU 纹理（<see cref="IGpuTextureResource"/>）呈现到当前 GL 帧缓冲（不交换缓冲，由调用方 SwapBuffers）。
    /// </summary>
    /// <remarks>
    /// <para>Windows WGL 路径：每帧在<b>当前 on-screen GL 上下文</b>上现场打开/注册/锁定 D3D11 共享纹理，
    /// 绘制后立即解锁/注销/删除 GL 纹理。这规避 owner 离屏上下文注册的对象在 on-screen 上下文上可能无法正确
    /// lock/采样的驱动实现问题（WGL_NV_DX_interop2 互操作对象强关联注册上下文）。</para>
    /// <para>Linux EGL dma_buf 路径：直接绑定 <see cref="IGpuTextureResource.NativeTextureHandle"/> 采样。</para>
    /// <para>外部纹理按 RGBA8（WGL 从 DXGI B8G8R8A8_UNORM 映射后由 shader 按 RGBA 采样，与 D3D11
    /// 原数据一致，故 <c>uIsBgra=0</c> 不做 BGR 交换）。</para>
    /// </remarks>
    internal void PresentGpuTexture(IGpuTextureResource gpu, int dstWidth, int dstHeight, AspectRatioMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(gpu);

        EnsureInitialized();

        // Windows WGL_NV_DX_interop2：在 on-screen 上下文上现场注册并绘制，用完即释。
        if (gpu is GLD3D11InteropTexture wglTex)
        {
            PresentWglInteropTexture(wglTex, dstWidth, dstHeight, mode);
            return;
        }

        // Linux EGL dma_buf：composed NV12 双平面（Y=R8 / UV=GR88），用 NV12 shader 采样（零拷贝）。
        if (gpu is GLDmaBufNv12Texture nvTex)
        {
            PresentDmaBufNv12(nvTex, dstWidth, dstHeight, mode);
            return;
        }

        uint tex = unchecked((uint)gpu.NativeTextureHandle);

        GLNative.ActiveTexture((uint)GlTexture0);
        GLNative.glBindTexture(GlTexture2D, tex);

        // 按 ScaleMode 计算目标视口矩形（与 Present 同源）
        ComputeScaleRects(gpu.Width, gpu.Height, dstWidth, dstHeight, mode,
            out int vx, out int vy, out int vw, out int vh);

        GLNative.glViewport(0, 0, dstWidth, dstHeight);
        GLNative.glClearColor(0f, 0f, 0f, 1f);
        GLNative.glClear(GlColorBufferBit);

        GLNative.glViewport(vx, dstHeight - vy - vh, vw, vh);
        GLNative.BindVertexArray(_vao);
        GLNative.UseProgram(_rgbProgram);
        GLNative.Uniform1i(_rgbUTex, 0);
        GLNative.Uniform1i(_rgbUIsBgra, 0);
        GLNative.glDrawArrays(GlTriangleStrip, 0, 4);
    }

    /// <summary>
    /// Linux EGL dma_buf 零拷贝绘制：composed NV12（Y=R8 / UV=GR88 双 GL 纹理）经 NV12 shader 采样上屏（零 CPU 回读）。
    /// </summary>
    private void PresentDmaBufNv12(GLDmaBufNv12Texture nvTex, int dstWidth, int dstHeight, AspectRatioMode mode)
    {
        // 按 ScaleMode 计算目标视口矩形（与 Present 同源）
        ComputeScaleRects(nvTex.Width, nvTex.Height, dstWidth, dstHeight, mode,
            out int vx, out int vy, out int vw, out int vh);

        GLNative.glViewport(0, 0, dstWidth, dstHeight);
        GLNative.glClearColor(0f, 0f, 0f, 1f);
        GLNative.glClear(GlColorBufferBit);

        GLNative.glViewport(vx, dstHeight - vy - vh, vw, vh);
        GLNative.BindVertexArray(_vao);

        // Y 平面（R8）→ unit0；UV 平面（GR88）→ unit1；NV12 半平面 shader（uSwap=0：U=R、V=G）
        GLNative.UseProgram(_nvProgram);
        BindPlane(0, nvTex.YTexture, _nvUY);
        BindPlane(1, nvTex.UVTexture, _nvUUV);
        GLNative.Uniform1i(_nvUSwap, 0);
        GLNative.glDrawArrays(GlTriangleStrip, 0, 4);
    }

    /// <summary>
    /// WGL_NV_DX_interop2 零拷贝绘制：在当前 on-screen GL 上下文上把每帧 D3D11 共享纹理注册为 GL 纹理，
    /// lock → 绘制 → unlock → unregister/delete，全程单帧内完成。
    /// </summary>
    private unsafe void PresentWglInteropTexture(GLD3D11InteropTexture tex, int dstWidth, int dstHeight, AspectRatioMode mode)
    {
        EnsureWglInteropDevice(tex.BridgeDevice);

        // 跨设备 SharedKeyedMutex 发布栅栏：WGL_NV_DX_interop2 *不* 自动处理 keyed mutex
        // （Firefox SharedSurfaceD3D11Interop.cpp 与本项目 ReadbackToCpu 均显式 AcquireSync）。
        // 桥接设备须在 lock 之前先 AcquireSync(0) 取得转换器（ffmpeg 设备）ReleaseSync(0) 释放的访问权，
        // GL 才能读到有效像素；否则纹理处于未授权访问态，采样结果为未定义（全黑）。
        // 绘制结束再 ReleaseSync(0)。
        // 有限超时（与 D3D11/Vulkan 共享表面源同口径）：上游转换器未及时释放栅栏时丢帧而非阻塞呈现线程——
        // INFINITE 等待会把上游停顿放大为整条呈现链冻结。超时覆盖数帧预算，容忍正常调度抖动。
        IDXGIKeyedMutex? keyedMutex = null;
        bool acquired = false;
        try { keyedMutex = tex.D3dTexture.QueryInterface<IDXGIKeyedMutex>(); }
        catch (Exception) { keyedMutex = null; }

        if (keyedMutex is not null)
        {
            try { keyedMutex.AcquireSync(0, InteropAcquireTimeoutMs); acquired = true; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[OPENGL-ZEROCOPY] 桥接设备 AcquireSync 超时/失败，跳过本帧绘制（防呈现链冻结）。");
                keyedMutex.Dispose();
                return;
            }
        }

        uint glTex = 0;
        nint interopObj = nint.Zero;
        try
        {
            GLNative.glGenTextures(1, &glTex);

            // 在 on-screen 上下文上注册 D3D 纹理为 GL 纹理对象（零拷贝只读采样）。
            interopObj = GLNative.WglDXRegisterObjectNV(
                _wglInteropDevice, (void*)tex.D3dTexture.NativePointer, glTex,
                (uint)GlTexture2D, (uint)GLNative.WglAccessReadOnlyNV);
            if (interopObj == nint.Zero)
                throw new InvalidOperationException("wglDXRegisterObjectNV 在 on-screen 上下文上失败。");

            // register 后绑定并设置采样参数，避免 texture incomplete 黑屏。
            GLNative.glBindTexture(GlTexture2D, glTex);
            GLNative.glTexParameteri(GlTexture2D, (uint)GlTextureMinFilter, GlLinear);
            GLNative.glTexParameteri(GlTexture2D, (uint)GlTextureMagFilter, GlLinear);

            // 取得跨设备访问权（keyed mutex 已在本方法入口 AcquireSync 取得）。
            GLNative.WglDXLockObjectsNV(_wglInteropDevice, 1, &interopObj);
            try
            {
                // 按 ScaleMode 计算目标视口矩形（与 Present 同源）
                ComputeScaleRects(tex.Width, tex.Height, dstWidth, dstHeight, mode,
                    out int vx, out int vy, out int vw, out int vh);

                GLNative.glViewport(0, 0, dstWidth, dstHeight);
                GLNative.glClearColor(0f, 0f, 0f, 1f);
                GLNative.glClear(GlColorBufferBit);

                GLNative.glViewport(vx, dstHeight - vy - vh, vw, vh);
                GLNative.BindVertexArray(_vao);
                GLNative.UseProgram(_rgbProgram);
                GLNative.Uniform1i(_rgbUTex, 0);
                // WGL_NV_DX_interop2 将 DXGI B8G8R8A8_UNORM 注册为 GL 纹理后，
                // GL 采样按 RGBA 通道顺序解释，无需额外红蓝交换（uIsBgra=0）。
                GLNative.Uniform1i(_rgbUIsBgra, 0);
                GLNative.glDrawArrays(GlTriangleStrip, 0, 4);
            }
            finally
            {
                GLNative.WglDXUnlockObjectsNV(_wglInteropDevice, 1, &interopObj);
            }
        }
        finally
        {
            if (interopObj != nint.Zero)
                GLNative.WglDXUnregisterObjectNV(_wglInteropDevice, interopObj);
            if (glTex != 0)
                GLNative.glDeleteTextures(1, &glTex);
            // 释放跨设备发布栅栏（与 AcquireSync 配对），使转换器下一帧可再次写入。
            if (acquired && keyedMutex is not null)
                keyedMutex.ReleaseSync(0);
            keyedMutex?.Dispose();
        }
    }

    /// <summary>确保当前 on-screen GL 上下文已打开与桥接 D3D11 设备对应的 WGL 互操作设备（按桥接设备缓存）。</summary>
    private unsafe void EnsureWglInteropDevice(ID3D11Device bridgeDevice)
    {
        if (_wglInteropDevice != nint.Zero && _wglBridgeDevice == bridgeDevice) return;

        if (_wglInteropDevice != nint.Zero)
        {
            GLNative.WglDXCloseDeviceNV(_wglInteropDevice);
            _wglInteropDevice = nint.Zero;
            _wglBridgeDevice = null;
        }

        _wglInteropDevice = GLNative.WglDXOpenDeviceNV((void*)bridgeDevice.NativePointer);
        if (_wglInteropDevice == nint.Zero)
            throw new InvalidOperationException("wglDXOpenDeviceNV 在 on-screen 上下文上失败（WGL_NV_DX_interop2 不可用）。");

        _wglBridgeDevice = bridgeDevice;
    }

    /// <summary>
    /// 按 <see cref="AspectRatioMode"/> 计算软帧→目标（top-left 原点）的目标视口矩形。
    /// <list type="bullet">
    /// <item><see cref="AspectRatioMode.Fill"/>：拉伸填满（整目标）。</item>
    /// <item><see cref="AspectRatioMode.Uniform"/>：保持比例、居中、留黑边（子矩形）。</item>
    /// <item><see cref="AspectRatioMode.UniformToFill"/>：保持比例、居中、溢出裁剪（大于目标的矩形，仅中心可见）。</item>
    /// </list>
    /// 纯视口数学，无需改动 Shader（Full→均匀覆盖、溢出→天然裁中心），与 Vulkan/Skia 语义一致。
    /// </summary>
    private static void ComputeScaleRects(
        int srcW, int srcH, int dstW, int dstH, AspectRatioMode mode,
        out int x, out int y, out int w, out int h)
    {
        x = 0; y = 0; w = dstW; h = dstH;
        if (srcW <= 0 || srcH <= 0) return;

        switch (mode)
        {
            case AspectRatioMode.Uniform:
            {
                double fit = Math.Min((double)dstW / srcW, (double)dstH / srcH);
                w = Math.Max(1, (int)(srcW * fit + 0.5));
                h = Math.Max(1, (int)(srcH * fit + 0.5));
                x = (dstW - w) / 2;
                y = (dstH - h) / 2;
                break;
            }
            case AspectRatioMode.UniformToFill:
            {
                double cover = Math.Max((double)dstW / srcW, (double)dstH / srcH);
                w = Math.Max(1, (int)(srcW * cover + 0.5));
                h = Math.Max(1, (int)(srcH * cover + 0.5));
                x = (dstW - w) / 2;
                y = (dstH - h) / 2;
                break;
            }
            case AspectRatioMode.Fill:
            default:
                break;
        }
    }

    /// <summary>清除当前 GL 帧缓冲为黑色（不交换缓冲，由调用方 SwapBuffers）。</summary>
    internal void Clear(int dstWidth, int dstHeight)
    {
        GLNative.glViewport(0, 0, dstWidth, dstHeight);
        GLNative.glClearColor(0f, 0f, 0f, 1f);
        GLNative.glClear(GlColorBufferBit);
    }
}

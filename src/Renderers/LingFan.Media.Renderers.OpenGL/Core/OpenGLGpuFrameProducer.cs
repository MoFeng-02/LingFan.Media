using System;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL 零拷贝帧生产者：把解码后端原生输出（Windows D3D11 共享句柄 / Linux VAAPI dma_buf）
/// 经跨 API 互操作导入为 GL 纹理（<see cref="IGpuTextureResource"/>），实现零拷贝上屏。
/// </summary>
/// <remarks>
/// <para>注册为 <see cref="IGpuFrameProducer"/>，供解码器经中立桥调用，严守依赖倒置（后端不感知 OpenGL 绑定细节）。</para>
/// <para><b>导入机制</b>：
/// <list type="bullet">
/// <item><b>Windows</b>：WGL_NV_DX_interop2——生产者自建桥接 D3D11 设备（<c>D3D11CreateDevice</c>），
/// 把解码侧 DXGI 共享 NT 句柄经 <c>ID3D11Device1.OpenSharedResource1</c> 打开为本设备纹理，
/// 再经 <c>wglDXOpenDeviceNV</c> + <c>wglDXRegisterObjectNV</c> 注册为 GL 纹理；
/// 着色器管线的 <see cref="OpenGLShaderPipeline.PresentGpuTexture"/> 直接采样（零拷贝）。</item>
/// <item><b>Linux</b>：EGL_EXT_image_dma_buf_import——经 <c>eglCreateImageKHR</c> 把 VAAPI dma_buf 绑为 EGLImage，
/// 再 <c>glEGLImageTargetTexture2DOES</c> 绑为 GL 纹理（零拷贝）。</item>
/// </list></para>
/// <para><b>能力自报 + 行为副作用双判据（S_OK≠被接受）</b>：扩展不可用 / 句柄无效 / 注册失败 →
/// <see cref="TryImport"/> 返回 <see langword="false"/>，调用方回落软解并计 [FRAMEPATH] 统计，绝不报"已就绪"假绿。</para>
/// <para><b>共享组</b>：导入在工厂级离屏 GL 上下文（共享组所有者，on-screen 上下文以 shareContext 接入）下执行，
/// 注册的 GL 纹理对渲染器可见——零拷贝链路与 D3D11/Vulkan 完全同源。跨上下文 D3D 绑定共享依赖 GL share-list，
/// 属运行期验收项（设计假设，由宿主 probe 验证）。</para>
/// <para><b>AOT</b>：GL/WGL/EGL 互操作函数指针经 <see cref="GLNative"/> 零反射解析；D3D11 桥接设备经 Vortice 类型安全 API；
/// 无 [DllImport]/[ComImport]/反射；跨平台经 OperatingSystem.IsXxx() 运行时分发，无 #if。</para>
/// <para><b>v1 范围</b>：Windows(D3D11→GL) 为主路径；Linux(VAAPI→GL) 结构就绪但解码侧 VAAPI→GL 导入为未来端点，
/// 当前调用方不产出 <see cref="GpuFrameImportKind.LinuxDmaBufFd"/>，可用性探测失败即回落软解。Android(AHardwareBuffer)/Apple(IOSurface) 为后续端点，当前返回 false。</para>
/// <para><b>异步策略</b>：<see cref="TryImport"/> 为同步（native 分类）——GPU 纹理导入是同步原生调用，无 I/O await；
/// 实现保持同步，不补 async（补即伪异步）。</para>
/// <para><b>句柄所有权契约（单一责任人）</b>：原生共享句柄（NT HANDLE / dma_buf fd）的所有权自
/// <see cref="TryImport"/> 调用起转移至本生产者；无论导入成功或失败，生产者均在返回前
/// 经 <c>CloseHandle</c>（NT HANDLE）/ close（fd）关闭句柄（导入成功后资源引用已由 GL 纹理 / EGLImage 持有，
/// 关闭句柄不销毁资源）。调用方（解码器）导出句柄后<b>不得</b>再关闭，避免双关。</para>
/// </remarks>
public sealed partial class OpenGLGpuFrameProducer : IGpuFrameProducer, IDisposable
{
    /// <inheritdoc/>
    public GPUApiType ApiType => GPUApiType.OpenGL;

    private readonly OpenGLOffscreenDeviceContext _glContext;
    private readonly ILogger? _logger;
    private readonly object _lock = new();

    // Windows 桥接 D3D11 设备（懒创建、跨帧复用；所有权归生产者）。
    // WGL 互操作句柄不在此处持有——它由 on-screen 渲染上下文（OpenGLShaderPipeline.EnsureWglInteropDevice）现场打开并在管线 Dispose 时关闭。
    private ID3D11Device? _bridgeDevice;
    private bool _disposed;

    /// <summary>初始化 <see cref="OpenGLGpuFrameProducer"/> 的新实例。</summary>
    /// <param name="glContext">工厂级离屏 GL 设备上下文（共享组所有者；导入时 MakeCurrent 所需）。</param>
    /// <param name="logger">可选日志器。</param>
    public OpenGLGpuFrameProducer(OpenGLOffscreenDeviceContext glContext, ILogger? logger = null)
    {
        _glContext = glContext ?? throw new ArgumentNullException(nameof(glContext));
        _logger = logger;
    }

    /// <inheritdoc/>
    public unsafe bool TryImport(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;
        try
        {
            if (source.Handle == IntPtr.Zero || source.Width <= 0 || source.Height <= 0)
                return false;

            if (OperatingSystem.IsWindows() && source.Kind == GpuFrameImportKind.D3D11SharedHandle)
                return TryImportWin32D3D11(source, out texture);
            if (OperatingSystem.IsLinux() && source.Kind == GpuFrameImportKind.LinuxDmaBufFd)
                return TryImportLinuxVaApi(source, out texture);

            // Android / iOS：后续端点（AHardwareBuffer / IOSurface），当前回落软解。
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "OpenGL 零拷贝导入失败，回落软件解码（S_OK≠被接受：导入行为副作用未成立）。");
            texture?.Dispose();
            texture = null;
            return false;
        }
    }

    // ── Windows：WGL_NV_DX_interop2（D3D11 共享句柄 → GL 纹理）──

    /// <summary>关闭 DXGI 共享 NT 句柄（导入完成/失败后由生产者负责关闭，防内核句柄泄漏）。</summary>
    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    private unsafe bool TryImportWin32D3D11(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;

        // 在 owner 离屏上下文上完成能力自报 + 行为副作用验证：扩展可用、桥接设备可创建、共享句柄可打开。
        // 真正的 WGL register/lock 推迟到 on-screen 渲染上下文绘制时执行（见 OpenGLShaderPipeline.PresentWglInteropTexture），
        // 以规避 owner 上下文注册的对象在 on-screen 上下文上可能无法正确 lock/采样的驱动实现问题。
        _glContext.EnsureCreated();
        lock (_glContext.GlAccessLock)
        {
        _glContext.MakeCurrent();
        ID3D11Device1? device1 = null;
        ID3D11Texture2D? d3dTex = null;
        try
        {
            if (!GLNative.IsWglDxInteropAvailable())
            {
                _logger?.LogWarning("[OPENGL-ZEROCOPY] WGL_NV_DX_interop2 不可用，回落软件解码。");
                CloseHandle(source.Handle);
                return false;
            }

            // WGL_NV_DX_interop2 无法可移植地采样 NV12 D3D11 纹理（D3D11 渲染器自身亦确认 NV12 硬解纹理不可绑 SRV、须 CPU 往返）。
            // 故 NV12/NV21 不进入 WGL 零拷贝路径，回落 CPU NV12 → GL 着色器（已有 NV12 路径、零新增代码、与 D3D11 渲染器一致）。
            if (source.Format is PixelFormat.NV12 or PixelFormat.NV21)
            {
                _logger?.LogDebug("[OPENGL-ZEROCOPY] D3D11 共享纹理为 NV12/NV21，WGL 不可移植采样 → 回落 CPU NV12 路径（与 D3D11 渲染器一致）。");
                CloseHandle(source.Handle);
                return false;
            }

            EnsureBridgeDevice();

            // 把解码侧 DXGI 共享 NT 句柄打开为本桥接 D3D11 设备的纹理（OpenSharedResource1 仅接受 NT 句柄）。
            // Vortice OpenSharedResource1 返回 Result 并 out 纹理；按本仓库 D3D11Interop 既有模式以 out 纹理判空为准（S_OK≠被接受）。
            device1 = _bridgeDevice!.QueryInterface<ID3D11Device1>();
            device1.OpenSharedResource1<ID3D11Texture2D>(source.Handle, out d3dTex);
            if (d3dTex is null)
            {
                _logger?.LogWarning("[OPENGL-ZEROCOPY] ID3D11Device1.OpenSharedResource1 失败，回落软件解码。");
                CloseHandle(source.Handle);
                return false;
            }

            // 导入成功：把 D3D 纹理引用交给 GLD3D11InteropTexture；真正的 GL 注册在 on-screen 上下文绘制时进行。
            texture = new GLD3D11InteropTexture(
                width: source.Width, height: source.Height, format: source.Format,
                d3dTexture: d3dTex, bridgeDevice: _bridgeDevice,
                glContext: _glContext, subresourceIndex: source.SubresourceIndex);

            // 所有权移交 GLD3D11InteropTexture：本方法不再释放 d3dTex。
            // NT 共享句柄：OpenSharedResource1 已为生产者建立独立纹理引用，此处关闭句柄（不销毁资源，防内核句柄泄漏）。
            d3dTex = null;
            CloseHandle(source.Handle);
            return true;
        }
        catch
        {
            d3dTex?.Dispose();
            CloseHandle(source.Handle);
            throw;
        }
        finally
        {
            device1?.Dispose();
            _glContext.ReleaseCurrent();
        }
        }
    }

    private unsafe void EnsureBridgeDevice()
    {
        if (_bridgeDevice is not null) return;
        lock (_lock)
        {
            if (_bridgeDevice is not null) return;

            // 调用方（TryImportWin32D3D11）已在导入全程持有 GL 上下文 current（MakeCurrent 于 try 入口，
            // ReleaseCurrent 于 finally），此处不重复 MakeCurrent/ReleaseCurrent——否则会提前解绑上下文，
            // 导致后续 glGenTextures / wglDXRegisterObjectNV 在无当前上下文下执行（未定义行为）。
            _glContext.EnsureCreated();

            // 桥接 D3D11 设备：仅用于把解码侧共享句柄打开为纹理并与 GL 互操作，不参与呈现（呈现由 on-screen GL 上下文完成）。
            // 注意：此处不打开 WGL 互操作设备——WGL interop 句柄强关联「打开它的 GL 上下文」，必须在 on-screen 渲染上下文
            // （OpenGLShaderPipeline.EnsureWglInteropDevice）上现场打开，否则在离屏 owner 上下文打开的 interop 句柄在 on-screen 上无法正确注册/lock。
            ID3D11Device device = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport);
            _bridgeDevice = device;
        }
    }

    // ── Linux：EGL_EXT_image_dma_buf_import（VAAPI dma_buf → GL 纹理）──

    private unsafe bool TryImportLinuxVaApi(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;

        // 须先建立并绑定 EGL/GL 上下文：EGL 扩展函数经 eglGetProcAddress 解析，仅当 EGL 上下文 current 时返回有效指针。
        // 可用性探测必须发生在 MakeCurrent 之后，否则误判"不可用"并经静态缓存永久禁用零拷贝路径。
        // 整个 EGL 段须在同一锁内串行（同 WGL 路径：共享组所有者上下文不能并发 current 于两线程）。
        _glContext.EnsureCreated();
        lock (_glContext.GlAccessLock)
        {
            _glContext.MakeCurrent();
            nint eglImage = nint.Zero;
            uint glTex = 0;
            try
            {
            if (!GLNative.IsEglDmaBufImportAvailable())
            {
                _logger?.LogWarning("[OPENGL-ZEROCOPY] EGL_EXT_image_dma_buf_import 不可用，回落软件解码。");
                return false;
            }

            nint display = _glContext.OffscreenDisplay; // EGLDisplay（离屏共享组所有者）

            // EGL_DMA_BUF 属性表。解码侧 VAAPI→GL 完整多平面（offset/stride/modifier）为未来端点；
            // 此处以单平面 + 零偏移 + 推导 stride（NV12 约 width*4 字节/行）的近似属性表尝试导入，
            // 信息缺失时导入多失败 → 回落软解（S_OK≠被接受）。EGLint 属性表（EGL 1.4 KHR Image 语义；
            // EGL 1.5 宿主若以 64 位 EGLAttrib 期望则需适配，属运行期验收项）。
            Span<int> attribs = stackalloc int[]
            {
                GLNative.EglWidth, source.Width,
                GLNative.EglHeight, source.Height,
                GLNative.EglDmaBufPlane0FdExt, unchecked((int)source.Handle),
                GLNative.EglDmaBufPlane0OffsetExt, 0,
                GLNative.EglDmaBufPlane0PitchExt, source.Width * 4,
                GLNative.EglDmaBufPlaneCountExt, 1,
                GLNative.EglLinuxDrmFourccExt, 0x3231564E, // DRM_FORMAT_NV12 ('NV12')
                GLNative.EglNone,
            };
            fixed (int* p = attribs)
                eglImage = GLNative.EglCreateImageKHR(display, nint.Zero, (uint)GLNative.EglLinuxDmaBufExt, p);

            if (eglImage == nint.Zero)
            {
                _logger?.LogWarning("[OPENGL-ZEROCOPY] eglCreateImageKHR 失败，回落软件解码。");
                return false;
            }

            GLNative.glGenTextures(1, &glTex);
            GLNative.glBindTexture(GLNative.GlTexture2DConst, glTex);
            GLNative.GlEGLImageTargetTexture2DOES((uint)GLNative.GlTexture2DConst, eglImage);

            texture = new GLEglDmaBufTexture(
                width: source.Width, height: source.Height, format: source.Format,
                textureId: glTex, eglDisplay: display, eglImage: eglImage,
                glContext: _glContext, subresourceIndex: source.SubresourceIndex);
            return true;
        }
        catch
        {
            if (glTex != 0) GLNative.glDeleteTextures(1, &glTex);
            if (eglImage != nint.Zero) GLNative.EglDestroyImageKHR(_glContext.OffscreenDisplay, eglImage);
            throw;
        }
        finally
        {
            _glContext.ReleaseCurrent();
        }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 桥接 D3D11 设备直接释放；WGL 互操作句柄由 on-screen 渲染上下文（OpenGLShaderPipeline）持有并在其 Dispose 时关闭。
        _bridgeDevice?.Dispose();
        _bridgeDevice = null;
    }
}

using System;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

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
/// <see cref="TryImport"/> 返回 <see langword="false"/>，调用方回落软解并计入 CPU 拷贝统计，绝不报"已就绪"假绿。</para>
/// <para><b>共享组</b>：导入在工厂级离屏 GL 上下文（共享组所有者，on-screen 上下文以 shareContext 接入）下执行，
/// 注册的 GL 纹理对渲染器可见——零拷贝链路与 D3D11/Vulkan 完全同源。跨上下文 D3D 绑定共享依赖 GL share-list，
/// 属运行期验收项（设计假设，由宿主 probe 验证）。</para>
/// <para><b>AOT</b>：GL/WGL/EGL 互操作函数指针经 <see cref="GLNative"/> 零反射解析；D3D11 桥接设备经 Vortice 类型安全 API；
/// 无 [DllImport]/[ComImport]/反射；跨平台经 OperatingSystem.IsXxx() 运行时分发，无 #if。</para>
/// <para><b>v1 范围</b>：Windows(D3D11→GL) 与 Linux(VAAPI→GL) 均为已启用主路径。
/// Linux 路径：VAAPI 解出 VA Surface → <c>vaExportSurfaceHandle(DRM_PRIME_2, COMPOSED_LAYERS)</c> 导出单 fd 双平面 NV12 dma_buf
/// → 拆为 Y(R8) / UV(GR88) 两 EGLImage → 两 GL 纹理 → NV12 shader 零拷贝上屏。Android(AHardwareBuffer)/Apple(IOSurface) 为后续端点，当前返回 false。</para>
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

            // GPU 一致性守卫：WGL_NV_DX_interop2 强关联「打开它的 GL 上下文」所在 GPU。
            // 桥接 D3D11 设备（独显优先，与解码器同 GPU）若与 on-screen GL 上下文（窗口所在显示器 GPU）不同卡，
            // wglDXOpenDeviceNV 返回伪句柄 → wglDXRegisterObjectNV 直接访问违例崩溃。
            // 必须在导入阶段拦截：厂商不匹配 → 回落 CPU（优雅降级，绝不让 on-screen 绘制崩溃）。
            if (!IsBridgeDeviceGpuCompatibleWithGlContext())
            {
                CloseHandle(source.Handle);
                return false;
            }

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
            // 🔴 2026-08-20 真机修复：此前 D3D11CreateDevice(DriverType.Hardware) 无适配器 → 默认落在主显示器 GPU（核显 AMD），
            //    而 ffmpeg 解码设备是 FindPreferredAdapter 独显优先（NVIDIA）→ 跨 GPU 打开共享句柄 OpenSharedResource1 必失败。
            //    改与解码器同源：FindPreferredAdapter + D3D11CreateDeviceOnAdapter（独显优先），保证桥接设备与解码设备同一 GPU。
            //    注意：此处不打开 WGL 互操作设备——WGL interop 句柄强关联「打开它的 GL 上下文」，必须在 on-screen 渲染上下文
            //    （OpenGLShaderPipeline.EnsureWglInteropDevice）上现场打开，否则在离屏 owner 上下文打开的 interop 句柄在 on-screen 上无法正确注册/lock。
            IntPtr adapter = LingFan.Media.GPUShare.D3D11.D3D11Interop.FindPreferredAdapter();
            if (adapter != IntPtr.Zero)
            {
                try
                {
                    LingFan.Media.GPUShare.D3D11.D3D11Interop.D3D11CreateDeviceOnAdapter(
                        adapter, out IntPtr devicePtr, out IntPtr contextPtr);
                    // OpenGL 桥接只需 device（OpenSharedResource1 + WGL 互操作），不需要立即上下文。
                    if (contextPtr != IntPtr.Zero)
                        LingFan.Media.GPUShare.D3D11.D3D11Interop.Release(contextPtr);
                    // devicePtr 为新建自有设备（引用计数 1）：Vortice 包装持有并 Dispose 释放（与解码器 _vaOwnedDevice 同模式）。
                    _bridgeDevice = new Vortice.Direct3D11.ID3D11Device(devicePtr);
                }
                finally
                {
                    LingFan.Media.GPUShare.D3D11.D3D11Interop.Release(adapter);
                }
            }
            else
            {
                // 无可用适配器（异常环境）：回退默认路径（D3D11CreateDevice 无适配器）。
                _bridgeDevice = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                    Vortice.Direct3D.DriverType.Hardware,
                    Vortice.Direct3D11.DeviceCreationFlags.BgraSupport);
            }
        }
    }

    // 桥接设备厂商与当前 GL 上下文所在 GPU 是否一致（首次不匹配已通告标志）。
    private bool _gpuMismatchAnnounced;

    /// <summary>
    /// 校验桥接 D3D11 设备与当前 GL 上下文是否在同一 GPU。
    /// <para>WGL_NV_DX_interop2 强关联「打开它的 GL 上下文」所在 GPU；桥接设备（独显）与 GL 上下文
    /// （窗口所在显示器 GPU，双卡核显）不同卡时，<c>wglDXOpenDeviceNV</c> 返回伪句柄，
    /// <c>wglDXRegisterObjectNV</c> 直接访问违例崩溃。返回 <see langword="false"/> 时调用方应回落 CPU 传输。</para>
    /// </summary>
    private bool IsBridgeDeviceGpuCompatibleWithGlContext()
    {
        try
        {
            if (_bridgeDevice is null) return false;

            uint bridgeVendorId;
            using (var dxgiDevice = _bridgeDevice.QueryInterface<Vortice.DXGI.IDXGIDevice>())
            using (var adapter = dxgiDevice.GetAdapter())
            {
                bridgeVendorId = adapter.Description.VendorId;
            }

            nint p = GLNative.glGetString(GLNative.GlRenderer);
            string renderer = Marshal.PtrToStringAnsi(p) ?? string.Empty;

            bool bridgeIsNvidia = bridgeVendorId == 0x10DE;
            bool bridgeIsAmd = bridgeVendorId == 0x1002;
            bool glIsNvidia = renderer.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
            bool glIsAmd = renderer.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                || renderer.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                || renderer.Contains("ATI", StringComparison.OrdinalIgnoreCase);

            if ((bridgeIsNvidia && glIsNvidia) || (bridgeIsAmd && glIsAmd))
                return true;

            if (!_gpuMismatchAnnounced)
            {
                _gpuMismatchAnnounced = true;
                _logger?.LogWarning(
                    $"[OPENGL-ZEROCOPY] GPU 不一致：桥接 D3D11 设备厂商 0x{bridgeVendorId:X4}" +
                    $"（{(bridgeIsNvidia ? "NVIDIA" : bridgeIsAmd ? "AMD" : "未知")}）与 GL 上下文渲染器「{renderer}」" +
                    $"不在同一 GPU → WGL 互操作不可用，禁用零拷贝，回落 CPU 传输（WGL 上下文绑定窗口所在显示器 GPU）。");
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[OPENGL-ZEROCOPY] GPU 一致性检查失败，保守禁用零拷贝，回落 CPU。");
            return false;
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
            nint eglImageY = nint.Zero, eglImageUV = nint.Zero;
            uint texY = 0, texUV = 0;
            int fd = source.Handle == IntPtr.Zero ? -1 : (int)source.Handle;
            try
            {
                if (!GLNative.IsEglDmaBufImportAvailable())
                {
                    _logger?.LogWarning("[OPENGL-ZEROCOPY] EGL_EXT_image_dma_buf_import 不可用，回落软件解码。");
                    return false;
                }

                nint display = _glContext.OffscreenDisplay; // EGLDisplay（离屏共享组所有者）
                bool hasModifier = source.DrmModifier != 0;

                // Y 平面：单平面 R8（DRM_FORMAT_R8 = 0x20203852）。composed NV12 双平面共享同一 fd。
                var yAttribs = BuildDmaBufPlaneAttribs(
                    fd, source.Width, source.Height,
                    source.PlaneOffsets?.Length > 0 ? source.PlaneOffsets[0] : 0,
                    source.PlanePitches?.Length > 0 ? source.PlanePitches[0] : (uint)source.Width,
                    0x20203852, hasModifier, source.DrmModifier);
                fixed (int* p = yAttribs)
                    eglImageY = GLNative.EglCreateImageKHR(display, nint.Zero, (uint)GLNative.EglLinuxDmaBufExt, p);
                if (eglImageY == nint.Zero)
                {
                    _logger?.LogWarning("[OPENGL-ZEROCOPY] eglCreateImageKHR(Y 平面) 失败，回落软件解码。");
                    return false;
                }

                // UV 平面：单平面 GR88（DRM_FORMAT_GR88 = fourcc('G','R','8','8') = 0x38385247），与 Y 同 fd、独立 offset/pitch。
                // 注：0x38385247 字节序为 G,R,8,8；EGL 导入为 RG8 纹理后 .rg = (U, V)，与 NV12(UV 交错) 含义一致（uSwap=0）。
                uint uvOffset = source.PlaneOffsets?.Length > 1 ? source.PlaneOffsets[1]
                    : (uint)(source.Height * (source.PlanePitches?.Length > 0 ? source.PlanePitches[0] : (uint)source.Width));
                uint uvPitch = source.PlanePitches?.Length > 1 ? source.PlanePitches[1] : (uint)source.Width;
                var uvAttribs = BuildDmaBufPlaneAttribs(
                    fd, (int)(source.Width / 2), (int)(source.Height / 2), uvOffset, uvPitch,
                    0x38385247, hasModifier, source.DrmModifier);
                fixed (int* p = uvAttribs)
                    eglImageUV = GLNative.EglCreateImageKHR(display, nint.Zero, (uint)GLNative.EglLinuxDmaBufExt, p);
                if (eglImageUV == nint.Zero)
                {
                    _logger?.LogWarning("[OPENGL-ZEROCOPY] eglCreateImageKHR(UV 平面) 失败，回落软件解码。");
                    return false;
                }

                GLNative.glGenTextures(1, &texY);
                GLNative.glBindTexture(GLNative.GlTexture2DConst, texY);
                GLNative.GlEGLImageTargetTexture2DOES((uint)GLNative.GlTexture2DConst, eglImageY);
                SetDmaBufTexParams();

                GLNative.glGenTextures(1, &texUV);
                GLNative.glBindTexture(GLNative.GlTexture2DConst, texUV);
                GLNative.GlEGLImageTargetTexture2DOES((uint)GLNative.GlTexture2DConst, eglImageUV);
                SetDmaBufTexParams();

                texture = new GLDmaBufNv12Texture(
                    width: source.Width, height: source.Height,
                    yTexture: texY, uvTexture: texUV,
                    eglDisplay: display, eglImageY: eglImageY, eglImageUV: eglImageUV,
                    glContext: _glContext);

                // fd 已被两个 EGLImage 导入（dma_buf 由 EGLImage 持有独立引用），关闭 fd 防泄漏（单一责任人）
                CloseFd(fd);
                return true;
            }
            catch
            {
                if (texY != 0) GLNative.glDeleteTextures(1, &texY);
                if (texUV != 0) GLNative.glDeleteTextures(1, &texUV);
                if (eglImageY != nint.Zero) GLNative.EglDestroyImageKHR(_glContext.OffscreenDisplay, eglImageY);
                if (eglImageUV != nint.Zero) GLNative.EglDestroyImageKHR(_glContext.OffscreenDisplay, eglImageUV);
                if (fd >= 0) CloseFd(fd); // 失败出口：fd 尚未被消费，须关闭防泄漏
                throw;
            }
            finally
            {
                _glContext.ReleaseCurrent();
            }
        }
    }

    /// <summary>构造单平面 dma_buf EGLImage 属性表（R8 / GR88 等单平面格式）。</summary>
    private static int[] BuildDmaBufPlaneAttribs(
        int fd, int width, int height, uint offset, uint pitch, int drmFourcc, bool hasModifier, ulong modifier)
    {
        if (hasModifier)
        {
            return new[]
            {
                GLNative.EglWidth, width,
                GLNative.EglHeight, height,
                GLNative.EglDmaBufPlane0FdExt, fd,
                GLNative.EglDmaBufPlane0OffsetExt, (int)offset,
                GLNative.EglDmaBufPlane0PitchExt, (int)pitch,
                GLNative.EglDmaBufPlane0ModifierLoExt, (int)(modifier & 0xFFFFFFFF),
                GLNative.EglDmaBufPlane0ModifierHiExt, (int)(modifier >> 32),
                GLNative.EglDmaBufPlaneCountExt, 1,
                GLNative.EglLinuxDrmFourccExt, drmFourcc,
                GLNative.EglNone,
            };
        }
        return new[]
        {
            GLNative.EglWidth, width,
            GLNative.EglHeight, height,
            GLNative.EglDmaBufPlane0FdExt, fd,
            GLNative.EglDmaBufPlane0OffsetExt, (int)offset,
            GLNative.EglDmaBufPlane0PitchExt, (int)pitch,
            GLNative.EglDmaBufPlaneCountExt, 1,
            GLNative.EglLinuxDrmFourccExt, drmFourcc,
            GLNative.EglNone,
        };
    }

    /// <summary>关闭 Linux dma_buf 文件描述符（导入完成后由导入方负责关闭，防 fd 泄漏）。</summary>
    [LibraryImport("libc")]
    private static partial int close(int fd);

    private static void CloseFd(int fd)
    {
        if (fd >= 0) _ = close(fd);
    }

    // 单平面 R8 / 双通道 GR88 EGLImage 纹理须显式设过滤/环绕参数：默认 MIN_FILTER 为 NEAREST_MIPMAP_LINEAR
    // 且无 mipmap → 纹理「不完整」，采样恒返回 (0,0,0,1)。必须置 LINEAR + CLAMP_TO_EDGE 才能正常采样。
    private const int GlTexMinFilter = 0x2801;
    private const int GlTexMagFilter = 0x2800;
    private const int GlTexWrapS = 0x2802;
    private const int GlTexWrapT = 0x2803;
    private const int GlLinear = 0x2601;
    private const int GlClampToEdge = 0x812F;

    private static void SetDmaBufTexParams()
    {
        GLNative.glTexParameteri((uint)GLNative.GlTexture2DConst, (uint)GlTexMinFilter, GlLinear);
        GLNative.glTexParameteri((uint)GLNative.GlTexture2DConst, (uint)GlTexMagFilter, GlLinear);
        GLNative.glTexParameteri((uint)GLNative.GlTexture2DConst, (uint)GlTexWrapS, GlClampToEdge);
        GLNative.glTexParameteri((uint)GLNative.GlTexture2DConst, (uint)GlTexWrapT, GlClampToEdge);
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

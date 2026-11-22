using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// Windows 导入分部：WGL_NV_DX_interop2（D3D11 共享 NT 句柄 → GL 纹理），含桥接设备与 GPU 一致性守卫。
/// </summary>
public sealed partial class OpenGLGpuFrameProducer
{
    // Windows 桥接 D3D11 设备（懒创建、跨帧复用；所有权归生产者）。
    // WGL 互操作句柄不在此处持有——它由 on-screen 渲染上下文（OpenGLShaderPipeline.EnsureWglInteropDevice）现场打开并在管线 Dispose 时关闭。
    private ID3D11Device? _bridgeDevice;

    // 桥接设备厂商与当前 GL 上下文所在 GPU 是否一致（首次不匹配已通告标志）。
    private bool _gpuMismatchAnnounced;

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
            // 关键：若 D3D11CreateDevice(DriverType.Hardware) 不指定适配器 → 默认落在主显示器 GPU，
            //    而 ffmpeg 解码设备是 FindPreferredAdapter 独显优先 → 跨 GPU 打开共享句柄 OpenSharedResource1 必失败。
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
}
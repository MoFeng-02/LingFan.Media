using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Renderers.OpenGL.Context;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL 工厂级离屏 GL 设备上下文（实现 <see cref="IGpuDeviceContext"/> 中立桥）。
/// </summary>
/// <remarks>
/// <para><b>治本目标</b>：D3D11 / Vulkan 的共享 GPU 设备是工厂级单例，解码后端在 decode-init 阶段即可经
/// <see cref="IGpuDeviceContext"/> 拿到设备句柄启用硬解 + 零拷贝。OpenGL 原先将 GL 上下文放在渲染器实例级
/// （<see cref="OpenGLRenderer.Attach"/>），导致工厂层面不存在可供注册的 <c>IGpuDeviceContext</c>，解码侧
/// 永远"看不到"OpenGL——这是违反依赖倒置的架构不对称。</para>
/// <para>本类型在工厂级以<b>离屏 GL 上下文</b>建立共享组所有者：Windows 用 1×1 隐藏窗口 + WGL 3.3 core
/// （<see cref="WglContext.CreateOffscreen"/>）；Linux 用 EGL 默认显示 + pbuffer 表面 + 桌面 GL 3.3
/// （<see cref="EglContext.CreateOffscreen"/>）。其上下文句柄作为 <see cref="IGpuDeviceContext.DeviceHandle"/> 与
/// 共享组句柄暴露，渲染器 on-screen 上下文以 <see cref="ShareHandle"/> 接入同一共享组，使解码侧产出的 GL 纹理
/// 对渲染器可见——零拷贝链路与 D3D11/Vulkan 完全同源。</para>
/// <para><b>跨平台</b>：仅 Windows(WGL) / Linux(EGL) 两条桌面 GL 路径（Apple 由 Metal 覆盖、Android 由 Vulkan 覆盖）。
/// 函数指针为进程级静态（<see cref="GLNative.LoadModern"/> 幂等），多上下文复用，无重复解析。</para>
/// <para><b>异步策略</b>：<see cref="IGpuDeviceContext.InitializeAsync"/> 为接口契约——离屏上下文由工厂延迟创建并注入，
/// 无真实 I/O await，返回 <see cref="Task.CompletedTask"/>（非伪异步）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；GL/Win32/EGL 绑定经 <see cref="GLNative"/>（零反射 [LibraryImport]）。</para>
/// </remarks>
public sealed unsafe class OpenGLOffscreenDeviceContext : IGpuDeviceContext, IDisposable
{
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private IGlContext? _offscreen;
    private GpuDeviceCapabilities _capabilities = new("Unknown", 0, 0, 16384, false, false, -1);
    private bool _disposed;

    /// <summary>初始化 <see cref="OpenGLOffscreenDeviceContext"/> 的新实例。</summary>
    public OpenGLOffscreenDeviceContext(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    GPUApiType IGpuDeviceContext.ApiType => GPUApiType.OpenGL;

    /// <inheritdoc/>
    nint IGpuDeviceContext.DeviceHandle => _offscreen?.ContextHandle ?? nint.Zero;

    /// <inheritdoc/>
    nint IGpuDeviceContext.ContextHandle => _offscreen?.PlatformDisplay ?? nint.Zero;

    /// <inheritdoc/>
    bool IGpuDeviceContext.IsInitialized => _offscreen is not null;

    /// <summary>共享组所有者上下文句柄（HGLRC / EGLContext）。渲染器 on-screen 上下文以此作为 shareContext 接入。</summary>
    public nint ShareHandle => _offscreen?.ContextHandle ?? nint.Zero;

    /// <summary>离屏上下文的平台显示句柄（HDC / EGLDisplay）。共享组所有者所在显示——上屏上下文须在其上创建，共享组才有效（EGL 要求同一 EGLDisplay）。</summary>
    public nint OffscreenDisplay => _offscreen?.PlatformDisplay ?? nint.Zero;

    /// <summary>将离屏上下文绑定到调用线程（供 <see cref="GLTextureResource"/> 回读/释放纹理）。</summary>
    public void MakeCurrent() => _offscreen?.MakeCurrent();

    /// <summary>解绑调用线程上的离屏上下文。</summary>
    public void ReleaseCurrent() => _offscreen?.ReleaseCurrent();

    /// <summary>确保离屏 GL 设备上下文已建立（线程安全，幂等）。首次访问时按平台创建。</summary>
    public void EnsureCreated()
    {
        if (_offscreen is not null) return;
        lock (_lock)
        {
            if (_offscreen is not null) return;

            IGlContext ctx = OperatingSystem.IsWindows()
                ? WglContext.CreateOffscreen(_logger)
                : OperatingSystem.IsLinux()
                    ? EglContext.CreateOffscreen(_logger)
                    : throw new PlatformNotSupportedException(
                        "OpenGL 设备上下文仅支持 Windows(WGL) / Linux(EGL)，当前平台不可用。");

            _offscreen = ctx;
            _capabilities = QueryCapabilities();
            _logger?.LogInformation(
                "[OPENGL-DEVICE] 离屏 GL 设备上下文已建立（{Api}，共享组所有者），{maxTex}px",
                OperatingSystem.IsWindows() ? "WGL" : "EGL", _capabilities.MaxTextureSize);
        }
    }

    private GpuDeviceCapabilities QueryCapabilities()
    {
        if (_offscreen is null)
            return new GpuDeviceCapabilities("Unknown", 0, 0, 16384, false, false, -1);

        _offscreen.MakeCurrent();
        try
        {
            int maxTex = 16384;
            GLNative.glGetIntegerv(GLNative.GlMaxTextureSize, &maxTex);

            string name = "Unknown";
            nint pName = GLNative.glGetString(GLNative.GlRenderer);
            if (pName != nint.Zero)
                name = Marshal.PtrToStringAnsi(pName) ?? "Unknown";

            // GL 3.3 core 无计算着色器（GL 4.3+）；硬解支持属未来 interop 范围，此处保守填 false。
            return new GpuDeviceCapabilities(name, 0, 0, maxTex, false, false, -1);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OpenGL 设备能力查询失败，使用默认快照。");
            return new GpuDeviceCapabilities("Unknown", 0, 0, 16384, false, false, -1);
        }
        finally
        {
            _offscreen.ReleaseCurrent();
        }
    }

    /// <inheritdoc/>
    Task IGpuDeviceContext.InitializeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureCreated();
        return Task.CompletedTask; // 离屏上下文由工厂创建并注入，无 I/O
    }

    /// <inheritdoc/>
    GpuDeviceCapabilities IGpuDeviceContext.GetCapabilities() => _capabilities;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _offscreen?.Dispose();
        _offscreen = null;
    }
}

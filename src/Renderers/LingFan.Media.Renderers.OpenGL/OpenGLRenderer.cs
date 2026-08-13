using System;
using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;
using LingFan.Media.Renderers.OpenGL.Context;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL 视频渲染器（桌面 GL 3.3 家族：Windows WGL / Linux EGL X11）。仅覆盖桌面 GL 平台，不含 Apple 平台。
/// </summary>
/// <remarks>
/// <para><b>跨平台边界（重要）</b>：本渲染器为<see cref="OpenGLShaderPipeline"/> 使用的桌面 GL 3.3（GLSL <c>#version 330 core</c>）。
/// 故仅覆盖桌面 GL 平台——Windows(WGL) / Linux(EGL X11)。
/// <b>Apple 平台（macOS/iOS）不使用 OpenGL</b>（已由 Metal 后端覆盖），<b>Android 仅支持 OpenGL ES</b>（API 与着色器 <c>#version 300 es</c> 均异）——
/// 二者不属本渲染器范畴，分别由 Metal 后端（<c>LingFan.Media.Metal</c>，macOS/iOS）与 Vulkan 后端（Android）覆盖。
/// <see cref="Attach"/> 对不支持的平台抛 <see cref="PlatformNotSupportedException"/> 并指明替代后端，fail-fast。</para>
/// <para><b>契约与线程模型</b>（镜像 D3D11/Vulkan）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，无 I/O，返回 <see cref="Task.CompletedTask"/>。</item>
/// <item><see cref="Attach"/> / <see cref="Detach"/>：同步（native 分类），UI 线程，创建/释放 GL 上下文。</item>
/// <item><see cref="Present"/> / <see cref="Clear"/>：同步（native 分类），渲染线程，GPU 操作。</item>
/// </list>
/// <para><b>线程安全</b>：<c>_gate</c> 锁串行化 Attach/Detach/Present/Clear/Dispose，化解管线线程 Present 与
/// UI 线程 Detach 的竞态（与 D3D11 同构）。</para>
/// <para><b>上下文生命周期</b>：GL 上下文在 <see cref="Attach"/> 时建立（非工厂共享 Device 单例），工厂保持薄。</para>
/// <para><b>异步策略</b>：公开异步方法均为真异步签名但无 I/O 阻塞——<see cref="InitializeAsync"/> 返回
/// <see cref="Task.CompletedTask"/>、<see cref="DisposeAsync"/> 委托 <see cref="Dispose"/> + <see cref="ValueTask.CompletedTask"/>，
/// 二者皆非伪异步（无 <c>Task.Run</c> 包同步、无 await 硬阻塞）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；GL 调用经 <see cref="GLNative"/>（零反射 [LibraryImport]）。</para>
/// </remarks>
public sealed class OpenGLRenderer : IVideoRenderer
{
    private readonly object _gate = new();
    private readonly ILogger? _logger;
    private IGlContext? _context;
    private OpenGLShaderPipeline? _shaderPipeline;
    private int _attachedWidth;
    private int _attachedHeight;
    private bool _attached;
    private bool _disposed;
    private int _presentSeq;
    private AspectRatioMode _scaleMode = AspectRatioMode.Uniform;

    // 有头 GPU vsync 呈现：1 个刷新周期（60Hz ≈ 16.67ms）
    private static readonly TimeSpan PresentationLatencyValue = TimeSpan.FromMilliseconds(16.67);

    /// <summary>初始化 <see cref="OpenGLRenderer"/> 的新实例。</summary>
    public OpenGLRenderer(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public TimeSpan PresentationLatency => PresentationLatencyValue;

    /// <summary>软帧宽高比缩放模式（契约层 <see cref="AspectRatioMode"/>）。默认 <see cref="AspectRatioMode.Uniform"/>（保持比例，留黑边）。</summary>
    public AspectRatioMode ScaleMode
    {
        get => _scaleMode;
        set => _scaleMode = value;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Attach(IRenderTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            if (_disposed) return; // 锁外已检查，二次确认防御竞态

            if (_attached)
            {
                _logger?.LogWarning("OpenGL 渲染器已附加渲染目标，先 Detach 再 Attach。");
                Detach();
            }

            if (target.HandleType != RenderHandleType.Pointer)
            {
                throw new NotSupportedException(
                    $"OpenGL 渲染器仅支持 {nameof(RenderHandleType.Pointer)} 句柄类型，收到 {target.HandleType}。");
            }

            if (target.Width <= 0 || target.Height <= 0)
            {
                throw new ArgumentException(
                    $"渲染目标尺寸无效：{target.Width}x{target.Height}。", nameof(target));
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (target.NativeHandle is not IntPtr hwnd || hwnd == IntPtr.Zero)
                    {
                        throw new ArgumentException(
                            "渲染目标的原生句柄无效——期望 IntPtr 类型的 HWND。", nameof(target));
                    }
                    _context = new WglContext(hwnd, _logger);
                }
                else if (OperatingSystem.IsLinux())
                {
                    if (target.NativeHandle is not X11WindowHandle x11)
                    {
                        throw new NotSupportedException(
                            "Linux GL 渲染器要求 IRenderTarget.NativeHandle 为 X11WindowHandle（Display + Window）。");
                    }
                    _context = new EglContext(x11.Display, x11.Window, _logger);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    // Apple 平台不使用 OpenGL，由 Metal 后端（LingFan.Media.Metal）覆盖。本渲染器仅 Windows WGL + Linux EGL。
                    throw new PlatformNotSupportedException(
                        "macOS 不使用 OpenGL，由 Metal 后端（LingFan.Media.Metal）覆盖。本渲染器仅含 Windows WGL + Linux EGL。");
                }
                else if (OperatingSystem.IsAndroid())
                {
                    // Android 仅支持 OpenGL ES（API/着色器 #version 300 es 均不同于桌面 GL 3.3），
                    // 同套渲染器无法复用，需独立 GLES 渲染器。Android 上屏由 Vulkan 后端覆盖。
                    throw new PlatformNotSupportedException(
                        "OpenGL 渲染器为桌面 GL 3.3，Android 仅支持 OpenGL ES——需独立 GLES 渲染器（着色器 #version 300 es）。" +
                        "Android 上屏请使用 Vulkan 后端。");
                }
                else if (OperatingSystem.IsIOS())
                {
                    // Apple 平台不使用 OpenGL，由 Metal 后端（LingFan.Media.Metal）覆盖。本渲染器仅 Windows WGL + Linux EGL。
                    throw new PlatformNotSupportedException(
                        "iOS 不使用 OpenGL，由 Metal 后端（LingFan.Media.Metal）覆盖。本渲染器仅含 Windows WGL + Linux EGL。");
                }
                else
                {
                    throw new NotSupportedException(
                        "OpenGL 渲染器仅支持 Windows(WGL) / Linux(EGL)，当前平台不可用。");
                }

                _attachedWidth = target.Width;
                _attachedHeight = target.Height;
                _shaderPipeline = new OpenGLShaderPipeline(_logger);
                _attached = true;

                _logger?.LogInformation(
                    "[OPENGL-ATTACH] target={TW}x{TH} 上下文={Ctx} 版本={Major}.{Minor}",
                    target.Width, target.Height,
                    _context is WglContext ? "WGL" : "EGL",
                    _context.GlMajor, _context.GlMinor);
            }
            catch
            {
                _shaderPipeline?.Dispose();
                _shaderPipeline = null;
                _context?.Dispose();
                _context = null;
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void Detach()
    {
        lock (_gate)
        {
            if (_disposed || !_attached) return;

            try
            {
                _context?.MakeCurrent();
                _shaderPipeline?.Dispose();
            }
            finally
            {
                _context?.ReleaseCurrent();
            }
            _shaderPipeline = null;
            _context?.Dispose();
            _context = null;
            _attachedWidth = 0;
            _attachedHeight = 0;
            _attached = false;
            _logger?.LogDebug("OpenGL 渲染器已解绑渲染目标");
        }
    }

    /// <inheritdoc />
    public void Present(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            if (_disposed) return; // 竞态兜底：Dispose 已在锁外置位，丢弃本帧避免触碰已释放上下文
            if (!_attached || _context is null || _shaderPipeline is null)
            {
                throw new InvalidOperationException("渲染器未附加渲染目标，无法 Present。");
            }

            _context.MakeCurrent();
            try
            {
                switch (frame.Resource)
                {
                    case SoftwareFrameResource sw:
                        // 统一走 Shader 路径：GPU 缩放（frame→target）+ YUV→RGB / RGB 直通
                        _shaderPipeline.Present(sw, _attachedWidth, _attachedHeight, _scaleMode);
                        break;

                    case IGpuTextureResource:
                        // 零拷贝 GPU 纹理路径依赖未来 VAAPI → GLTextureResource 的 EGL interop，不在本渲染器当前范围
                        throw new NotSupportedException(
                            "OpenGL 零拷贝 GPU 纹理 Present 尚未实现（依赖未来 VAAPI EGL interop 路径）。");

                    default:
                        throw new NotSupportedException(
                            $"不支持的帧资源类型：{frame.Resource?.GetType().Name ?? "null"}。" +
                            "OpenGL 渲染器支持 SoftwareFrameResource。");
                }

                _context.SwapBuffers();
                _presentSeq++;
            }
            finally
            {
                // GL 上下文线程亲和：渲染完毕后解绑，交还上下文使 Detach/Dispose（主线程）可安全重新绑定。
                _context.ReleaseCurrent();
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_disposed || !_attached || _context is null || _shaderPipeline is null) return;

            _context.MakeCurrent();
            try
            {
                _shaderPipeline.Clear(_attachedWidth, _attachedHeight);
                _context.SwapBuffers();
            }
            finally
            {
                _context.ReleaseCurrent();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _context?.MakeCurrent();
                _shaderPipeline?.Dispose();
            }
            finally
            {
                _context?.ReleaseCurrent();
            }
            _shaderPipeline = null;
            _context?.Dispose();
            _context = null;
            _attachedWidth = 0;
            _attachedHeight = 0;
            _attached = false;
            _logger?.LogDebug("OpenGL 渲染器已释放");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 接口契约：GL 资源释放为快速同步原生调用，无 I/O 可 await。
    /// 委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

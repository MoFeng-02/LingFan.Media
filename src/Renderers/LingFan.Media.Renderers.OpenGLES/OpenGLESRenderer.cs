using System;
using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;
using LingFan.Media.Renderers.OpenGLES.Context;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.OpenGLES;

/// <summary>
/// OpenGL ES 视频渲染器（Android：libEGL + libGLESv2，ANativeWindow）。
/// </summary>
/// <remarks>
/// <para><b>跨平台边界（重要）</b>：本渲染器仅覆盖 <b>Android</b>（OpenGL ES 3.0，GLSL <c>#version 300 es</c>），
/// 作为低版本 Android 的<b>兜底上屏后端</b>——现代 Android 上屏由 Vulkan 后端覆盖。
/// 其余平台不属本渲染器范畴，分别由对应后端覆盖：
/// Windows→OpenGL 桌面渲染器 / Linux→Vulkan 或 OpenGL 桌面渲染器 / Apple→Metal 后端。</para>
/// <para><see cref="Attach"/> 对不支持的平台抛 <see cref="PlatformNotSupportedException"/> 并指明替代后端，fail-fast。</para>
/// <para><b>契约与线程模型</b>（镜像 D3D11/Vulkan/桌面 OpenGL）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，无 I/O，返回 <see cref="Task.CompletedTask"/>。</item>
/// <item><see cref="Attach"/> / <see cref="Detach"/>：同步（native 分类），UI 线程，创建/释放 GLES 上下文。</item>
/// <item><see cref="Present"/> / <see cref="Clear"/>：同步（native 分类），渲染线程，GPU 操作。</item>
/// </list>
/// <para><b>线程安全</b>：<c>_gate</c> 锁串行化 Attach/Detach/Present/Clear/Dispose，化解管线线程 Present 与
/// UI 线程 Detach 的竞态（与 D3D11/桌面 OpenGL 同源）。</para>
/// <para><b>上下文生命周期</b>：GLES 上下文在 <see cref="Attach"/> 时建立（非工厂共享 Device 单例），工厂保持薄。
/// 当前无工厂级离屏 GLES 设备上下文（GPU 纹理零拷贝属 C 线未来增强），故工厂不注册 <see cref="IGpuDeviceContext"/>。</para>
/// <para><b>异步策略</b>：公开异步方法均为真异步签名但无 I/O 阻塞——<see cref="InitializeAsync"/> 返回
/// <see cref="Task.CompletedTask"/>、<see cref="DisposeAsync"/> 委托 <see cref="Dispose"/> + <see cref="ValueTask.CompletedTask"/>，
/// 二者皆非伪异步（无 <c>Task.Run</c> 包同步、无 await 硬阻塞）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；GLES 调用经 <see cref="GlesNative"/>（零反射 [LibraryImport]，libGLESv2 直接导出）。</para>
/// </remarks>
public sealed class OpenGLESRenderer : IVideoRenderer
{
    private readonly object _gate = new();
    private readonly ILogger? _logger;
    private IGlContext? _context;
    private GlesShaderPipeline? _shaderPipeline;
    private int _attachedWidth;
    private int _attachedHeight;
    private bool _attached;
    private bool _disposed;
    private int _presentSeq;
    private AspectRatioMode _scaleMode = AspectRatioMode.Uniform;

    // 有头 GPU vsync 呈现：1 个刷新周期（60Hz ≈ 16.67ms）
    private static readonly TimeSpan PresentationLatencyValue = TimeSpan.FromMilliseconds(16.67);

    /// <summary>初始化 <see cref="OpenGLESRenderer"/> 的新实例。</summary>
    /// <param name="logger">日志器（可为 null）。</param>
    public OpenGLESRenderer(ILogger? logger = null)
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
                _logger?.LogWarning("OpenGLES 渲染器已附加渲染目标，先 Detach 再 Attach。");
                Detach();
            }

            if (target.HandleType != RenderHandleType.Pointer)
            {
                throw new NotSupportedException(
                    $"OpenGLES 渲染器仅支持 {nameof(RenderHandleType.Pointer)} 句柄类型，收到 {target.HandleType}。");
            }

            if (target.Width <= 0 || target.Height <= 0)
            {
                throw new ArgumentException(
                    $"渲染目标尺寸无效：{target.Width}x{target.Height}。", nameof(target));
            }

            // OpenGLES 渲染器仅覆盖 Android（OpenGL ES 3.0）。其余平台 fail-fast 指明替代后端。
            if (!OperatingSystem.IsAndroid())
            {
                throw new PlatformNotSupportedException(
                    "OpenGLES 渲染器仅支持 Android（OpenGL ES 3.0）。" +
                    "Windows 请使用 OpenGL 桌面渲染器；Linux 请使用 Vulkan 或 OpenGL 桌面渲染器；Apple 由 Metal 后端覆盖。");
            }

            try
            {
                if (target.NativeHandle is not IntPtr window || window == IntPtr.Zero)
                {
                    throw new ArgumentException(
                        "渲染目标的原生句柄无效——期望 IntPtr 类型的 ANativeWindow*。", nameof(target));
                }

                // Android 上屏窗口为 ANativeWindow*（opaque，经 IRenderTarget.NativeHandle 以 Pointer 传入的单一 IntPtr）。
                _context = new AndroidEglContext(window, _logger);

                _attachedWidth = target.Width;
                _attachedHeight = target.Height;
                _shaderPipeline = new GlesShaderPipeline(_logger);
                _attached = true;

                _logger?.LogInformation(
                    "[OPENGLES-ATTACH] target={TW}x{TH} 上下文={Ctx} 版本={Major}.{Minor}",
                    target.Width, target.Height, "Android EGL",
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
            _logger?.LogDebug("OpenGLES 渲染器已解绑渲染目标");
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
                        // GLES GPU 纹理零拷贝路径属 C 线未来增强（GLES 离屏设备上下文 + 共享组具备后再补）。
                        throw new NotSupportedException(
                            "OpenGLES 渲染器当前不支持 GPU 纹理零拷贝路径（IGpuTextureResource）。" +
                            "该路径属未来 C 线增强。");

                    default:
                        throw new NotSupportedException(
                            $"不支持的帧资源类型：{frame.Resource?.GetType().Name ?? "null"}。" +
                            "OpenGLES 渲染器支持 SoftwareFrameResource。");
                }

                _context.SwapBuffers();
                _presentSeq++;
            }
            finally
            {
                // GLES 上下文线程亲和：渲染完毕后解绑，交还上下文使 Detach/Dispose（主线程）可安全重新绑定。
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
            _logger?.LogDebug("OpenGLES 渲染器已释放");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 接口契约：GLES 资源释放为快速同步原生调用，无 I/O 可 await。
    /// 委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;
using LingFan.Media.Apple.Shared;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.Metal;

/// <summary>
/// Metal 视频渲染器（macOS / iOS：CAMetalLayer + CoreAnimation，MTLDevice + 运行时编译 MSL）。
/// </summary>
/// <remarks>
/// <para><b>跨平台边界（重要）</b>：本渲染器仅覆盖 <b>Apple</b>（macOS / iOS），作为 Apple 平台的上屏后端。
/// 其余平台不属本渲染器范畴，分别由对应后端覆盖：
/// Windows→D3D11 / Linux→Vulkan 或 OpenGL 桌面 / Android→OpenGL ES（Vulkan 现代 Android 上屏）。</para>
/// <para><see cref="Attach"/> 对非 Apple 平台抛 <see cref="PlatformNotSupportedException"/> 并指明替代后端，fail-fast。
/// 句柄契约：<see cref="RenderHandleType.Surface"/>（宿主提供的 CAMetalLayer*，macOS/iOS 均支持）或
/// <see cref="RenderHandleType.Pointer"/>（macOS NSView*，自动创建并挂载 CAMetalLayer）；iOS 的 Pointer(UIView) 路径因
/// CGRect 结构传参的跨架构 ABI 风险，要求宿主以 Surface 传递 CAMetalLayer。</para>
/// <para><b>契约与线程模型</b>（镜像 D3D11/Vulkan/OpenGL/GLES）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，无 I/O，返回 <see cref="Task.CompletedTask"/>。</item>
/// <item><see cref="Attach"/> / <see cref="Detach"/>：同步（native 分类），UI 线程，创建/释放 Metal 上下文。</item>
/// <item><see cref="Present"/> / <see cref="Clear"/>：同步（native 分类），渲染线程，GPU 操作。</item>
/// </list>
/// <para><b>线程安全</b>：<c>_gate</c> 锁串行化 Attach/Detach/Present/Clear/Dispose，化解管线线程 Present 与
/// UI 线程 Detach 的竞态（与 D3D11/OpenGL/GLES 同源）。Metal 命令队列线程安全，无 GLES 式上下文线程亲和，
/// 故 Present 无需 MakeCurrent/ReleaseCurrent，仅以 autorelease 池包裹每帧原生对象创建。</para>
/// <para><b>上下文生命周期</b>：Metal 上下文在 <see cref="Attach"/> 时建立（非工厂共享 Device 单例），工厂保持薄。
/// 当前无工厂级离屏设备上下文（GPU 纹理零拷贝属 C 线未来增强），故工厂不注册 <see cref="IGpuDeviceContext"/>。</para>
/// <para><b>异步策略</b>：公开异步方法均为真异步签名但无 I/O 阻塞——<see cref="InitializeAsync"/> 返回
/// <see cref="Task.CompletedTask"/>、<see cref="DisposeAsync"/> 委托 <see cref="Dispose"/> + <see cref="ValueTask.CompletedTask"/>，
/// 二者皆非伪异步（无 <c>Task.Run</c> 包同步、无 await 硬阻塞）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；Metal 调用经 <see cref="MetalNative"/>（零反射 [LibraryImport]，objc_msgSend 多固定签名重载）。</para>
/// </remarks>
public sealed class MetalRenderer : IVideoRenderer
{
    private readonly object _gate = new();
    private readonly ILogger? _logger;
    private MetalContext? _context;
    private MetalShaderPipeline? _shaderPipeline;
    private int _attachedWidth;
    private int _attachedHeight;
    private bool _attached;
    private bool _disposed;
    private int _presentSeq;
    private AspectRatioMode _scaleMode = AspectRatioMode.Uniform;

    // 有头 GPU vsync 呈现：1 个刷新周期（60Hz ≈ 16.67ms）
    private static readonly TimeSpan PresentationLatencyValue = TimeSpan.FromMilliseconds(16.67);

    /// <summary>初始化 <see cref="MetalRenderer"/> 的新实例。</summary>
    /// <param name="logger">日志器（可为 null）。</param>
    public MetalRenderer(ILogger? logger = null)
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
            if (_disposed) return;

            if (_attached)
            {
                _logger?.LogWarning("Metal 渲染器已附加渲染目标，先 Detach 再 Attach。");
                Detach();
            }

            if (target.Width <= 0 || target.Height <= 0)
            {
                throw new ArgumentException(
                    $"渲染目标尺寸无效：{target.Width}x{target.Height}。", nameof(target));
            }

            // Metal 渲染器仅覆盖 Apple（macOS / iOS）。其余平台 fail-fast 指明替代后端。
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS())
            {
                throw new PlatformNotSupportedException(
                    "Metal 渲染器仅支持 Apple 平台（macOS / iOS）。" +
                    "Windows 请使用 D3D11 渲染器；Linux 请使用 Vulkan 或 OpenGL 桌面渲染器；Android 请使用 OpenGL ES 渲染器。");
            }

            nint layer;
            if (target.HandleType == RenderHandleType.Surface)
            {
                // 宿主提供的 CAMetalLayer*（macOS/iOS 均支持）。宿主负责尺寸与挂载，本层强引用。
                if (target.NativeHandle is not nint surf || surf == nint.Zero)
                {
                    throw new ArgumentException(
                        "Surface 句柄期望 CAMetalLayer*（IntPtr）。", nameof(target));
                }
                layer = surf;
            }
            else if (target.HandleType == RenderHandleType.Pointer)
            {
                // macOS NSView*：创建 CAMetalLayer 并挂载；iOS Pointer(UIView) 因 CGRect 结构传参跨架构风险要求 Surface。
                if (!OperatingSystem.IsMacOS())
                {
                    throw new PlatformNotSupportedException(
                        "Metal 渲染器在 iOS 上须以 RenderHandleType.Surface 传递 CAMetalLayer*（宿主负责尺寸与挂载）；Pointer(UIView) 路径仅 macOS 支持。");
                }
                if (target.NativeHandle is not nint view || view == nint.Zero)
                {
                    throw new ArgumentException(
                        "Pointer 句柄期望 NSView*（IntPtr）。", nameof(target));
                }
                AppleRuntime.objc_msgSend(view, AppleRuntime.Sel("setWantsLayer:"), (byte)1);
                nint metalLayerCls = AppleRuntime.Class("CAMetalLayer");
                nint metalLayer = AppleRuntime.AllocInit(metalLayerCls);
                AppleRuntime.objc_msgSend(view, AppleRuntime.Sel("setLayer:"), metalLayer);
                layer = metalLayer;
                // 注：view 与后续构造的 MetalContext 各自 retain metalLayer；此处释放 alloc 的 +1 以平衡引用计数。
                _attachedWidth = target.Width;
                _attachedHeight = target.Height;
                try
                {
                    _context = new MetalContext(metalLayer, target.Width, target.Height, _logger);
                    _shaderPipeline = new MetalShaderPipeline(_context.Device, _context.Queue, _logger);
                    AppleRuntime.objc_release(metalLayer); // 平衡 alloc 的 +1（view 与 context 已各自 retain）
                    _attached = true;
                    _logger?.LogInformation(
                        "[METAL-ATTACH] target={TW}x{TH} 图层={Layer}",
                        target.Width, target.Height, metalLayer);
                    return;
                }
                catch
                {
                    _shaderPipeline?.Dispose();
                    _shaderPipeline = null;
                    _context?.Dispose();
                    _context = null;
                    AppleRuntime.objc_release(metalLayer);
                    throw;
                }
            }
            else
            {
                throw new NotSupportedException(
                    $"Metal 渲染器仅支持 {nameof(RenderHandleType.Surface)}（CAMetalLayer*）或 {nameof(RenderHandleType.Pointer)}（macOS NSView*），收到 {target.HandleType}。");
            }

            _attachedWidth = target.Width;
            _attachedHeight = target.Height;
            try
            {
                _context = new MetalContext(layer, target.Width, target.Height, _logger);
                _shaderPipeline = new MetalShaderPipeline(_context.Device, _context.Queue, _logger);
                _attached = true;
                _logger?.LogInformation(
                    "[METAL-ATTACH] target={TW}x{TH} 图层={Layer}",
                    target.Width, target.Height, layer);
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

            _shaderPipeline?.Dispose();
            _shaderPipeline = null;
            _context?.Dispose();
            _context = null;
            _attachedWidth = 0;
            _attachedHeight = 0;
            _attached = false;
            _logger?.LogDebug("Metal 渲染器已解绑渲染目标");
        }
    }

    /// <inheritdoc />
    public void Present(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            if (_disposed) return;
            if (!_attached || _context is null || _shaderPipeline is null)
            {
                throw new InvalidOperationException("渲染器未附加渲染目标，无法 Present。");
            }

            // 每帧原生对象（可绘制层、纹理描述符、命令缓冲等）置于 autorelease 池内，避免 NativeAOT 无池环境下的逐帧泄漏。
            nint pool = AppleRuntime.objc_autoreleasePoolPush();
            try
            {
                switch (frame.Resource)
                {
                    case SoftwareFrameResource sw:
                        var (drawable, texture, w, h) = _context.NextDrawable();
                        _shaderPipeline.Present(sw, w, h, _scaleMode, drawable, texture);
                        break;

                    case IGpuTextureResource:
                        // Metal GPU 纹理零拷贝路径属 C 线未来增强（需共享设备上下文 + IOSurface 桥接）。
                        throw new NotSupportedException(
                            "Metal 渲染器当前不支持 GPU 纹理零拷贝路径（IGpuTextureResource）。" +
                            "该路径属未来 C 线增强。");

                    default:
                        throw new NotSupportedException(
                            $"不支持的帧资源类型：{frame.Resource?.GetType().Name ?? "null"}。" +
                            "Metal 渲染器支持 SoftwareFrameResource。");
                }

                _presentSeq++;
            }
            finally
            {
                AppleRuntime.objc_autoreleasePoolPop(pool);
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_disposed || !_attached || _context is null) return;

            nint pool = AppleRuntime.objc_autoreleasePoolPush();
            try
            {
                // 呈现一帧纯黑（clear 语义：清空当前画面）。Metal 渲染目标以 drawable 纹理呈现。
                var (drawable, texture, w, h) = _context.NextDrawable();
                ClearToBlack(texture, w, h, drawable);
            }
            finally
            {
                AppleRuntime.objc_autoreleasePoolPop(pool);
            }
        }
    }

    private void ClearToBlack(nint targetTexture, int w, int h, nint drawable)
    {
        nint rpDesc = AppleRuntime.objc_msgSend(AppleRuntime.Class("MTLRenderPassDescriptor"), AppleRuntime.Sel("renderPassDescriptor"));
        nint caArr = AppleRuntime.objc_msgSend(rpDesc, AppleRuntime.Sel("colorAttachments"));
        nint ca0 = AppleRuntime.objc_msgSend(caArr, AppleRuntime.Sel("objectAtIndexedSubscript:"), (nuint)0);
        AppleRuntime.objc_msgSend(ca0, AppleRuntime.Sel("setTexture:"), targetTexture);
        AppleRuntime.objc_msgSend(ca0, AppleRuntime.Sel("setLoadAction:"), MetalConstants.LoadActionClear);
        AppleRuntime.MTLClearColor cc = new() { Red = 0, Green = 0, Blue = 0, Alpha = 1 };
        AppleRuntime.objc_msgSend(ca0, AppleRuntime.Sel("setClearColor:"), ref cc);
        AppleRuntime.objc_msgSend(ca0, AppleRuntime.Sel("setStoreAction:"), MetalConstants.StoreActionStore);

        nint cb = AppleRuntime.objc_msgSend(_context!.Queue, AppleRuntime.Sel("newCommandBuffer"));
        nint enc = AppleRuntime.objc_msgSend(cb, AppleRuntime.Sel("renderCommandEncoderWithDescriptor:"), rpDesc);
        AppleRuntime.objc_msgSend(enc, AppleRuntime.Sel("endEncoding"));
        AppleRuntime.objc_msgSend(cb, AppleRuntime.Sel("presentDrawable:"), drawable);
        AppleRuntime.objc_msgSend(cb, AppleRuntime.Sel("commit"));
        AppleRuntime.objc_release(cb); // newCommandBuffer 返回 +1；commit 后由命令队列接管，释放我们的 +1
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            _shaderPipeline?.Dispose();
            _shaderPipeline = null;
            _context?.Dispose();
            _context = null;
            _attachedWidth = 0;
            _attachedHeight = 0;
            _attached = false;
            _logger?.LogDebug("Metal 渲染器已释放");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 接口契约：Metal 资源释放为快速同步原生调用，无 I/O 可 await。
    /// 委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

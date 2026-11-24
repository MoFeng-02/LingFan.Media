using System;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LingFan.Media.Presenters.OpenGL;

/// <summary>
/// OpenGL 原生 GPU 视频呈现器。实现 <see cref="IGpuPresenter"/>，内部委托 <see cref="IVideoRenderer"/>
/// （OpenGLRenderer，桌面 GL 3.3：Windows WGL / Linux EGL）将视频帧直接合成到窗口。
/// </summary>
/// <remarks>
/// <para><b>无空域渲染</b>：本 Presenter 不含 Render——GPU 合成已由 <see cref="IVideoRenderer.Present"/>
/// 完成（SwapBuffers 到窗口）。Avalonia 控件层（字幕、UI 叠加）位于窗口合成树之上。</para>
/// <para><b>句柄来源</b>：<see cref="Initialize"/> 收到的 <see cref="IRenderTarget"/> 必须是 Pointer 类型
/// （Windows=HWND / Linux=X11 句柄）。句柄解析由 UI 层在调用前完成，本类不依赖任何 UI 框架。</para>
/// <para><b>依赖倒置</b>：仅依赖 Abstractions 的 IVideoRenderer/IVideoRendererFactory 与 Presenters 的
/// IGpuPresenter；不引用 Avalonia 或具体 GPU 类型。OpenGLRenderer 由 DI 注入的工厂创建。</para>
/// <para><b>线程安全</b>：Present（管线线程）与 Resize/Dispose/Initialize（UI 线程）通过 _rendererLock 互斥。
/// GL 上下文具线程亲和性，<c>OpenGLRenderer</c> 内部以 MakeCurrent/ReleaseCurrent 配对管理绑定。</para>
/// <para><b>生命周期（实例级，区别于 D3D11/Vulkan）</b>：<c>OpenGLRendererFactory.Create</c> 每次返回
/// <b>新实例</b>（GL 上下文按窗口建立，非工厂共享单例）。本 Presenter 持有该实例并在 Dispose 时
/// <b>Detach + Dispose</b>（实例归本 Presenter 所有，不存在共享单例被殃及的问题）。
/// Attach 失败时 Dispose 半初始化实例后置空引用，由 UI 层捕获并降级到 SkiaVideoPresenter。</para>
/// <para><b>异步策略</b>：Initialize/Present/Clear/Resize 同步——GPU 操作无 I/O 可 await；
/// Dispose 同步快速释放（GL 资源销毁为快速原生调用，非伪异步）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class OpenGLGpuPresenter : IGpuPresenter
{
    private readonly IVideoRendererFactory _rendererFactory;
    private readonly ILogger<OpenGLGpuPresenter> _logger;
    private IVideoRenderer? _renderer;
    private IRenderTarget? _target;
    private bool _disposed;
    // 保护 _renderer 的并发访问：Present（管线线程）与 Resize/Dispose（UI 线程）互斥。
    private readonly object _rendererLock = new();

    /// <summary>
    /// 初始化 <see cref="OpenGLGpuPresenter"/> 的新实例。
    /// </summary>
    /// <param name="rendererFactory">视频渲染器工厂（DI 注入 IVideoRendererFactory，通常由 AddOpenGLRenderer 注册）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public OpenGLGpuPresenter(IVideoRendererFactory rendererFactory, ILoggerFactory loggerFactory)
    {
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<OpenGLGpuPresenter>();
    }

    /// <inheritdoc/>
    public void Initialize(IRenderTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        // 中立层只接受 Pointer 渲染目标（窗口句柄已由 UI 层解析好）。
        if (target.HandleType != RenderHandleType.Pointer)
            throw new NotSupportedException(
                $"OpenGL GPU Presenter 需要 Pointer 渲染目标（携带窗口原生句柄），当前 HandleType={target.HandleType}。");

        _target = target;

        // 实例级模式：工厂每次 Create 返回新渲染器（GL 上下文按窗口建立），归本 Presenter 所有。
        // Attach 失败时须 Dispose 半初始化实例（与缓存单例后端不同——此处无共享实例可殃及）。
        var renderer = _rendererFactory.Create();
        try
        {
            renderer.Attach(target);
        }
        catch
        {
            renderer.Dispose();
            throw;
        }
        _renderer = renderer;
        _logger.LogDebug("OpenGL GPU Presenter 已附加（{Width}x{Height}）", target.Width, target.Height);
    }

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (_renderer is null)
        {
            // 渲染器未就绪：帧归还由管线 ReturnFrame 统一负责（只读借用契约）。
            // 多播通道下不得在此 Dispose，否则后续订阅方读到已释放帧（use-after-free）。
            return;
        }
        // 锁内双重检查：防止 Dispose（UI 线程）释放 GL 资源后仍在进行的 Present 触达已销毁上下文。
        lock (_rendererLock)
        {
            if (_disposed || _renderer is null)
            {
                // 同上：归还由管线负责，此处不得 Dispose。
                return;
            }
            _renderer.Present(frame);
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_rendererLock)
        {
            _renderer?.Clear();
        }
    }

    /// <inheritdoc/>
    public void Resize(int width, int height, float scale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_renderer is null || _target is null) return;
        // 保留原句柄形态重建目标（Windows=IntPtr / Linux=X11 中性句柄）。
        _target = new ResizedRenderTarget(_target, width, height, scale);
        // 锁内重建：与 Present 互斥（Detach 释放窗口 GL 上下文，Attach 用新尺寸重建）。
        lock (_rendererLock)
        {
            if (_disposed || _renderer is null) return;
            _renderer.Detach();
            _renderer.Attach(_target);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 与 Present/Resize 互斥，防止释放期间仍有 Present 访问 GL 资源。
        lock (_rendererLock)
        {
            if (_renderer is not null)
            {
                // 实例级模式：渲染器归本 Presenter 所有，Detach 后 Dispose 释放全部 GL 资源。
                _renderer.Detach();
                _renderer.Dispose();
                _renderer = null;
            }
        }
        _logger.LogDebug("OpenGL GPU Presenter 已释放");
    }
}
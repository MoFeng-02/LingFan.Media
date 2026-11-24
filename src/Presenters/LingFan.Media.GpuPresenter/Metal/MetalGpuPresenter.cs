using System;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LingFan.Media.Presenters.Metal;

/// <summary>
/// Metal 原生 GPU 视频呈现器。实现 <see cref="IGpuPresenter"/>，内部委托 <see cref="IVideoRenderer"/>
/// （MetalRenderer）将视频帧直接合成到 CAMetalLayer。
/// </summary>
/// <remarks>
/// <para><b>无空域渲染</b>：本 Presenter 不含 Render——GPU 合成已由 <see cref="IVideoRenderer.Present"/>
/// 完成（CAMetalLayer drawable present）。Avalonia 控件层（字幕、UI 叠加）位于窗口合成树之上。</para>
/// <para><b>句柄来源</b>：<see cref="Initialize"/> 接受 Surface（宿主提供的 CAMetalLayer*，macOS/iOS 均可）
/// 或 Pointer（macOS NSView*，渲染器自动创建并挂载 CAMetalLayer；iOS 因 CGRect 跨架构传参风险仅支持 Surface）。
/// 句柄解析由 UI 层在调用前完成，本类不依赖任何 UI 框架。</para>
/// <para><b>依赖倒置</b>：仅依赖 Abstractions 的 IVideoRenderer/IVideoRendererFactory 与 Presenters 的
/// IGpuPresenter；不引用 Avalonia 或具体 GPU 类型。MetalRenderer 由 DI 注入的工厂创建。</para>
/// <para><b>线程安全</b>：Present（管线线程）与 Resize/Dispose/Initialize（UI 线程）通过 _rendererLock 互斥。
/// <c>MetalRenderer</c> 内部亦以 <c>_gate</c> 锁串行化。</para>
/// <para><b>生命周期（实例级）</b>：<c>MetalRendererFactory.Create</c> 每次返回新实例，归本 Presenter 所有，
/// Dispose 时 Detach + Dispose。Attach 失败时 Dispose 半初始化实例后置空引用，由 UI 层捕获并降级。</para>
/// <para><b>平台边界</b>：仅 Apple（macOS/iOS）；其余平台由 MetalRenderer.Attach 抛
/// PlatformNotSupportedException 指明替代后端。<b>运行时验证待 Apple 真机</b>（缺设备，与渲染器同口径）。</para>
/// <para><b>异步策略</b>：Initialize/Present/Clear/Resize 同步——GPU 操作无 I/O 可 await；
/// Dispose 同步快速释放。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class MetalGpuPresenter : IGpuPresenter
{
    private readonly IVideoRendererFactory _rendererFactory;
    private readonly ILogger<MetalGpuPresenter> _logger;
    private IVideoRenderer? _renderer;
    private IRenderTarget? _target;
    private bool _disposed;
    // 保护 _renderer 的并发访问：Present（管线线程）与 Resize/Dispose（UI 线程）互斥。
    private readonly object _rendererLock = new();

    /// <summary>
    /// 初始化 <see cref="MetalGpuPresenter"/> 的新实例。
    /// </summary>
    /// <param name="rendererFactory">视频渲染器工厂（DI 注入 IVideoRendererFactory，通常由 AddMetalRenderer 注册）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public MetalGpuPresenter(IVideoRendererFactory rendererFactory, ILoggerFactory loggerFactory)
    {
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<MetalGpuPresenter>();
    }

    /// <inheritdoc/>
    public void Initialize(IRenderTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        // Metal 接受 Surface（CAMetalLayer*）或 Pointer（macOS NSView*）；具体平台守卫由渲染器执行。
        if (target.HandleType is not (RenderHandleType.Surface or RenderHandleType.Pointer))
            throw new NotSupportedException(
                $"Metal GPU Presenter 需要 Surface（CAMetalLayer*）或 Pointer（macOS NSView*）渲染目标，当前 HandleType={target.HandleType}。");

        _target = target;

        // 实例级模式：工厂每次 Create 返回新渲染器，归本 Presenter 所有。
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
        _logger.LogDebug("Metal GPU Presenter 已附加（{Width}x{Height}）", target.Width, target.Height);
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
        // 保留原句柄形态重建目标（CAMetalLayer*/NSView* 均为 IntPtr，但类型语义按原目标保留）。
        _target = new ResizedRenderTarget(_target, width, height, scale);
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
        lock (_rendererLock)
        {
            if (_renderer is not null)
            {
                // 实例级模式：渲染器归本 Presenter 所有，Detach 后 Dispose 释放全部 Metal 资源。
                _renderer.Detach();
                _renderer.Dispose();
                _renderer = null;
            }
        }
        _logger.LogDebug("Metal GPU Presenter 已释放");
    }
}
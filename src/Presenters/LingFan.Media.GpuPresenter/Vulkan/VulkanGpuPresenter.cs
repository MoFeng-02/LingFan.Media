using System;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LingFan.Media.Presenters.Vulkan;

/// <summary>
/// Vulkan 原生 GPU 视频呈现器。实现 <see cref="IGpuPresenter"/>，内部委托 <see cref="IVideoRenderer"/>
/// （VulkanRenderer）将视频帧直接合成到窗口 SwapChain。
/// </summary>
/// <remarks>
/// <para><b>无空域渲染</b>：本 Presenter 不含 Render——GPU 合成已由 <see cref="IVideoRenderer.Present"/>
/// 完成（SwapChain present 到窗口原生句柄）。Avalonia 控件层（字幕、UI 叠加）位于窗口合成树之上。</para>
/// <para><b>句柄来源</b>：<see cref="Initialize"/> 收到的 <see cref="IRenderTarget"/> 必须是 Pointer 类型
/// （Windows=HWND / Android=ANativeWindow / Linux=X11·Wayland 句柄）。句柄解析由 UI 层在调用前完成，
/// 本类不依赖任何 UI 框架。</para>
/// <para><b>依赖倒置</b>：仅依赖 Abstractions 的 IVideoRenderer/IVideoRendererFactory 与 Presenters 的
/// IGpuPresenter；不引用 Avalonia 或具体 GPU 类型。VulkanRenderer 由 DI 注入的工厂创建。</para>
/// <para><b>线程安全</b>：Present（管线线程）与 Resize/Dispose/Initialize（UI 线程）通过 _rendererLock 互斥，
/// 防止 Detach 释放 SwapChain 与 Present 使用 SwapChain 原生竞态。<c>VulkanRenderer</c> 内部亦以
/// <c>_gate</c> 锁串行化所有原生方法，是跨调用方（Core 管线 + UI Presenter）的最终序列化点。</para>
/// <para><b>生命周期（缓存单例）</b>：<see cref="IVideoRenderer"/> 为 <c>VulkanRendererFactory</c> 的缓存单例
/// （Core 管线与 UI Presenter 共享同一实例）。本 Presenter 仅负责 Attach/Detach（管理 SwapChain 与窗口的绑定），
/// <b>不 Dispose 共享单例</b>——释放交由工厂在应用关闭（或播放器释放后重建）时处理。Attach 失败时仅置空引用，
/// 由 UI 层捕获并降级到 SkiaVideoPresenter。</para>
/// <para><b>异步策略</b>：Initialize/Present/Clear/Resize 同步——GPU 操作无 I/O 可 await；
/// Dispose 同步快速释放（Detach 为快速原生调用，非伪异步）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class VulkanGpuPresenter : IGpuPresenter
{
    private readonly IVideoRendererFactory _rendererFactory;
    private readonly ILogger<VulkanGpuPresenter> _logger;
    private IVideoRenderer? _renderer;
    private IRenderTarget? _target;
    private bool _disposed;
    // 保护 _renderer 的并发访问：Present（管线线程）与 Resize/Dispose（UI 线程）互斥，
    // 防止 Detach 释放 SwapChain 与 Present 使用 SwapChain 原生竞态（VulkanRenderer 非线程安全）。
    private readonly object _rendererLock = new();

    /// <summary>
    /// 初始化 <see cref="VulkanGpuPresenter"/> 的新实例。
    /// </summary>
    /// <param name="rendererFactory">视频渲染器工厂（DI 注入 IVideoRendererFactory，通常由 AddVulkanRenderer 注册）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public VulkanGpuPresenter(IVideoRendererFactory rendererFactory, ILoggerFactory loggerFactory)
    {
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<VulkanGpuPresenter>();
    }

    /// <inheritdoc/>
    public void Initialize(IRenderTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        // 中立层只接受 Pointer 渲染目标（窗口句柄已由 UI 层解析好）。
        // 不从 Visual 追溯句柄——那会引入 UI 框架依赖，违背本项目的"与 UI 无关"设计。
        if (target.HandleType != RenderHandleType.Pointer)
            throw new NotSupportedException(
                $"Vulkan GPU Presenter 需要 Pointer 渲染目标（携带窗口原生句柄），当前 HandleType={target.HandleType}。");

        _target = target;

        // 缓存单例模式：工厂返回缓存单例渲染器（共享，非本 Presenter 私有）。Attach 失败时必须
        // 不能 Dispose 共享单例（会殃及 Core 管线持有的同一实例）。此处仅置空引用并向上抛出，
        // 由 UI 层（VideoView）捕获后降级到 SkiaVideoPresenter。
        _renderer = _rendererFactory.Create();
        try
        {
            _renderer.Attach(target);
        }
        catch
        {
            _renderer = null;
            throw;
        }
        _logger.LogDebug("Vulkan GPU Presenter 已附加（{Width}x{Height}）", target.Width, target.Height);
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
        // 锁内双重检查：防止 Dispose（UI 线程）置 _disposed 后仍在进行的 Present 触达已释放的 SwapChain。
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
        // 保留原句柄形态重建目标：Windows/Android 为 IntPtr，Linux 为 X11/Wayland 中性句柄类型，
        // 不能统一包成 GpuRenderTarget（其 ctor 仅接受 IntPtr）。
        _target = new ResizedRenderTarget(_target, width, height, scale);
        // 锁内重建 SwapChain：与 Present 互斥，防止 Detach 释放 SwapChain 与 Present 使用 SwapChain 原生竞态。
        lock (_rendererLock)
        {
            if (_disposed || _renderer is null) return;
            // 重建 SwapChain（Detach 释放 Session 级资源，Attach 用新尺寸重建）
            _renderer.Detach();
            _renderer.Attach(_target);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 与 Present/Resize 互斥，防止释放期间仍有 Present 访问 SwapChain。
        lock (_rendererLock)
        {
            if (_renderer is not null)
            {
                // 缓存单例模式：共享单例渲染器由工厂在应用关闭（或播放器释放后重建）时 Dispose。
                // 此处仅 Detach 释放本窗口的 SwapChain（Session 级资源），不 Dispose 共享实例，
                // 否则会殃及 Core 管线持有的同一渲染器实例。
                _renderer.Detach();
                _renderer = null;
            }
        }
        _logger.LogDebug("Vulkan GPU Presenter 已释放");
    }
}